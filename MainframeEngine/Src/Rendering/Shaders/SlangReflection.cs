using System.Globalization;
using System.Text.Json;

namespace MainframeEngine;

/// <summary>
/// Reads slangc's <c>-reflection-json</c> of a surface shader's probe compile (<see cref="SurfaceShaderCompiler.Probe"/>)
/// into its <see cref="SurfaceShaderLayout"/> (ADR 0237): <c>struct Material</c>'s fields with their offsets, bindings and
/// attributes, the struct's render-mode attributes, and <c>struct Varyings</c>' fields with their locations. The only
/// reader of Slang's JSON: if slangc changes it, this class and its fixtures change.
/// </summary>
public static class SlangReflection
{
    /// <summary>The layout of the shader <paramref name="source"/> describes, from its probe's reflection.</summary>
    /// <param name="warnings">Receives what is suspicious but builds (an alpha write on an opaque material).</param>
    /// <exception cref="FormatException">A parameter or varying the engine can't bind, or a default that doesn't fit its type.</exception>
    public static SurfaceShaderLayout Read(string json, SurfaceShaderSource source, ICollection<string>? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var name = source.Name;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var uniforms = new List<ShaderUniform>();
        var blockSize = 0;
        var modes = new SurfaceRenderModes();
        foreach (var parameter in Array(root, "parameters"))
        {
            var parameterName = parameter.GetProperty("name").GetString();
            if (parameterName != SurfaceShaderCompiler.MaterialParameter)
                throw new FormatException(
                    $"{name}: '{parameterName}' is a global shader parameter; material parameters go in `struct Material` (ADR 0237).");
            var material = parameter.GetProperty("type").GetProperty("elementType");
            modes = RenderModes(material, source.Kind);
            foreach (var field in material.GetProperty("fields").EnumerateArray())
                uniforms.Add(Uniform(field, source, ref blockSize));
        }

        // A material of textures only: the probe's buffer (at binding 0) holds no data, so slangc put the first texture at 0.
        // The entries declare such a material at binding 1 (SurfaceShaderLayout.MaterialBinding), which moves them all up one.
        if (uniforms.Count > 0 && uniforms.All(u => u.IsSampler))
            uniforms = [.. uniforms.Select(u => u with { Binding = u.Binding + 1 })];
        var samplers = uniforms.Count(u => u.IsSampler);
        if (source.Kind == SurfaceShaderKind.Spatial && samplers > SpatialShaderProgram.MaxSamplers)
        {
            var extra = uniforms.Where(u => u.IsSampler).ElementAt(SpatialShaderProgram.MaxSamplers);
            throw new FormatException(
                $"{name}: Material.{extra.Name}: more than {SpatialShaderProgram.MaxSamplers.ToString(CultureInfo.InvariantCulture)} textures (the mesh pipelines' sampler budget).");
        }

        foreach (var key in source.Defaults.Keys)
        {
            if (uniforms.Find(u => u.Name == key) is { IsSampler: true })
                throw new FormatException($"{name}: Material.{key}: a texture can't have a default value (use [defaultTexture(…)]).");
        }

        if (source.Kind == SurfaceShaderKind.Spatial)
        {
            if (source.WritesAlpha && modes.Alpha == AlphaMode.Opaque)
                warnings?.Add($"{name}: fragment() writes alpha, but the material is opaque; add [alpha(Alpha.Blend)] or [alpha(Alpha.Cutout)] to struct Material.");
            if (source.UsesAlphaScissor && modes.Alpha != AlphaMode.Cutout)
                warnings?.Add($"{name}: alphaScissorThreshold only cuts with [alpha(Alpha.Cutout)] on struct Material.");
        }

        var varyings = new List<SurfaceVarying>();
        foreach (var entry in Array(root, "entryPoints"))
        {
            foreach (var parameter in Array(entry, "parameters"))
            {
                if (parameter.GetProperty("name").GetString() != SurfaceShaderCompiler.VaryingsParameter)
                    continue;
                var first = SurfaceShaderCompiler.FirstVaryingLocation(source.Kind);
                foreach (var field in parameter.GetProperty("type").GetProperty("fields").EnumerateArray())
                    varyings.Add(Varying(field, first, name));
            }
        }

        return new SurfaceShaderLayout(source.Kind, uniforms, (blockSize + 15) / 16 * 16, varyings, modes, source.Vertex is not null,
            source.Fragment is not null);
    }

    private static ShaderUniform Uniform(JsonElement field, SurfaceShaderSource source, ref int blockSize)
    {
        var fieldName = field.GetProperty("name").GetString()!;
        var at = $"{source.Name}: Material.{fieldName}";
        var type = field.GetProperty("type");
        var binding = field.GetProperty("binding");
        bool color = false;
        var filter = CanvasTextureFilter.ParentNode;
        var repeat = CanvasTextureRepeat.ParentNode;
        var hint = ShaderTextureHint.White;
        if (field.TryGetProperty("userAttribs", out var attributes))
        {
            foreach (var attribute in attributes.EnumerateArray())
            {
                switch (attribute.GetProperty("name").GetString())
                {
                    case "color": color = true; break;
                    case "filter": filter = (CanvasTextureFilter)Argument(attribute); break;
                    case "repeat": repeat = (CanvasTextureRepeat)Argument(attribute); break;
                    case "defaultTexture": hint = (ShaderTextureHint)Argument(attribute); break;
                }
            }
        }

        if (type.GetProperty("kind").GetString() == "resource")
        {
            if (type.GetProperty("baseShape").GetString() != "texture2D" || !type.TryGetProperty("combined", out var combined) || !combined.GetBoolean())
                throw new FormatException($"{at}: only Sampler2D textures are supported.");
            return new ShaderUniform(fieldName, ShaderUniformType.Sampler2D, 0, 0, 0, 0, null, color, binding.GetProperty("index").GetInt32(),
                filter, repeat, hint);
        }

        var arrayLength = 0;
        var element = type;
        if (type.GetProperty("kind").GetString() == "array")
        {
            arrayLength = type.GetProperty("elementCount").GetInt32();
            element = type.GetProperty("elementType");
            if (element.GetProperty("kind").GetString() is "array" or "resource" or "struct")
                throw new FormatException($"{at}: arrays of {element.GetProperty("kind").GetString()}s are not supported.");
        }

        var uniformType = UniformType(element, at);
        var offset = binding.GetProperty("offset").GetInt32();
        var size = binding.GetProperty("size").GetInt32();
        var stride = arrayLength > 0 ? binding.GetProperty("elementStride").GetInt32() : size;
        blockSize = Math.Max(blockSize, offset + size);
        float[]? value = null;
        if (source.Defaults.TryGetValue(fieldName, out var literal))
        {
            var components = ShaderUniform.Components(uniformType);
            if (literal.Length == 1 && components > 1)
                value = [.. Enumerable.Repeat(literal[0], components)];
            else if (literal.Length == components)
                value = literal;
            else
                throw new FormatException(
                    $"{at}: the default has {literal.Length.ToString(CultureInfo.InvariantCulture)} values; its type takes {components.ToString(CultureInfo.InvariantCulture)} (or 1).");
        }

        return new ShaderUniform(fieldName, uniformType, arrayLength, offset, size, stride, value, color, -1, filter, repeat, hint);
    }

    private static ShaderUniformType UniformType(JsonElement type, string at)
    {
        var kind = type.GetProperty("kind").GetString();
        switch (kind)
        {
            case "scalar":
                return type.GetProperty("scalarType").GetString() switch
                {
                    "bool" => ShaderUniformType.Bool,
                    "int32" => ShaderUniformType.Int,
                    "uint32" => ShaderUniformType.UInt,
                    "float32" => ShaderUniformType.Float,
                    var s => throw new FormatException($"{at}: the scalar type {s} is not supported (bool, int, uint, float)."),
                };
            case "vector":
                {
                    var n = type.GetProperty("elementCount").GetInt32();
                    var scalar = type.GetProperty("elementType").GetProperty("scalarType").GetString();
                    return (scalar, n) switch
                    {
                        ("float32", 2) => ShaderUniformType.Vec2,
                        ("float32", 3) => ShaderUniformType.Vec3,
                        ("float32", 4) => ShaderUniformType.Vec4,
                        ("int32" or "uint32", 2) => ShaderUniformType.IVec2,
                        ("int32" or "uint32", 3) => ShaderUniformType.IVec3,
                        ("int32" or "uint32", 4) => ShaderUniformType.IVec4,
                        _ => throw new FormatException($"{at}: vectors of {scalar} are not supported (floatN, intN, uintN)."),
                    };
                }

            case "matrix":
                {
                    var rows = type.GetProperty("rowCount").GetInt32();
                    var columns = type.GetProperty("columnCount").GetInt32();
                    var scalar = type.GetProperty("elementType").GetProperty("scalarType").GetString();
                    return (scalar, rows, columns) switch
                    {
                        ("float32", 2, 2) => ShaderUniformType.Mat2,
                        ("float32", 3, 3) => ShaderUniformType.Mat3,
                        ("float32", 4, 4) => ShaderUniformType.Mat4,
                        _ => throw new FormatException($"{at}: only float2x2, float3x3 and float4x4 matrices are supported."),
                    };
                }

            default:
                throw new FormatException($"{at}: {kind} parameters are not supported (scalars, vectors, square float matrices, 1-D arrays of those, Sampler2D).");
        }
    }

    private static SurfaceRenderModes RenderModes(JsonElement material, SurfaceShaderKind kind)
    {
        var modes = new SurfaceRenderModes();
        if (!material.TryGetProperty("userAttribs", out var attributes))
            return modes;
        var alphaSet = false;
        foreach (var attribute in attributes.EnumerateArray())
        {
            modes = attribute.GetProperty("name").GetString() switch
            {
                "blend" => modes with { Blend = (byte)Argument(attribute) },
                "cull" => modes with { Cull = (CullMode)Argument(attribute) },
                "depthDraw" => modes with { DepthDraw = (SpatialDepthDraw)Argument(attribute) },
                "alpha" => modes with { Alpha = (AlphaMode)Argument(attribute) },
                "specular" => modes with { BlinnPhong = Argument(attribute) == 1 },
                "noDepthTest" => modes with { NoDepthTest = true },
                "unshaded" => modes with { Unshaded = true },
                "lightOnly" => modes with { LightOnly = true },
                "worldVertexCoords" => modes with { WorldVertexCoords = true },
                "noFog" => modes with { NoFog = true },
                _ => modes,
            };
            alphaSet |= attribute.GetProperty("name").GetString() == "alpha";
        }

        // A spatial blend mode other than Mix makes the surface blended (as before ADR 0237), unless the alpha mode is set.
        if (kind == SurfaceShaderKind.Spatial && !alphaSet && modes.Blend != (byte)SpatialBlendMode.Mix)
            modes = modes with { Alpha = AlphaMode.Blend };
        return modes;
    }

    private static SurfaceVarying Varying(JsonElement field, int firstLocation, string name)
    {
        var fieldName = field.GetProperty("name").GetString()!;
        var type = field.GetProperty("type");
        var binding = field.GetProperty("binding");
        var location = firstLocation + binding.GetProperty("index").GetInt32();
        var locations = binding.TryGetProperty("count", out var count) ? count.GetInt32() : 1;
        var (typeName, flat) = VaryingType(type, $"{name}: Varyings.{fieldName}");
        if (location + locations > SurfaceShaderCompiler.MaxLocations)
            throw new FormatException(
                $"{name}: Varyings.{fieldName}: too many varyings (at most {(SurfaceShaderCompiler.MaxLocations - firstLocation).ToString(CultureInfo.InvariantCulture)} locations).");
        return new SurfaceVarying(fieldName, typeName, location, locations, flat);
    }

    private static (string Type, bool Flat) VaryingType(JsonElement type, string at)
    {
        static string Scalar(string? s, string at) => s switch
        {
            "float32" => "float",
            "int32" => "int",
            "uint32" => "uint",
            _ => throw new FormatException($"{at}: varyings of type {s} are not supported (float, int, uint and their vectors, float matrices)."),
        };

        switch (type.GetProperty("kind").GetString())
        {
            case "scalar":
                {
                    var s = Scalar(type.GetProperty("scalarType").GetString(), at);
                    return (s, s != "float");
                }

            case "vector":
                {
                    var s = Scalar(type.GetProperty("elementType").GetProperty("scalarType").GetString(), at);
                    return (s + type.GetProperty("elementCount").GetInt32().ToString(CultureInfo.InvariantCulture), s != "float");
                }

            case "matrix":
                {
                    var s = Scalar(type.GetProperty("elementType").GetProperty("scalarType").GetString(), at);
                    if (s != "float")
                        throw new FormatException($"{at}: only float matrices can be varyings.");
                    return (string.Create(CultureInfo.InvariantCulture,
                        $"float{type.GetProperty("rowCount").GetInt32()}x{type.GetProperty("columnCount").GetInt32()}"), false);
                }

            default:
                throw new FormatException($"{at}: {type.GetProperty("kind").GetString()} varyings are not supported (float, int, uint and their vectors, float matrices).");
        }
    }

    // slangc leaves out an empty array (an entry point without parameters).
    private static JsonElement.ArrayEnumerator Array(JsonElement element, string name) =>
        element.TryGetProperty(name, out var array) ? array.EnumerateArray() : default;

    private static int Argument(JsonElement attribute) => attribute.GetProperty("arguments")[0].GetInt32();
}

using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MainframeEngine;

/// <summary>One field of a surface shader's <c>struct Varyings</c>: its stage-interface location(s) and Slang type.</summary>
public sealed record SurfaceVarying(string Name, string Type, int Location, int Locations, bool Flat);

/// <summary>
/// A surface shader's render modes (the attributes on its <c>struct Material</c>, ADR 0237). <see cref="Blend"/> is a
/// <see cref="SpatialBlendMode"/> or a <see cref="CanvasBlendMode"/>, by kind.
/// </summary>
public sealed record SurfaceRenderModes(
    byte Blend = 0,
    CullMode Cull = CullMode.Back,
    SpatialDepthDraw DepthDraw = SpatialDepthDraw.Opaque,
    AlphaMode Alpha = AlphaMode.Opaque,
    bool NoDepthTest = false,
    bool Unshaded = false,
    bool LightOnly = false,
    bool WorldVertexCoords = false,
    bool NoFog = false,
    bool BlinnPhong = false);

/// <summary>
/// Everything the engine needs about a built surface shader without slangc: its parameters as slangc laid them out, its
/// render modes, its varyings and which functions it has. mf-shaders writes it next to the SPIR-V as
/// <c>x.slang.layout.json</c> (<see cref="Write"/>) and <see cref="Shader.Load"/> reads it (<see cref="Read"/>).
/// </summary>
public sealed record SurfaceShaderLayout(
    SurfaceShaderKind Kind,
    IReadOnlyList<ShaderUniform> Uniforms,
    int UniformBlockSize,
    IReadOnlyList<SurfaceVarying> Varyings,
    SurfaceRenderModes Modes,
    bool HasVertexFunction,
    bool HasFragmentFunction)
{
    /// <summary>The extension of the layout file next to a shader (<c>x.slang.layout.json</c>).</summary>
    public const string Extension = ".layout.json";

    /// <summary>
    /// The binding the material's constant buffer is declared at: 0, or 1 when it holds only textures (Slang then drops the
    /// buffer and would put the first texture at its binding; the engine's layout keeps textures at 1…n either way).
    /// </summary>
    public int MaterialBinding => Uniforms.Count > 0 && Uniforms.All(u => u.IsSampler) ? 1 : 0;

    /// <summary>The spatial program (meshes) of a <see cref="SurfaceShaderKind.Spatial"/> layout.</summary>
    public SpatialShaderProgram ToSpatial(string name) => new(name, Uniforms, UniformBlockSize, HasVertexFunction, HasFragmentFunction,
        Modes.Alpha, Modes.Cull, (SpatialBlendMode)Modes.Blend, Modes.DepthDraw, Modes.NoDepthTest, Modes.Unshaded, Modes.BlinnPhong,
        Modes.WorldVertexCoords, Modes.NoFog);

    /// <summary>The canvas program (2D items) of a <see cref="SurfaceShaderKind.Canvas"/> layout.</summary>
    public CanvasShaderProgram ToCanvas() =>
        new(Uniforms, UniformBlockSize, HasVertexFunction, HasFragmentFunction, (CanvasBlendMode)Modes.Blend, Modes.Unshaded, Modes.LightOnly);

    /// <summary>The layout as JSON (stable member order, so a rebuild of an unchanged shader writes the same bytes).</summary>
    public string Write()
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("format", 1);
            w.WriteString("kind", Kind == SurfaceShaderKind.Spatial ? "spatial" : "canvas");
            w.WriteBoolean("vertex", HasVertexFunction);
            w.WriteBoolean("fragment", HasFragmentFunction);
            w.WriteNumber("blockSize", UniformBlockSize);
            w.WriteStartObject("modes");
            w.WriteNumber("blend", Modes.Blend);
            w.WriteString("cull", Modes.Cull.ToString());
            w.WriteString("depthDraw", Modes.DepthDraw.ToString());
            w.WriteString("alpha", Modes.Alpha.ToString());
            w.WriteBoolean("noDepthTest", Modes.NoDepthTest);
            w.WriteBoolean("unshaded", Modes.Unshaded);
            w.WriteBoolean("lightOnly", Modes.LightOnly);
            w.WriteBoolean("worldVertexCoords", Modes.WorldVertexCoords);
            w.WriteBoolean("noFog", Modes.NoFog);
            w.WriteBoolean("blinnPhong", Modes.BlinnPhong);
            w.WriteEndObject();
            w.WriteStartArray("uniforms");
            foreach (var u in Uniforms)
            {
                w.WriteStartObject();
                w.WriteString("name", u.Name);
                w.WriteString("type", u.Type.ToString());
                w.WriteNumber("arrayLength", u.ArrayLength);
                w.WriteNumber("offset", u.Offset);
                w.WriteNumber("size", u.Size);
                w.WriteNumber("stride", u.Stride);
                if (u.Default is { } d)
                {
                    w.WriteStartArray("default");
                    foreach (var v in d)
                        w.WriteNumberValue(v);
                    w.WriteEndArray();
                }

                w.WriteBoolean("color", u.SourceColor);
                w.WriteNumber("binding", u.Binding);
                w.WriteString("filter", u.Filter.ToString());
                w.WriteString("repeat", u.Repeat.ToString());
                w.WriteString("defaultTexture", u.TextureHint.ToString());
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteStartArray("varyings");
            foreach (var v in Varyings)
            {
                w.WriteStartObject();
                w.WriteString("name", v.Name);
                w.WriteString("type", v.Type);
                w.WriteNumber("location", v.Location);
                w.WriteNumber("locations", v.Locations);
                w.WriteBoolean("flat", v.Flat);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray()).ReplaceLineEndings("\n") + "\n";
    }

    /// <summary>Reads a layout <see cref="Write"/> wrote.</summary>
    /// <exception cref="FormatException">Not a layout file of a format this engine reads.</exception>
    public static SurfaceShaderLayout Read(string json, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.GetProperty("format").GetInt32() != 1)
                throw new FormatException($"{name}: layout format {root.GetProperty("format").GetInt32().ToString(CultureInfo.InvariantCulture)} is not supported; rebuild the shader.");
            var modes = root.GetProperty("modes");
            var uniforms = new List<ShaderUniform>();
            foreach (var u in root.GetProperty("uniforms").EnumerateArray())
            {
                float[]? value = u.TryGetProperty("default", out var d) ? [.. d.EnumerateArray().Select(e => e.GetSingle())] : null;
                uniforms.Add(new ShaderUniform(
                    u.GetProperty("name").GetString()!,
                    Enum.Parse<ShaderUniformType>(u.GetProperty("type").GetString()!),
                    u.GetProperty("arrayLength").GetInt32(),
                    u.GetProperty("offset").GetInt32(),
                    u.GetProperty("size").GetInt32(),
                    u.GetProperty("stride").GetInt32(),
                    value,
                    u.GetProperty("color").GetBoolean(),
                    u.GetProperty("binding").GetInt32(),
                    Enum.Parse<CanvasTextureFilter>(u.GetProperty("filter").GetString()!),
                    Enum.Parse<CanvasTextureRepeat>(u.GetProperty("repeat").GetString()!),
                    Enum.Parse<ShaderTextureHint>(u.GetProperty("defaultTexture").GetString()!)));
            }

            var varyings = root.GetProperty("varyings").EnumerateArray().Select(v => new SurfaceVarying(
                v.GetProperty("name").GetString()!, v.GetProperty("type").GetString()!, v.GetProperty("location").GetInt32(),
                v.GetProperty("locations").GetInt32(), v.GetProperty("flat").GetBoolean())).ToList();
            return new SurfaceShaderLayout(
                root.GetProperty("kind").GetString() == "spatial" ? SurfaceShaderKind.Spatial : SurfaceShaderKind.Canvas,
                uniforms,
                root.GetProperty("blockSize").GetInt32(),
                varyings,
                new SurfaceRenderModes(
                    modes.GetProperty("blend").GetByte(),
                    Enum.Parse<CullMode>(modes.GetProperty("cull").GetString()!),
                    Enum.Parse<SpatialDepthDraw>(modes.GetProperty("depthDraw").GetString()!),
                    Enum.Parse<AlphaMode>(modes.GetProperty("alpha").GetString()!),
                    modes.GetProperty("noDepthTest").GetBoolean(),
                    modes.GetProperty("unshaded").GetBoolean(),
                    modes.GetProperty("lightOnly").GetBoolean(),
                    modes.GetProperty("worldVertexCoords").GetBoolean(),
                    modes.GetProperty("noFog").GetBoolean(),
                    modes.GetProperty("blinnPhong").GetBoolean()),
                root.GetProperty("vertex").GetBoolean(),
                root.GetProperty("fragment").GetBoolean());
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        {
            throw new FormatException($"{name}: the layout file is not readable ({e.Message}); rebuild the shader.", e);
        }
    }
}

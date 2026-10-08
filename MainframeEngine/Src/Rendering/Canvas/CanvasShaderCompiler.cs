using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MainframeEngine;

/// <summary>The scalar/vector types a canvas shader uniform can have.</summary>
#pragma warning disable CA1720 // Godot's shader type names
public enum ShaderUniformType : byte
{
    Bool,
    Int,
    UInt,
    Float,
    Vec2,
    Vec3,
    Vec4,
    IVec2,
    IVec3,
    IVec4,
    Mat2,
    Mat3,
    Mat4,
    Sampler2D,
}
#pragma warning restore CA1720

/// <summary>One uniform of a canvas shader: its std140 place in the material block, or its sampler binding.</summary>
public sealed record ShaderUniform(
    string Name,
    ShaderUniformType Type,
    int ArrayLength,
    int Offset,
    int Size,
    int Stride,
    float[]? Default,
    bool SourceColor,
    int Binding,
    CanvasTextureFilter Filter,
    CanvasTextureRepeat Repeat)
{
    public bool IsSampler => Type == ShaderUniformType.Sampler2D;
}

/// <summary>A translated canvas shader: Slang for both stages and the material interface.</summary>
public sealed record CanvasShaderProgram(
    string VertexSource,
    string FragmentSource,
    IReadOnlyList<ShaderUniform> Uniforms,
    int UniformBlockSize,
    bool HasVertexFunction,
    bool HasFragmentFunction,
    CanvasBlendMode BlendMode,
    bool Unshaded,
    bool LightOnly)
{
    public int SamplerCount => Uniforms.Count(u => u.IsSampler);
}

/// <summary>
/// Translates Godot 4.7 <c>shader_type canvas_item</c> shaders to Slang for the canvas renderer (ADR 0113, ADR 0144): the
/// shader keeps Godot's language — <c>uniform</c>s with hints and defaults, <c>varying</c>s, <c>render_mode</c>,
/// <c>vertex()</c>/<c>fragment()</c> and Godot's built-ins (VERTEX, UV, COLOR, TEXTURE, TEXTURE_PIXEL_SIZE, TIME,
/// FRAGCOORD, SCREEN_UV, SCREEN_PIXEL_SIZE, MODEL_MATRIX, CANVAS_MATRIX, SCREEN_MATRIX, PI, TAU, E) — and the stages are
/// wrapped in a Slang template mirroring Godot's canvas.glsl (compiled with -allow-glsl, so the user code keeps GLSL's
/// syntax and meaning). Non-sampler uniforms become one std140 block (set 1, binding
/// 0) in declaration order; samplers follow at bindings 1…n. Lights (<c>light()</c>) are not supported yet.
/// </summary>
public static partial class CanvasShaderCompiler
{
    /// <summary>Translates <paramref name="source"/> (the <c>.gdshader</c> text).</summary>
    /// <exception cref="FormatException">Not a canvas_item shader, or a construct the translator does not handle.</exception>
    public static CanvasShaderProgram Translate(string source, string name = "shader")
    {
        ArgumentNullException.ThrowIfNull(source);
        var code = StripComments(source);
        var statements = SplitTopLevel(code);
        string? shaderType = null;
        var blend = CanvasBlendMode.Mix;
        bool unshaded = false, lightOnly = false;
        var uniforms = new List<ParsedUniform>();
        var varyings = new List<(string Decl, string Type, string Name, bool Flat)>();
        var globals = new StringBuilder();
        string? vertexFn = null, fragmentFn = null;
        var helpers = new StringBuilder();

        foreach (var statement in statements)
        {
            var s = statement.Trim();
            if (s.Length == 0)
                continue;
            if (s.StartsWith("shader_type", StringComparison.Ordinal))
            {
                shaderType = s["shader_type".Length..].Trim().TrimEnd(';').Trim();
                continue;
            }

            if (s.StartsWith("render_mode", StringComparison.Ordinal))
            {
                foreach (var mode in s["render_mode".Length..].TrimEnd(';').Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    switch (mode)
                    {
                        case "blend_mix": blend = CanvasBlendMode.Mix; break;
                        case "blend_add": blend = CanvasBlendMode.Add; break;
                        case "blend_sub": blend = CanvasBlendMode.Sub; break;
                        case "blend_mul": blend = CanvasBlendMode.Mul; break;
                        case "blend_premul_alpha": blend = CanvasBlendMode.PremultAlpha; break;
                        case "blend_disabled": blend = CanvasBlendMode.Disabled; break;
                        case "unshaded": unshaded = true; break;
                        case "light_only": lightOnly = true; break;
                        default: throw new FormatException($"{name}: render_mode '{mode}' is not supported.");
                    }
                }

                continue;
            }

            if (s.StartsWith("uniform ", StringComparison.Ordinal))
            {
                uniforms.Add(ParseUniform(s, name));
                continue;
            }

            if (s.StartsWith("varying ", StringComparison.Ordinal))
            {
                var m = VaryingRegex().Match(s);
                if (!m.Success)
                    throw new FormatException($"{name}: cannot read varying '{s}'.");
                varyings.Add((s, m.Groups["type"].Value, m.Groups["name"].Value, m.Groups["flat"].Success || m.Groups["type"].Value.StartsWith('i') || m.Groups["type"].Value.StartsWith('u')));
                continue;
            }

            if (s.EndsWith('}'))
            {
                var fm = FunctionRegex().Match(s);
                if (!fm.Success)
                    throw new FormatException($"{name}: cannot read the top-level block '{Head(s)}'.");
                switch (fm.Groups["name"].Value)
                {
                    case "vertex": vertexFn = s; break;
                    case "fragment": fragmentFn = s; break;
                    case "light": throw new FormatException($"{name}: light() is not supported yet.");
                    default: helpers.Append(s).Append('\n'); break;
                }

                continue;
            }

            // Constants, structs and other global declarations pass through.
            globals.Append(s).Append(";\n");
        }

        if (shaderType != "canvas_item")
            throw new FormatException($"{name}: only shader_type canvas_item is supported (found '{shaderType ?? "none"}').");

        var laidOut = Layout(uniforms, out var blockSize);
        // Slang (ADR 0144) compiled with -allow-glsl: the user code keeps Godot's GLSL-like language (vec4, M * v,
        // texture()) and its meaning; the template around it is Slang. Built-ins and varyings are static globals the
        // user functions read and write; the entry points copy them to and from the stage interface.
        var common = new StringBuilder();
        common.Append("// Translated from a Godot canvas_item shader by CanvasShaderCompiler. Do not edit: edit the .gdshader.\n");
        common.Append("#include \"canvas.slang\"\n");
        common.Append("#define PI 3.1415926535897932384626433833\n#define TAU 6.2831853071795864769252867666\n#define E 2.7182818284590452353602874714\n");
        if (laidOut.Any(u => !u.IsSampler))
        {
            common.Append("[[vk::binding(0, 1)]] cbuffer MaterialUniforms\n{\n");
            foreach (var u in laidOut.Where(u => !u.IsSampler))
                common.Append("    ").Append(ShaderTypeName(u.Type)).Append(' ').Append(u.Name).Append(u.ArrayLength > 0 ? string.Create(CultureInfo.InvariantCulture, $"[{u.ArrayLength}]") : "").Append(";\n");
            common.Append("};\n");
        }

        foreach (var u in laidOut.Where(u => u.IsSampler))
            common.Append(CultureInfo.InvariantCulture, $"[[vk::binding({u.Binding}, 1)]] Sampler2D {u.Name};\n");
        common.Append(globals);
        foreach (var v in varyings)
            common.Append("static ").Append(v.Type).Append(' ').Append(v.Name).Append(";\n");

        // The varyings' fields of a stage interface struct (locations 2…).
        var varyingFields = new StringBuilder();
        for (var i = 0; i < varyings.Count; i++)
            varyingFields.Append(CultureInfo.InvariantCulture, $"    [[vk::location({2 + i})]] {(varyings[i].Flat ? "nointerpolation " : "")}{varyings[i].Type} {varyings[i].Name};\n");

        // ── Vertex stage ──
        var vs = new StringBuilder(common.ToString());
        vs.Append("static vec2 VERTEX;\nstatic vec2 UV;\nstatic vec4 COLOR;\nstatic mat4 MODEL_MATRIX;\nstatic mat4 CANVAS_MATRIX;\nstatic mat4 SCREEN_MATRIX;\nstatic float TIME;\nstatic vec2 TEXTURE_PIXEL_SIZE;\nstatic float POINT_SIZE;\nstatic vec4 INSTANCE_CUSTOM;\nstatic int INSTANCE_ID;\nstatic int VERTEX_ID;\n");
        vs.Append(helpers);
        if (vertexFn is not null)
            vs.Append(vertexFn).Append('\n');
        vs.Append("struct VsIn\n{\n    [[vk::location(0)]] vec2 inPosition;\n    [[vk::location(1)]] vec2 inUv;\n    [[vk::location(2)]] vec4 inColor;\n};\n");
        vs.Append("struct VsOut\n{\n    float4 position : SV_Position;\n    [[vk::location(0)]] vec4 uvVertexInterp;\n    [[vk::location(1)]] vec4 colorInterp;\n");
        vs.Append(varyingFields).Append("};\n");
        vs.Append("""
            [shader("vertex")]
            VsOut main(VsIn input, uint vertexId : SV_VulkanVertexID, uint instanceId : SV_VulkanInstanceID)
            {
                VERTEX = input.inPosition;
                UV = input.inUv;
                COLOR = input.inColor;
                MODEL_MATRIX = canvas_model_matrix();
                CANVAS_MATRIX = canvas_canvas_matrix();
                SCREEN_MATRIX = canvas_screen_matrix();
                TIME = canvas_pc.canvasOrigin.z;
                TEXTURE_PIXEL_SIZE = canvas_pc.modelOrigin.zw;
                POINT_SIZE = 1.0;
                INSTANCE_CUSTOM = vec4(0.0);
                INSTANCE_ID = int(instanceId);
                VERTEX_ID = int(vertexId);

            """);
        if (vertexFn is not null)
            vs.Append("    vertex();\n");
        vs.Append("""
                vec2 vertex = (MODEL_MATRIX * vec4(VERTEX, 0.0, 1.0)).xy;
                VsOut o;
                o.colorInterp = COLOR;
                vertex = (CANVAS_MATRIX * vec4(vertex, 0.0, 1.0)).xy;
                o.uvVertexInterp = vec4(UV, vertex);
                o.position = SCREEN_MATRIX * vec4(vertex, 0.0, 1.0);

            """);
        foreach (var v in varyings)
            vs.Append("    o.").Append(v.Name).Append(" = ").Append(v.Name).Append(";\n");
        vs.Append("    return o;\n}\n");

        // ── Fragment stage ──
        var fs = new StringBuilder(common.ToString());
        fs.Append("[[vk::binding(0, 0)]] Sampler2D colorTexture;\n#define TEXTURE colorTexture\n");
        fs.Append("#define CANVAS_LIGHT_SET 2\n#include \"canvas_lights.slang\"\n");
        fs.Append("static vec4 COLOR;\nstatic vec2 UV;\nstatic vec2 VERTEX;\nstatic vec4 FRAGCOORD;\nstatic vec2 SCREEN_UV;\nstatic vec2 SCREEN_PIXEL_SIZE;\nstatic vec2 TEXTURE_PIXEL_SIZE;\nstatic float TIME;\nstatic vec2 POINT_COORD;\nstatic bool AT_LIGHT_PASS;\nstatic vec3 NORMAL;\nstatic vec3 NORMAL_MAP;\nstatic float NORMAL_MAP_DEPTH;\nstatic vec3 LIGHT_VERTEX;\nstatic vec2 SHADOW_VERTEX;\n");
        fs.Append(helpers);
        if (fragmentFn is not null)
            fs.Append(fragmentFn).Append('\n');
        fs.Append("struct FsIn\n{\n    [[vk::location(0)]] vec4 uvVertexInterp;\n    [[vk::location(1)]] vec4 colorInterp;\n");
        fs.Append(varyingFields).Append("};\n");
        fs.Append("""
            [shader("fragment")]
            float4 main(FsIn input, float4 fragCoord : SV_Position) : SV_Target0
            {

            """);
        foreach (var v in varyings)
            fs.Append("    ").Append(v.Name).Append(" = input.").Append(v.Name).Append(";\n");
        fs.Append("""
                UV = input.uvVertexInterp.xy;
                VERTEX = input.uvVertexInterp.zw;
                FRAGCOORD = fragCoord;
                SCREEN_PIXEL_SIZE = vec2(canvas_pc.screen.x, canvas_pc.screen.y) * 0.5;
                SCREEN_UV = fragCoord.xy * SCREEN_PIXEL_SIZE;
                TEXTURE_PIXEL_SIZE = canvas_pc.modelOrigin.zw;
                TIME = canvas_pc.canvasOrigin.z;
                POINT_COORD = vec2(0.5);
                AT_LIGHT_PASS = false;
                NORMAL = vec3(0.0, 0.0, 1.0);
                NORMAL_MAP = vec3(0.0, 0.0, 1.0);
                NORMAL_MAP_DEPTH = 1.0;
                LIGHT_VERTEX = vec3(VERTEX, 0.0);
                SHADOW_VERTEX = VERTEX;
                COLOR = input.colorInterp * texture(colorTexture, UV);

            """);
        if (fragmentFn is not null)
            fs.Append("    fragment();\n");
        fs.Append("""
                vec4 baseColor = COLOR;
                if ((canvas_flags() & CANVAS_FLAG_UNSHADED) == 0u)
                {
                    COLOR *= canvas_pc.canvasModulation;
                    canvas_apply_lights(COLOR, baseColor, VERTEX);
                }
                if ((canvas_flags() & CANVAS_FLAG_PREMULTIPLY) != 0u)
                    COLOR.rgb *= COLOR.a;
                return COLOR;
            }

            """);

        return new CanvasShaderProgram(vs.ToString(), fs.ToString(), laidOut, blockSize, vertexFn is not null, fragmentFn is not null,
            blend, unshaded, lightOnly);
    }

    private sealed record ParsedUniform(string Name, ShaderUniformType Type, int ArrayLength, float[]? Default, bool SourceColor,
        CanvasTextureFilter Filter, CanvasTextureRepeat Repeat);

    private static ParsedUniform ParseUniform(string s, string name)
    {
        var m = UniformRegex().Match(s);
        if (!m.Success)
            throw new FormatException($"{name}: cannot read uniform '{s}'.");
        var type = ParseType(m.Groups["type"].Value, name);
        var array = m.Groups["array"].Success ? int.Parse(m.Groups["array"].Value, CultureInfo.InvariantCulture) : 0;
        var hints = m.Groups["hints"].Success ? m.Groups["hints"].Value : "";
        var sourceColor = false;
        var filter = CanvasTextureFilter.ParentNode;
        var repeat = CanvasTextureRepeat.ParentNode;
        foreach (var hint in SplitHints(hints))
        {
            switch (hint)
            {
                case "source_color": sourceColor = true; break;
                case "filter_nearest": filter = CanvasTextureFilter.Nearest; break;
                case "filter_linear": filter = CanvasTextureFilter.Linear; break;
                case "repeat_enable": repeat = CanvasTextureRepeat.Enabled; break;
                case "repeat_disable": repeat = CanvasTextureRepeat.Disabled; break;
            }
        }

        float[]? value = null;
        if (m.Groups["default"].Success)
            value = ParseDefault(m.Groups["default"].Value.Trim(), type, name);
        return new ParsedUniform(m.Groups["name"].Value, type, array, value, sourceColor, filter, repeat);
    }

    private static IEnumerable<string> SplitHints(string hints)
    {
        // hint_range(0.0, 1.0) contains commas: split at commas outside parentheses.
        var depth = 0;
        var start = 0;
        for (var i = 0; i < hints.Length; i++)
        {
            if (hints[i] == '(') depth++;
            else if (hints[i] == ')') depth--;
            else if (hints[i] == ',' && depth == 0)
            {
                yield return Strip(hints[start..i]);
                start = i + 1;
            }
        }

        if (start < hints.Length)
            yield return Strip(hints[start..]);

        static string Strip(string h)
        {
            h = h.Trim();
            var p = h.IndexOf('(', StringComparison.Ordinal);
            return p >= 0 ? h[..p].Trim() : h;
        }
    }

    private static float[] ParseDefault(string text, ShaderUniformType type, string name)
    {
        var components = Components(type);
        if (text is "true" or "false")
            return [text == "true" ? 1f : 0f];
        var m = ConstructorRegex().Match(text);
        if (m.Success)
        {
            var args = m.Groups["args"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(a => float.Parse(a.TrimEnd('f'), CultureInfo.InvariantCulture)).ToArray();
            if (args.Length == 1 && components > 1)
                return Enumerable.Repeat(args[0], components).ToArray();
            if (args.Length != components)
                throw new FormatException($"{name}: default '{text}' has {args.Length} components, expected {components}.");
            return args;
        }

        if (float.TryParse(text.TrimEnd('f'), NumberStyles.Float, CultureInfo.InvariantCulture, out var scalar))
            return [scalar];
        throw new FormatException($"{name}: cannot read the default value '{text}'.");
    }

    private static List<ShaderUniform> Layout(List<ParsedUniform> uniforms, out int blockSize)
    {
        var result = new List<ShaderUniform>(uniforms.Count);
        var offset = 0;
        foreach (var u in uniforms.Where(u => u.Type != ShaderUniformType.Sampler2D))
        {
            var (size, align) = Std140(u.Type);
            int stride;
            if (u.ArrayLength > 0)
            {
                // std140: array elements are aligned and strided to 16 bytes.
                stride = RoundUp(size, 16);
                align = Math.Max(align, 16);
                offset = RoundUp(offset, align);
                result.Add(new ShaderUniform(u.Name, u.Type, u.ArrayLength, offset, stride * u.ArrayLength, stride, u.Default, u.SourceColor, -1, u.Filter, u.Repeat));
                offset += stride * u.ArrayLength;
            }
            else
            {
                stride = size;
                offset = RoundUp(offset, align);
                result.Add(new ShaderUniform(u.Name, u.Type, 0, offset, size, stride, u.Default, u.SourceColor, -1, u.Filter, u.Repeat));
                offset += size;
            }
        }

        blockSize = RoundUp(offset, 16);
        var binding = 1;
        foreach (var u in uniforms.Where(u => u.Type == ShaderUniformType.Sampler2D))
            result.Add(new ShaderUniform(u.Name, u.Type, 0, 0, 0, 0, null, u.SourceColor, binding++, u.Filter, u.Repeat));
        return result;
    }

    private static (int Size, int Align) Std140(ShaderUniformType type) => type switch
    {
        ShaderUniformType.Bool or ShaderUniformType.Int or ShaderUniformType.UInt or ShaderUniformType.Float => (4, 4),
        ShaderUniformType.Vec2 or ShaderUniformType.IVec2 => (8, 8),
        ShaderUniformType.Vec3 or ShaderUniformType.IVec3 => (12, 16),
        ShaderUniformType.Vec4 or ShaderUniformType.IVec4 => (16, 16),
        ShaderUniformType.Mat2 => (32, 16),
        ShaderUniformType.Mat3 => (48, 16),
        ShaderUniformType.Mat4 => (64, 16),
        _ => (0, 4),
    };

    /// <summary>Number of float/int components (matrices count columns × 4 in std140).</summary>
    public static int Components(ShaderUniformType type) => type switch
    {
        ShaderUniformType.Vec2 or ShaderUniformType.IVec2 => 2,
        ShaderUniformType.Vec3 or ShaderUniformType.IVec3 => 3,
        ShaderUniformType.Vec4 or ShaderUniformType.IVec4 => 4,
        ShaderUniformType.Mat2 => 4,
        ShaderUniformType.Mat3 => 9,
        ShaderUniformType.Mat4 => 16,
        _ => 1,
    };

    private static int RoundUp(int value, int alignment) => (value + alignment - 1) / alignment * alignment;

    private static ShaderUniformType ParseType(string type, string name) => type switch
    {
        "bool" => ShaderUniformType.Bool,
        "int" => ShaderUniformType.Int,
        "uint" => ShaderUniformType.UInt,
        "float" => ShaderUniformType.Float,
        "vec2" => ShaderUniformType.Vec2,
        "vec3" => ShaderUniformType.Vec3,
        "vec4" => ShaderUniformType.Vec4,
        "ivec2" => ShaderUniformType.IVec2,
        "ivec3" => ShaderUniformType.IVec3,
        "ivec4" => ShaderUniformType.IVec4,
        "mat2" => ShaderUniformType.Mat2,
        "mat3" => ShaderUniformType.Mat3,
        "mat4" => ShaderUniformType.Mat4,
        "sampler2D" => ShaderUniformType.Sampler2D,
        _ => throw new FormatException($"{name}: uniform type '{type}' is not supported."),
    };

    // Godot's (GLSL) type names; the translated shader is compiled with -allow-glsl, which accepts them.
    private static string ShaderTypeName(ShaderUniformType type) => type switch
    {
        ShaderUniformType.Bool => "bool",
        ShaderUniformType.Int => "int",
        ShaderUniformType.UInt => "uint",
        ShaderUniformType.Float => "float",
        ShaderUniformType.Vec2 => "vec2",
        ShaderUniformType.Vec3 => "vec3",
        ShaderUniformType.Vec4 => "vec4",
        ShaderUniformType.IVec2 => "ivec2",
        ShaderUniformType.IVec3 => "ivec3",
        ShaderUniformType.IVec4 => "ivec4",
        ShaderUniformType.Mat2 => "mat2",
        ShaderUniformType.Mat3 => "mat3",
        ShaderUniformType.Mat4 => "mat4",
        _ => "sampler2D",
    };

    // Comments become spaces/newlines so line numbers in errors stay meaningful.
    private static string StripComments(string source)
    {
        var sb = new StringBuilder(source.Length);
        for (var i = 0; i < source.Length; i++)
        {
            if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n')
                    i++;
                if (i < source.Length)
                    sb.Append('\n');
            }
            else if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/'))
                {
                    if (source[i] == '\n')
                        sb.Append('\n');
                    i++;
                }

                i++;
            }
            else
            {
                sb.Append(source[i]);
            }
        }

        return sb.ToString();
    }

    // Top-level statements: ';'-terminated declarations and '{…}' function bodies (a trailing ';' after '}' is dropped).
    private static List<string> SplitTopLevel(string code)
    {
        var result = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < code.Length; i++)
        {
            var c = code[i];
            if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    result.Add(code[start..(i + 1)]);
                    start = i + 1;
                }
            }
            else if (c == ';' && depth == 0)
            {
                result.Add(code[start..i]);
                start = i + 1;
            }
        }

        if (code[start..].Trim().Length > 0)
            result.Add(code[start..]);
        return result;
    }

    private static string Head(string s) => s.Length <= 60 ? s : s[..60] + "…";

    [GeneratedRegex(@"^uniform\s+(?<type>\w+)\s+(?<name>\w+)\s*(\[\s*(?<array>\d+)\s*\])?\s*(:\s*(?<hints>[^=]+?))?\s*(=\s*(?<default>.+))?$", RegexOptions.Singleline)]
    private static partial Regex UniformRegex();

    [GeneratedRegex(@"^varying\s+(?<flat>flat\s+)?(?:(?:lowp|mediump|highp)\s+)?(?<type>\w+)\s+(?<name>\w+)$")]
    private static partial Regex VaryingRegex();

    [GeneratedRegex(@"^\s*\w+\s+(?<name>\w+)\s*\([^)]*\)\s*\{", RegexOptions.Singleline)]
    private static partial Regex FunctionRegex();

    [GeneratedRegex(@"^\w+\s*\((?<args>[^)]*)\)$")]
    private static partial Regex ConstructorRegex();
}

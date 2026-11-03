using System.Numerics;
using System.Runtime.InteropServices;

namespace MainframeEngine;

/// <summary>
/// A surface shader (Godot's <c>Shader</c>, ADR 0237) loaded from a <c>.slang</c> file whose first statement is <c>import
/// mainframe.canvas;</c> (<see cref="Program"/>, drawn by the canvas renderer) or <c>import mainframe.spatial;</c>
/// (<see cref="Spatial"/>, drawn by the mesh renderer). Its SPIR-V and its material layout are built ahead of time next to the
/// source by <see cref="SurfaceShaderBuild"/> (mf-shaders, <c>just canvas-shaders</c>): <c>x.slang.vert.spv</c>,
/// <c>x.slang.frag.spv</c> and, for spatial shaders, one pair per other <see cref="SpatialPass"/>, <c>x.slang.layout.json</c> and
/// <c>x.slang.spvlock</c>, all committed, like the engine's own shaders.
/// </summary>
[EditorIcon("code")]
public sealed class Shader : Resource
{
    /// <summary>The extension of a shader file (ADR 0237); helper modules share it, without the <c>import mainframe.…;</c>.</summary>
    public const string Extension = ".slang";

    /// <summary>The extensions of Godot-syntax shaders (ADRs 0113, 0205, 0236), which no longer load (ADR 0237).</summary>
    public static readonly string[] GodotExtensions = [".mfshader", ".gdshader"];

    /// <summary>The canvas program (uniform layout, blend and render modes); null for a spatial shader.</summary>
    public CanvasShaderProgram? Program { get; private set; }

    /// <summary>The spatial program (ADR 0205); null for a canvas shader.</summary>
    public SpatialShaderProgram? Spatial { get; private set; }

    /// <summary>The <c>.slang</c> path the SPIR-V file names extend (null for code-built shaders and shaders without a layout).</summary>
    public string? SpvBasePath { get; private set; }

    /// <summary>Absolute path of the vertex stage's SPIR-V (null for code-built shaders without one); a spatial shader's colour pass.</summary>
    public string? VertexSpvPath => SpvBasePath is null ? null : SpvBasePath + ".vert.spv";

    public string? FragmentSpvPath => SpvBasePath is null ? null : SpvBasePath + ".frag.spv";

    /// <summary>The SPIR-V of one stage of a spatial shader's pass (<see cref="SpatialShaderProgram.PassSuffix"/>), or null without SPIR-V.</summary>
    public string? SpatialSpvPath(SpatialPass pass, bool vertex) =>
        SpvBasePath is null ? null : SpvBasePath + SpatialShaderProgram.PassSuffix(pass) + (vertex ? ".vert.spv" : ".frag.spv");

    /// <summary>True when the shader defines <c>vertex()</c>: its canvas items keep local vertices and get the model matrix.</summary>
    public bool HasVertexFunction => Program?.HasVertexFunction ?? Spatial?.HasVertexFunction ?? false;

    /// <summary>The canvas shader's <c>[blend(…)]</c>.</summary>
    public CanvasBlendMode BlendMode => Program?.BlendMode ?? CanvasBlendMode.Mix;

    /// <summary>The shader's <c>[unshaded]</c>: no lights (canvas: the canvas modulation does not apply either).</summary>
    public bool Unshaded => Program?.Unshaded ?? Spatial?.Unshaded ?? false;

    /// <summary>The uniforms of either kind of shader.</summary>
    public IReadOnlyList<ShaderUniform> Uniforms => Program?.Uniforms ?? Spatial?.Uniforms ?? [];

    /// <summary>Bytes of the uniform block (std140, a multiple of 16).</summary>
    public int UniformBlockSize => Program?.UniformBlockSize ?? Spatial?.UniformBlockSize ?? 0;

    /// <summary>A shader from a program built in code (tools, tests); <paramref name="spvBasePath"/> + <c>.vert.spv</c>/<c>.frag.spv</c> when drawn.</summary>
    public static Shader FromProgram(CanvasShaderProgram program, string? spvBasePath = null) => new()
    {
        Program = program ?? throw new ArgumentNullException(nameof(program)),
        SpvBasePath = spvBasePath,
    };

    /// <summary>A spatial shader from a program built in code (tools, tests); its SPIR-V extends <paramref name="spvBasePath"/>.</summary>
    public static Shader FromProgram(SpatialShaderProgram program, string? spvBasePath = null) => new()
    {
        Spatial = program ?? throw new ArgumentNullException(nameof(program)),
        SpvBasePath = spvBasePath,
    };

    /// <summary>
    /// Loads a surface shader's <c>.slang</c> file with its prebuilt layout and SPIR-V (a warning when they are stale). Without
    /// a layout file the shader has no parameters and no SPIR-V, so it draws with the default material.
    /// </summary>
    /// <exception cref="FormatException">
    /// A Godot-syntax shader (<c>.mfshader</c>, <c>.gdshader</c>), a <c>.slang</c> file that is not a surface shader, or an
    /// unreadable layout file (the message names the file).
    /// </exception>
    public static Shader Load(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (IsGodotSyntax(fullPath))
            throw new FormatException(
                $"{path}: Godot-syntax shaders were removed (ADR 0237); port it to a Slang surface shader (x.slang starting with `import mainframe.spatial;` or `import mainframe.canvas;`, see docs/design/shaders.md#porting-from-godot).");
        var source = ContentFiles.ReadAllText(fullPath);
        var name = Path.GetFileName(fullPath);
        var parsed = SurfaceShaderSource.Parse(source, name);
        var layoutPath = fullPath + SurfaceShaderLayout.Extension;
        if (!ContentFiles.Exists(layoutPath))
        {
            Log.Warning($"[Shader] '{path}' has no layout file (x.slang.layout.json): it is not built; build it with mf-shaders (`just canvas-shaders <folder>` in the engine checkout, needs slangc).");
            var empty = new SurfaceShaderLayout(parsed.Kind, [], 0, [], new SurfaceRenderModes(), parsed.Vertex is not null, parsed.Fragment is not null);
            return parsed.Kind == SurfaceShaderKind.Spatial ? new Shader { Spatial = empty.ToSpatial(name) } : new Shader { Program = empty.ToCanvas() };
        }

        var layout = SurfaceShaderLayout.Read(ContentFiles.ReadAllText(layoutPath), name);
        var shader = layout.Kind == SurfaceShaderKind.Spatial
            ? new Shader { Spatial = layout.ToSpatial(name), SpvBasePath = fullPath }
            : new Shader { Program = layout.ToCanvas(), SpvBasePath = fullPath };
        if (!SurfaceShaderBuild.IsUpToDate(fullPath, source, parsed, layout))
            Log.Warning($"[Shader] '{path}': the SPIR-V is missing or older than the shader; rebuild it with mf-shaders (`just canvas-shaders <folder>` in the engine checkout, needs slangc).");
        return shader;
    }

    /// <summary>True when <paramref name="path"/> is a Godot-syntax shader (<see cref="GodotExtensions"/>), which no longer loads.</summary>
    public static bool IsGodotSyntax(string path) =>
        Array.Exists(GodotExtensions, e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The std140 bytes of the material block for <paramref name="parameters"/> over the shader's defaults. A spatial
    /// shader's <c>[color]</c> vectors are converted from sRGB to linear (the 3D renderer is linear); a canvas
    /// shader's are not (ADR 0113).
    /// </summary>
    public void WriteUniformBlock(IReadOnlyDictionary<string, object> parameters, Span<byte> block)
    {
        if (Program is null && Spatial is null)
            throw new InvalidOperationException("The shader has no program.");
        var linearize = Spatial is not null;
        block.Clear();
        var uniforms = Uniforms;
        for (var i = 0; i < uniforms.Count; i++) // no enumerator: a uniform set every frame (a hit flash) allocates nothing here
        {
            var u = uniforms[i];
            if (u.IsSampler)
                continue;
            var target = block.Slice(u.Offset, u.Size);
            if (parameters.TryGetValue(u.Name, out var value))
                ShaderValues.Write(u, value, target);
            else if (u.Default is { } d)
                ShaderValues.Write(u, d, target);
            else
                continue;
            if (linearize && u.SourceColor && u.Type is ShaderUniformType.Vec3 or ShaderUniformType.Vec4)
                ShaderValues.Linearize(target, u.ArrayLength > 0 ? u.ArrayLength : 1, u.Stride);
        }
    }
}

/// <summary>Writes ShaderMaterial parameter values into std140 uniform storage.</summary>
internal static class ShaderValues
{
    public static void Write(ShaderUniform u, object value, Span<byte> target)
    {
        var components = ShaderUniform.Components(u.Type);
        var isInt = u.Type is ShaderUniformType.Int or ShaderUniformType.UInt or ShaderUniformType.Bool or ShaderUniformType.IVec2
            or ShaderUniformType.IVec3 or ShaderUniformType.IVec4;
        Span<float> scratch = stackalloc float[16];
        if (u.ArrayLength > 0)
        {
            var elements = Elements(value);
            for (var i = 0; i < Math.Min(elements.Count, u.ArrayLength); i++)
            {
                var n = Flatten(elements[i], scratch);
                Store(target.Slice(i * u.Stride, u.Stride), scratch[..Math.Min(n, components)], isInt);
            }

            return;
        }

        var count = Flatten(value, scratch);
        Store(target, scratch[..Math.Min(count, components)], isInt);
    }

    /// <summary>Converts the rgb of <paramref name="count"/> colours (<paramref name="stride"/> bytes apart) from sRGB to linear in place.</summary>
    public static void Linearize(Span<byte> target, int count, int stride)
    {
        for (var i = 0; i < count; i++)
        {
            var rgb = MemoryMarshal.Cast<byte, float>(target.Slice(i * stride, 12));
            for (var c = 0; c < 3; c++)
                rgb[c] = ColorSpace.SrgbToLinear(rgb[c]);
        }
    }

    private static void Store(Span<byte> target, ReadOnlySpan<float> values, bool isInt)
    {
        var words = MemoryMarshal.Cast<byte, uint>(target);
        for (var i = 0; i < values.Length && i < words.Length; i++)
            words[i] = isInt ? unchecked((uint)(int)values[i]) : BitConverter.SingleToUInt32Bits(values[i]);
    }

    private static List<object> Elements(object value) => value switch
    {
        float[] f => [.. f.Select(x => (object)x)],
        int[] i => [.. i.Select(x => (object)x)],
        Vector2[] v => [.. v.Select(x => (object)x)],
        Vector3[] v => [.. v.Select(x => (object)x)],
        Vector4[] v => [.. v.Select(x => (object)x)],
        System.Collections.IEnumerable e and not string => [.. e.Cast<object>()],
        _ => [value],
    };

    private static int Flatten(object value, Span<float> output)
    {
        switch (value)
        {
            case float f: output[0] = f; return 1;
            case double d: output[0] = (float)d; return 1;
            case int i: output[0] = i; return 1;
            case uint ui: output[0] = ui; return 1;
            case long l: output[0] = l; return 1;
            case bool b: output[0] = b ? 1 : 0; return 1;
            case Vector2 v: output[0] = v.X; output[1] = v.Y; return 2;
            case Vector3 v: output[0] = v.X; output[1] = v.Y; output[2] = v.Z; return 3;
            case Vector4 v: output[0] = v.X; output[1] = v.Y; output[2] = v.Z; output[3] = v.W; return 4;
            case System.Drawing.Color c: output[0] = c.R / 255f; output[1] = c.G / 255f; output[2] = c.B / 255f; output[3] = c.A / 255f; return 4;
            case float[] a:
                for (var k = 0; k < a.Length && k < output.Length; k++)
                    output[k] = a[k];
                return Math.Min(a.Length, output.Length);
            default:
                throw new ArgumentException($"Shader parameter value of type {value.GetType().Name} is not supported.", nameof(value));
        }
    }
}


/// <summary>
/// <c>.slang</c> surface shaders → <see cref="Shader"/>; Godot-syntax <c>.mfshader</c>/<c>.gdshader</c> files fail with the
/// porting error (ADR 0237).
/// </summary>
public sealed class ShaderImporter : IAssetImporter
{
    public string Name => "shader";

    public IReadOnlyList<string> Extensions { get; } = [Shader.Extension, .. Shader.GodotExtensions];

    public Resource Import(string fullPath, string projectPath, AssetMeta? meta) => Shader.Load(fullPath);
}

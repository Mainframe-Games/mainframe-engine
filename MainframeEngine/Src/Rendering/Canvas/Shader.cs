using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MainframeEngine;

/// <summary>
/// A canvas item shader (Godot's <c>Shader</c>, <c>shader_type canvas_item</c>) loaded from a <c>.gdshader</c> file in Godot's
/// shading language (ADR 0113). <see cref="CanvasShaderCompiler"/> translates it to Slang (ADR 0144); the SPIR-V is built
/// ahead of time next to the source (<c>x.gdshader.vert.spv</c>, <c>x.gdshader.frag.spv</c>, plus <c>x.gdshader.spvlock</c>
/// with the hash of the Slang it came from) by <see cref="CanvasShaderBuild"/> — the mf-shaders tool runs it
/// (<c>just canvas-shaders</c>) — and committed, like the engine's own shaders.
/// </summary>
[EditorIcon("code")]
public sealed class Shader : Resource
{
    /// <summary>The translated program (uniform layout, blend and render modes).</summary>
    public CanvasShaderProgram? Program { get; private set; }

    /// <summary>Absolute path of the vertex stage's SPIR-V (null for code-built shaders without one).</summary>
    public string? VertexSpvPath { get; private set; }

    public string? FragmentSpvPath { get; private set; }

    /// <summary>True when the shader defines <c>vertex()</c>: its items keep local vertices and get MODEL_MATRIX.</summary>
    public bool HasVertexFunction => Program?.HasVertexFunction ?? false;

    /// <summary>The shader's <c>render_mode blend_*</c>.</summary>
    public CanvasBlendMode BlendMode => Program?.BlendMode ?? CanvasBlendMode.Mix;

    /// <summary>The shader's <c>render_mode unshaded</c>: the canvas modulation (CanvasModulate) does not apply.</summary>
    public bool Unshaded => Program?.Unshaded ?? false;

    /// <summary>A shader from an already translated program (tools, tests); <paramref name="spvBasePath"/> + <c>.vert.spv</c>/<c>.frag.spv</c> when drawn.</summary>
    public static Shader FromProgram(CanvasShaderProgram program, string? spvBasePath = null) => new()
    {
        Program = program ?? throw new ArgumentNullException(nameof(program)),
        VertexSpvPath = spvBasePath is null ? null : spvBasePath + ".vert.spv",
        FragmentSpvPath = spvBasePath is null ? null : spvBasePath + ".frag.spv",
    };

    /// <summary>Loads a <c>.gdshader</c> file and its prebuilt SPIR-V (warning when the SPIR-V is stale).</summary>
    public static Shader Load(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var source = File.ReadAllText(fullPath);
        var program = CanvasShaderCompiler.Translate(source, Path.GetFileName(fullPath));
        var shader = new Shader
        {
            Program = program,
            VertexSpvPath = fullPath + ".vert.spv",
            FragmentSpvPath = fullPath + ".frag.spv",
        };
        if (!CanvasShaderBuild.IsUpToDate(fullPath, program))
            Log.Warning($"[Shader] '{path}': the SPIR-V is missing or older than the shader (or was built before the engine moved to Slang); rebuild it with mf-shaders (`just canvas-shaders <folder>` in the engine checkout, needs slangc).");
        return shader;
    }

    /// <summary>The std140 bytes of the material block for <paramref name="parameters"/> over the shader's defaults.</summary>
    public void WriteUniformBlock(IReadOnlyDictionary<string, object> parameters, Span<byte> block)
    {
        var program = Program ?? throw new InvalidOperationException("The shader has no program.");
        block.Clear();
        foreach (var u in program.Uniforms)
        {
            if (u.IsSampler)
                continue;
            var target = block.Slice(u.Offset, u.Size);
            if (parameters.TryGetValue(u.Name, out var value))
                ShaderValues.Write(u, value, target);
            else if (u.Default is { } d)
                ShaderValues.Write(u, d, target);
        }
    }
}

/// <summary>Writes ShaderMaterial parameter values into std140 uniform storage.</summary>
internal static class ShaderValues
{
    public static void Write(ShaderUniform u, object value, Span<byte> target)
    {
        var components = CanvasShaderCompiler.Components(u.Type);
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
/// Builds the SPIR-V of <c>.gdshader</c> files ahead of time (the canvas-shader counterpart of build/Shaders.targets):
/// translate, compile both stages with slangc (the engine's shader flags plus <c>-allow-glsl</c>, the engine's shader include
/// directory) — the fragment stage first, so the vertex stage writes only the inputs it kept (<see cref="SpirvInputs"/>) —
/// write <c>.vert.spv</c>/<c>.frag.spv</c> next to the source and a <c>.spvlock</c> with the hash of the Slang they came
/// from.
/// </summary>
public static partial class CanvasShaderBuild
{
    /// <summary>The extension of a shader's lock file.</summary>
    public const string LockExtension = ".spvlock";

    /// <summary>The hash a shader's lock records: the translated Slang of both stages.</summary>
    public static string Hash(CanvasShaderProgram program)
    {
        var bytes = Encoding.UTF8.GetBytes(program.VertexSource + "\n//--\n" + program.FragmentSource);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    /// <summary>True when both SPIR-V files exist and the lock matches the shader's current translation.</summary>
    public static bool IsUpToDate(string gdshaderPath, CanvasShaderProgram program) =>
        File.Exists(gdshaderPath + ".vert.spv") && File.Exists(gdshaderPath + ".frag.spv") &&
        File.Exists(gdshaderPath + LockExtension) && File.ReadAllText(gdshaderPath + LockExtension).Trim() == Hash(program);

    /// <summary>
    /// Compiles one shader when its SPIR-V is stale. Returns true when it compiled, false when it was up to date.
    /// </summary>
    /// <exception cref="InvalidOperationException">slangc failed (its output is in the message) or was not found.</exception>
    public static bool Build(string gdshaderPath, string includeDirectory, string? slangc = null)
    {
        var program = CanvasShaderCompiler.Translate(File.ReadAllText(gdshaderPath), Path.GetFileName(gdshaderPath));
        if (IsUpToDate(gdshaderPath, program))
            return false;
        slangc ??= FindSlangc() ?? throw new InvalidOperationException("slangc was not found (install the Vulkan SDK or put slangc on PATH).");
        var version = SlangcVersion(slangc);
        if (!IsSupportedSlangcVersion(version))
            throw new InvalidOperationException($"slangc {version.Trim()} is older than {MinimumSlangcVersion} (update the Vulkan SDK or Slang).");
        // The fragment stage first: the vertex stage then writes only the inputs its compiled code kept.
        Compile(slangc, includeDirectory, program.FragmentSource, "fragment", gdshaderPath + ".frag.spv", gdshaderPath);
        var fragmentInputs = SpirvInputs.InputLocations(File.ReadAllBytes(gdshaderPath + ".frag.spv"));
        var vertexSource = CanvasShaderCompiler.Translate(File.ReadAllText(gdshaderPath), Path.GetFileName(gdshaderPath), fragmentInputs).VertexSource;
        Compile(slangc, includeDirectory, vertexSource, "vertex", gdshaderPath + ".vert.spv", gdshaderPath);
        File.WriteAllText(gdshaderPath + LockExtension, Hash(program) + "\n");
        return true;
    }

    /// <summary>The oldest slangc the shaders are written for (build/Shaders.targets checks the same version).</summary>
    public const string MinimumSlangcVersion = "2026.1";

    /// <summary>True when <c>slangc -v</c> printed a version at least <see cref="MinimumSlangcVersion"/> (e.g. <c>2026.1-52-gc8ddf20bb</c>).</summary>
    public static bool IsSupportedSlangcVersion(string versionOutput)
    {
        var m = SlangcVersionRegex().Match(versionOutput ?? "");
        return m.Success
            && new Version(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture))
                >= Version.Parse(MinimumSlangcVersion);
    }

    /// <summary>What <c>slangc -v</c> prints (on stderr).</summary>
    private static string SlangcVersion(string slangc)
    {
        var start = new ProcessStartInfo(slangc) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        start.ArgumentList.Add("-v");
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {slangc}.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var text = process.StandardError.ReadToEnd() + stdout.GetAwaiter().GetResult();
        process.WaitForExit();
        return text;
    }

    [GeneratedRegex(@"(\d+)\.(\d+)")]
    private static partial Regex SlangcVersionRegex();

    /// <summary>
    /// slangc's arguments for one stage: the engine's flags (build/Shaders.targets), <c>-allow-glsl</c> for Godot's GLSL-like
    /// user code, and <c>-obfuscate</c>, which drops every name from the SPIR-V: MoltenVK's SPIRV-Cross would otherwise carry
    /// a local named like a Metal keyword (<c>vertex</c>, <c>device</c>, …) into the Metal source, which then fails to compile.
    /// </summary>
    internal static string[] SlangcArguments(string source, string stage, string includeDirectory, string output) =>
    [
        source, "-allow-glsl", "-obfuscate", "-target", "spirv", "-capability", "spirv_1_5", "-matrix-layout-row-major",
        "-entry", "main", "-stage", stage, "-I", includeDirectory, "-o", output,
    ];

    /// <summary>slangc from <c>$VULKAN_SDK/bin</c> or PATH, or null.</summary>
    public static string? FindSlangc()
    {
        var exe = OperatingSystem.IsWindows() ? "slangc.exe" : "slangc";
        if (Environment.GetEnvironmentVariable("VULKAN_SDK") is { Length: > 0 } sdk && File.Exists(Path.Combine(sdk, "bin", exe)))
            return Path.Combine(sdk, "bin", exe);
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            if (File.Exists(Path.Combine(dir, exe)))
                return Path.Combine(dir, exe);
        return null;
    }

    private static void Compile(string slangc, string includeDirectory, string code, string stage, string output, string source)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"mf-canvas-{Guid.NewGuid():N}.{stage}.slang");
        File.WriteAllText(temp, code);
        try
        {
            var start = new ProcessStartInfo(slangc)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            foreach (var arg in SlangcArguments(temp, stage, includeDirectory, output))
                start.ArgumentList.Add(arg);
            using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {slangc}.");
            var errors = process.StandardError.ReadToEnd() + process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"slangc failed for {source} ({stage}):\n{errors.Replace(temp, source + "(" + stage + ")", StringComparison.Ordinal)}");
        }
        finally
        {
            File.Delete(temp);
        }
    }
}

/// <summary><c>.gdshader</c> files → <see cref="Shader"/>.</summary>
public sealed class ShaderImporter : IAssetImporter
{
    public string Name => "shader";

    public IReadOnlyList<string> Extensions { get; } = [".gdshader"];

    public Resource Import(string fullPath, string projectPath, AssetMeta? meta) => Shader.Load(fullPath);
}

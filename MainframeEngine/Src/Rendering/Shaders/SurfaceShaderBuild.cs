using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MainframeEngine;

/// <summary>
/// Builds a surface shader's SPIR-V and layout ahead of time (ADR 0237; the counterpart of build/Shaders.targets, run by
/// mf-shaders): a probe compile whose <c>-reflection-json</c> gives the layout (<see cref="SlangReflection"/>), then every
/// pass's entry (<see cref="SurfaceShaderCompiler"/>), each fragment stage first so its vertex stage writes only the inputs
/// it kept (<see cref="SpirvInputs"/>). It writes <c>x.slang.*.spv</c>, <c>x.slang.layout.json</c> and <c>x.slang.spvlock</c>
/// next to the source. The lock's first line hashes the shader and its generated entries (what <see cref="Shader.Load"/>
/// checks); each further line hashes a file slangc read (<c>-depfile</c>: imported modules, the engine includes), which
/// <see cref="IsUpToDate(string, string)"/> checks too.
/// </summary>
public static partial class SurfaceShaderBuild
{
    /// <summary>The extension of a shader's lock file.</summary>
    public const string LockExtension = ".spvlock";

    /// <summary>
    /// Revision of how the engine builds surface shaders, for changes the lock would not otherwise see (slangc's flags,
    /// the layout format): bump it to make every lock stale. The generated entries and the engine includes are hashed already.
    /// </summary>
    public const int Revision = 1;

    /// <summary>A dependency line's prefix for a file in the engine's include directory.</summary>
    private const string IncludePrefix = "include:";

    /// <summary>The SPIR-V files of a shader: a vertex and a fragment stage per pass (<see cref="SpatialPass"/>; canvas: one).</summary>
    public static IEnumerable<string> SpvFiles(string shaderPath, SurfaceShaderKind kind) => kind == SurfaceShaderKind.Canvas
        ? [shaderPath + ".vert.spv", shaderPath + ".frag.spv"]
        : SpatialShaderProgram.Passes.SelectMany(pass => new[]
        {
            shaderPath + SpatialShaderProgram.PassSuffix(pass) + ".vert.spv",
            shaderPath + SpatialShaderProgram.PassSuffix(pass) + ".frag.spv",
        });

    /// <summary>
    /// The lock's first line: the shader's text, every generated entry (vertex stages as if the fragment kept every input)
    /// and <see cref="Revision"/>, so a change to the shader or to the engine's templates makes it stale.
    /// </summary>
    public static string Hash(string source, SurfaceShaderSource parsed, SurfaceShaderLayout layout)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(parsed);
        var module = parsed.Name;
        var text = new StringBuilder("surface ").Append(Revision.ToString(CultureInfo.InvariantCulture)).Append('\n');
        text.Append(source.ReplaceLineEndings("\n")).Append("\n//==\n").Append(layout.Write()).Append("\n//==\n");
        foreach (var (fragment, vertex) in Entries(parsed, layout, module))
            text.Append(fragment).Append("\n//--\n").Append(vertex).Append("\n//==\n");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private static IEnumerable<(string Fragment, string Vertex)> Entries(SurfaceShaderSource parsed, SurfaceShaderLayout layout, string module)
    {
        if (parsed.Kind == SurfaceShaderKind.Canvas)
        {
            yield return (SurfaceShaderCompiler.CanvasFragment(parsed, layout, module), SurfaceShaderCompiler.CanvasVertex(parsed, layout, module));
            yield break;
        }

        foreach (var pass in SpatialShaderProgram.Passes)
            yield return (SurfaceShaderCompiler.SpatialFragment(parsed, layout, module, pass), SurfaceShaderCompiler.SpatialVertex(parsed, layout, module, pass));
    }

    /// <summary>
    /// True when the SPIR-V files exist and the lock's first line matches the shader as it is (<see cref="Shader.Load"/>'s
    /// check: it has no slangc and no engine includes).
    /// </summary>
    public static bool IsUpToDate(string shaderPath, string source, SurfaceShaderSource parsed, SurfaceShaderLayout layout) =>
        SpvFiles(shaderPath, parsed.Kind).All(ContentFiles.Exists) && ReadLock(shaderPath) is [var first, ..] && first == Hash(source, parsed, layout);

    /// <summary>
    /// True when the shader's SPIR-V, layout and lock are current: <see cref="IsUpToDate(string, string, SurfaceShaderSource,
    /// SurfaceShaderLayout)"/>, and every file the lock lists (resolved against <paramref name="includeDirectory"/> and the
    /// shader's folder) still hashes the same (<c>mf-shaders check</c>, <c>just shaders-check</c>).
    /// </summary>
    /// <exception cref="FormatException">The shader or its layout file does not read.</exception>
    public static bool IsUpToDate(string shaderPath, string includeDirectory)
    {
        var source = File.ReadAllText(shaderPath);
        var parsed = SurfaceShaderSource.Parse(source, Path.GetFileName(shaderPath));
        var layoutPath = shaderPath + SurfaceShaderLayout.Extension;
        if (!File.Exists(layoutPath))
            return false;
        var layout = SurfaceShaderLayout.Read(File.ReadAllText(layoutPath), parsed.Name);
        if (!IsUpToDate(shaderPath, source, parsed, layout))
            return false;
        var directory = Path.GetDirectoryName(Path.GetFullPath(shaderPath))!;
        foreach (var line in ReadLock(shaderPath).Skip(1))
        {
            var space = line.LastIndexOf(' ');
            if (space < 0)
                return false;
            var file = Resolve(line[..space], includeDirectory, directory);
            if (!File.Exists(file) || FileHash(file) != line[(space + 1)..])
                return false;
        }

        return true;
    }

    private static string[] ReadLock(string shaderPath)
    {
        var lockPath = shaderPath + LockExtension;
        // Shader.Load reads it at run time, so through the content file system (the build and the check read the disk).
        return ContentFiles.Exists(lockPath) ? ContentFiles.ReadAllText(lockPath).ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries) : [];
    }

    private static string Resolve(string entry, string includeDirectory, string shaderDirectory) =>
        entry.StartsWith(IncludePrefix, StringComparison.Ordinal)
            ? Path.Combine(includeDirectory, entry[IncludePrefix.Length..])
            : Path.GetFullPath(Path.Combine(shaderDirectory, entry));

    private static string FileHash(string path) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(File.ReadAllText(path).ReplaceLineEndings("\n"))));

    /// <summary>
    /// Builds one surface shader when it is stale. Returns true when it compiled, false when it was up to date.
    /// <paramref name="warnings"/> receives what is suspicious but builds.
    /// </summary>
    /// <exception cref="InvalidOperationException">slangc failed (its output is in the message) or was not found.</exception>
    /// <exception cref="FormatException">The shader is not a surface shader, or its material or varyings can't be bound.</exception>
    public static bool Build(string shaderPath, string includeDirectory, string? slangc = null, ICollection<string>? warnings = null)
    {
        shaderPath = Path.GetFullPath(shaderPath);
        includeDirectory = Path.GetFullPath(includeDirectory);
        var source = File.ReadAllText(shaderPath);
        var name = Path.GetFileName(shaderPath);
        var parsed = SurfaceShaderSource.Parse(source, name);
        if (IsUpToDate(shaderPath, includeDirectory))
            return false;
        var compiler = CheckedSlangc(slangc);
        var temp = Directory.CreateTempSubdirectory("mf-shader-").FullName;
        var dependencies = new SortedSet<string>(StringComparer.Ordinal);
        try
        {
            var context = new CompileContext(compiler, includeDirectory, shaderPath, temp, dependencies);
            var json = Path.Combine(temp, "probe.json");
            context.Compile(SurfaceShaderCompiler.Probe(parsed, name), "fragment", Path.Combine(temp, "probe.spv"), "reflection probe", reflection: json);
            var layout = SlangReflection.Read(File.ReadAllText(json), parsed, warnings);
            if (parsed.Kind == SurfaceShaderKind.Canvas)
            {
                context.Compile(SurfaceShaderCompiler.CanvasFragment(parsed, layout, name), "fragment", shaderPath + ".frag.spv", "fragment stage");
                var kept = SpirvInputs.InputLocations(File.ReadAllBytes(shaderPath + ".frag.spv"));
                context.Compile(SurfaceShaderCompiler.CanvasVertex(parsed, layout, name, kept), "vertex", shaderPath + ".vert.spv", "vertex stage");
            }
            else
            {
                // Every pass in parallel (ten slangc runs), each fragment stage before its vertex stage.
                try
                {
                    Parallel.ForEach(SpatialShaderProgram.Passes, pass =>
                    {
                        var basePath = shaderPath + SpatialShaderProgram.PassSuffix(pass);
                        context.Compile(SurfaceShaderCompiler.SpatialFragment(parsed, layout, name, pass), "fragment", basePath + ".frag.spv", $"{pass} pass, fragment");
                        var kept = SpirvInputs.InputLocations(File.ReadAllBytes(basePath + ".frag.spv"));
                        context.Compile(SurfaceShaderCompiler.SpatialVertex(parsed, layout, name, pass, kept), "vertex", basePath + ".vert.spv", $"{pass} pass, vertex");
                    });
                }
                catch (AggregateException e) when (e.InnerExceptions.Count > 0)
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerExceptions[0]).Throw();
                }
            }

            File.WriteAllText(shaderPath + SurfaceShaderLayout.Extension, layout.Write());
            var directory = Path.GetDirectoryName(shaderPath)!;
            var text = new StringBuilder(Hash(source, parsed, layout)).Append('\n');
            foreach (var dependency in dependencies)
                text.Append(LockEntry(dependency, includeDirectory, directory)).Append(' ').Append(FileHash(dependency)).Append('\n');
            File.WriteAllText(shaderPath + LockExtension, text.ToString());
            return true;
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    private static string LockEntry(string file, string includeDirectory, string shaderDirectory)
    {
        var relative = Path.GetRelativePath(includeDirectory, file);
        if (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative))
            return IncludePrefix + relative.Replace('\\', '/');
        return Path.GetRelativePath(shaderDirectory, file).Replace('\\', '/');
    }

    // One shader's compiles: the same slangc, search paths and temp folder, and the files they read.
    private sealed class CompileContext(string slangc, string includeDirectory, string shaderPath, string temp, SortedSet<string> dependencies)
    {
        public void Compile(string code, string stage, string output, string what, string? reflection = null)
        {
            var entry = Path.Combine(temp, $"mf-{Guid.NewGuid():N}.{stage}.slang");
            var depfile = entry + ".d";
            File.WriteAllText(entry, code);
            var arguments = SlangcArguments(entry, stage, includeDirectory, Path.GetDirectoryName(shaderPath)!, output, obfuscate: reflection is null);
            arguments = [.. arguments, "-depfile", depfile, .. (reflection is null ? (string[])[] : ["-reflection-json", reflection])];
            var (exitCode, errors) = RunSlangc(slangc, arguments);
            if (exitCode != 0)
                throw new InvalidOperationException(
                    $"slangc failed for {shaderPath} ({what}, exit code {exitCode.ToString(CultureInfo.InvariantCulture)}):\n{errors.Replace(entry, Path.GetFileName(shaderPath) + " (" + what + ")", StringComparison.Ordinal)}");
            if (File.Exists(depfile))
            {
                lock (dependencies)
                {
                    foreach (var file in ReadDepfile(File.ReadAllText(depfile)))
                    {
                        var full = Path.GetFullPath(file);
                        if (!full.StartsWith(temp, StringComparison.Ordinal) && full != shaderPath)
                            dependencies.Add(full);
                    }
                }
            }
        }
    }

    /// <summary>The prerequisites of a make-style depfile (<c>out: a b c</c>; <c>\ </c> escapes a space, <c>\</c> ends a continued line).</summary>
    internal static List<string> ReadDepfile(string text)
    {
        var colon = text.IndexOf(": ", StringComparison.Ordinal);
        var rest = (colon >= 0 ? text[(colon + 2)..] : text).Replace("\\\r\n", " ", StringComparison.Ordinal).Replace("\\\n", " ", StringComparison.Ordinal);
        var files = new List<string>();
        var current = new StringBuilder();
        for (var i = 0; i < rest.Length; i++)
        {
            var c = rest[i];
            if (c == '\\' && i + 1 < rest.Length && rest[i + 1] == ' ')
            {
                current.Append(' ');
                i++;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                    files.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
            files.Add(current.ToString());
        return files;
    }

    private static string CheckedSlangc(string? slangc)
    {
        slangc ??= FindSlangc() ?? throw new InvalidOperationException("slangc was not found (install the Vulkan SDK or put slangc on PATH).");
        var version = SlangcVersion(slangc);
        if (!IsSupportedSlangcVersion(version))
            throw new InvalidOperationException($"slangc {version.Trim()} is older than {MinimumSlangcVersion} (update the Vulkan SDK or Slang).");
        return slangc;
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
    /// slangc's arguments for one stage: the engine's flags (build/Shaders.targets) and the search paths (the engine includes,
    /// then the shader's folder, where its module and the modules it imports are), and <c>-obfuscate</c>, which drops every
    /// name from the SPIR-V: MoltenVK's SPIRV-Cross would otherwise carry a name like a Metal keyword (<c>vertex</c>,
    /// <c>fragment</c>, <c>device</c>, …) into the Metal source, which then fails to compile. The reflection probe is not
    /// obfuscated (its names are what it is for).
    /// </summary>
    internal static string[] SlangcArguments(string source, string stage, string includeDirectory, string shaderDirectory, string output, bool obfuscate = true) =>
    [
        // -warnings-disable 41012: as build/Shaders.targets (the shadow kernels' Texture2DArray.Gather needs no extra capability).
        source, .. (obfuscate ? (string[])["-obfuscate"] : []), "-warnings-disable", "41012", "-target", "spirv", "-capability", "spirv_1_5",
        "-matrix-layout-row-major", "-entry", "main", "-stage", stage, "-I", includeDirectory, "-I", shaderDirectory, "-o", output,
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

    // slangc's exit code and output. slangc 2026.1 sometimes dies of a signal (SIGBUS, exit code 138) with no output while
    // other slangc processes run: a crash, not a compile error, so it is retried.
    private static (int ExitCode, string Output) RunSlangc(string slangc, string[] arguments)
    {
        var start = new ProcessStartInfo(slangc)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (var arg in arguments)
            start.ArgumentList.Add(arg);
        for (var attempt = 1; ; attempt++)
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {slangc}.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEnd() + stdout.GetAwaiter().GetResult();
            process.WaitForExit();
            if (process.ExitCode > 128 && errors.Trim().Length == 0 && attempt < 6)
                continue;
            return (process.ExitCode, errors);
        }
    }
}

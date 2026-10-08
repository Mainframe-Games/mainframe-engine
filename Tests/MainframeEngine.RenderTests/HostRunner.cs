using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MainframeEngine.RenderTests.Host;

namespace MainframeEngine.RenderTests;

/// <summary>Launches <c>MainframeEngine.RenderTests.Host</c> for one scene and reads its result.</summary>
public static class HostRunner
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    public static HostResult Run(string scene, string outputDirectory, params string[] extraArgs)
        => RunExpectingExit(0, scene, outputDirectory, extraArgs);

    /// <summary>Runs the host with extra environment variables (e.g. runtime knobs) for its process.</summary>
    public static HostResult RunWithEnvironment(IReadOnlyDictionary<string, string> environment, string scene, string outputDirectory,
        params string[] extraArgs)
        => RunExpectingExit(0, scene, outputDirectory, environment, extraArgs);

    /// <summary>Runs the host and requires it to exit with <paramref name="expectedExitCode"/> (the scene's <c>Engine.Run()</c> result).</summary>
    public static HostResult RunExpectingExit(int expectedExitCode, string scene, string outputDirectory, params string[] extraArgs)
        => RunExpectingExit(expectedExitCode, scene, outputDirectory, environment: null, extraArgs);

    private static HostResult RunExpectingExit(int expectedExitCode, string scene, string outputDirectory,
        IReadOnlyDictionary<string, string>? environment, string[] extraArgs)
    {
        ArgumentNullException.ThrowIfNull(extraArgs);
        return Launch("MainframeEngine.RenderTests.Host.dll", [scene, "--out", outputDirectory, .. extraArgs], scene, outputDirectory,
            environment, expectedExitCode);
    }

    /// <summary>
    /// Runs the editor (<c>MainframeEngine.Editor --smoke &lt;dir&gt; ...</c>, in this output folder through the project
    /// reference); it writes the same <c>result.json</c> as the render host.
    /// </summary>
    public static HostResult RunEditor(string outputDirectory, IReadOnlyDictionary<string, string>? environment, params string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        // At the goldens' canonical content scale (2 on macOS, 1 elsewhere), like the scene host: captures are --size × that
        // in pixels on any display.
        var scale = HostOptions.CanonicalScale.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Launch("MainframeEngine.Editor.dll", ["--smoke", outputDirectory, "--scale", scale, .. args], "editor", outputDirectory, environment, 0);
    }

    private static HostResult Launch(string dll, IReadOnlyList<string> arguments, string scene, string outputDirectory,
        IReadOnlyDictionary<string, string>? environment, int expectedExitCode)
    {
        if (Directory.Exists(outputDirectory))
            Directory.Delete(outputDirectory, recursive: true);
        Directory.CreateDirectory(outputDirectory);

        var hostDll = Path.Combine(AppContext.BaseDirectory, dll);
        var psi = new ProcessStartInfo(DotnetPath())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        // Linux under Xvfb: make SDL use X11 unless the caller chose a driver (CI sets it too).
        if (OperatingSystem.IsLinux() && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SDL_VIDEODRIVER")) &&
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
            psi.Environment["SDL_VIDEODRIVER"] = "x11";

        // Keep the persisted pipeline cache out of the user's cache folder (and shared by the test runs).
        // (--pipeline-cache <dir> overrides it per run.)
        psi.Environment[PipelineCache.DirectoryVariable] = Path.Combine(RenderTestEnvironment.ArtifactsDirectory, "pipeline-cache");
        if (environment is not null)
            foreach (var (name, value) in environment)
                psi.Environment[name] = value;

        psi.ArgumentList.Add(hostDll);
        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        var output = new StringBuilder();
        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (!process.WaitForExit(Timeout))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail($"Render host '{scene}' timed out after {Timeout}.\n{output}");
        }
        process.WaitForExit(); // flush async output

        File.WriteAllText(Path.Combine(outputDirectory, "host.log"), output.ToString());
        var resultPath = Path.Combine(outputDirectory, HostResult.FileName);
        if (process.ExitCode != expectedExitCode || !File.Exists(resultPath))
        {
            // > 128 on Unix is 128 + signal (134 = SIGABRT: a native abort, e.g. in the driver or SDL).
            var signal = !OperatingSystem.IsWindows() && process.ExitCode > 128 ? $", signal {process.ExitCode - 128}" : "";
            Assert.Fail($"Render host '{scene}' exited with {process.ExitCode}{signal} (expected {expectedExitCode}" +
                        $"{(File.Exists(resultPath) ? "" : ", no result.json")}).\n" +
                        $"Command: {psi.FileName} {string.Join(' ', psi.ArgumentList)}\n" +
                        $"SDL_VIDEODRIVER={(psi.Environment.TryGetValue("SDL_VIDEODRIVER", out var driver) ? driver : "")} " +
                        $"DISPLAY={(psi.Environment.TryGetValue("DISPLAY", out var display) ? display : "")}\n" +
                        $"--- host stdout/stderr ---\n{output}");
        }

        return JsonSerializer.Deserialize<HostResult>(File.ReadAllText(resultPath), HostResult.JsonOptions)
               ?? throw new InvalidDataException($"Empty result from render host '{scene}'.");
    }

    // dotnet sets DOTNET_HOST_PATH for the processes it starts; fall back to the current muxer or PATH.
    private static string DotnetPath()
    {
        if (Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } hostPath && File.Exists(hostPath))
            return hostPath;

        var current = Environment.ProcessPath;
        if (current is not null && Path.GetFileNameWithoutExtension(current).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return current;

        return "dotnet";
    }
}

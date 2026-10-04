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

    /// <summary>Runs the host and requires it to exit with <paramref name="expectedExitCode"/> (the scene's <c>Engine.Run()</c> result).</summary>
    public static HostResult RunExpectingExit(int expectedExitCode, string scene, string outputDirectory, params string[] extraArgs)
    {
        ArgumentNullException.ThrowIfNull(extraArgs);
        if (Directory.Exists(outputDirectory))
            Directory.Delete(outputDirectory, recursive: true);
        Directory.CreateDirectory(outputDirectory);

        var hostDll = Path.Combine(AppContext.BaseDirectory, "MainframeEngine.RenderTests.Host.dll");
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

        psi.ArgumentList.Add(hostDll);
        psi.ArgumentList.Add(scene);
        psi.ArgumentList.Add("--out");
        psi.ArgumentList.Add(outputDirectory);
        foreach (var arg in extraArgs)
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
                        $"SDL_VIDEODRIVER={psi.Environment["SDL_VIDEODRIVER"]} DISPLAY={psi.Environment["DISPLAY"]}\n" +
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

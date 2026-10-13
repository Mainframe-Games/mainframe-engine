using System.Diagnostics;
using System.Text;

namespace MainframeEngine.Video.Media;

/// <summary>
/// Thin wrapper over <see cref="Process"/> for running ffmpeg and ffprobe with redirected streams. It is public so the
/// editor's preview decodes with the same preamble and error reporting as export. Callers must read stderr
/// (<c>StandardError.ReadToEndAsync</c>) or ffmpeg blocks once the pipe fills, and binary output comes from
/// <c>StandardOutput.BaseStream</c>.
/// </summary>
public static class FfmpegProcess
{
    /// <summary>Common leading arguments: never wait on stdin, keep output terse.</summary>
    private static readonly string[] Preamble = ["-nostdin", "-hide_banner", "-loglevel", "error"];

    public static Process Start(string executable, IEnumerable<string> arguments, bool redirectStandardOutput, string? logLevel = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = false,
            RedirectStandardOutput = redirectStandardOutput,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var arg in Preamble)
        {
            startInfo.ArgumentList.Add(arg);
        }

        if (logLevel is not null)
        {
            startInfo.ArgumentList[^1] = logLevel;
        }

        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start {executable}.");
    }

    /// <summary>Starts a tool (ffprobe) with no ffmpeg preamble, both streams redirected.</summary>
    public static Process StartRaw(string executable, IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start {executable}.");
    }

    /// <summary>
    /// Waits for exit, honouring cancellation by killing the process tree. Throws
    /// <see cref="FfmpegException"/> on a non-zero exit unless cancellation was requested.
    /// </summary>
    public static async Task WaitForSuccessAsync(Process process, Task<string> standardError, string what, CancellationToken cancellationToken)
    {
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        var stderr = await standardError.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new FfmpegException($"{what} failed (ffmpeg exit code {process.ExitCode}).\n\n{Tail(stderr)}", process.ExitCode, stderr);
        }
    }

    public static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already exited between the check and the kill.
        }
    }

    private static string Tail(string text, int maxLines = 12)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join('\n', lines.TakeLast(maxLines));
    }
}

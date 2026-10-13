using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace MainframeEngine;

/// <summary>One ffmpeg run: argument list (no shell), optional stdin, stdout lines to a callback, last 4 KB of stderr kept for errors.</summary>
internal sealed class FfmpegProcess : IDisposable
{
    private readonly Process _process;
    private readonly StringBuilder _stderr = new();

    public FfmpegProcess(string ffmpeg, IEnumerable<string> args, bool stdin, Action<string>? onStdout = null)
    {
        var info = new ProcessStartInfo(ffmpeg)
        {
            // Always redirected so an ffmpeg that does not need input never reads (and steals keys from) the parent's console.
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
            info.ArgumentList.Add(a);
        _process = new Process { StartInfo = info, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, e) => { if (e.Data is { } line) onStdout?.Invoke(line); };
        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
                return;
            lock (_stderr)
            {
                _stderr.AppendLine(e.Data);
                if (_stderr.Length > 4096)
                    _stderr.Remove(0, _stderr.Length - 4096);
            }
        };

        try
        {
            _process.Start();
        }
        catch (Win32Exception e)
        {
            _process.Dispose();
            throw new FfmpegNotFoundException($"Could not start ffmpeg at '{ffmpeg}': {e.Message}");
        }

        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        if (!stdin)
            _process.StandardInput.Close();
    }

    /// <summary>The encoder's stdin. Only valid for a process started with <c>stdin: true</c>.</summary>
    public Stream Input => _process.StandardInput.BaseStream;

    public bool HasExited => _process.HasExited;

    public string ErrorTail { get { lock (_stderr) return _stderr.ToString().Trim(); } }

    public async Task<int> WaitAsync(CancellationToken ct)
    {
        await _process.WaitForExitAsync(ct).ConfigureAwait(false);
        // The async readers can still be delivering the last lines when the exit event fires; the parameterless
        // overload blocks (briefly, the process is already gone) until both streams reach EOF, so stdout and
        // the stderr tail are complete before the caller looks at them.
        _process.WaitForExit();
        return _process.ExitCode;
    }

    /// <summary>Runs to completion and throws with ffmpeg's own error text on failure.</summary>
    public static async Task RunAsync(string ffmpeg, IEnumerable<string> args, CancellationToken ct)
    {
        using var p = new FfmpegProcess(ffmpeg, args, stdin: false);
        if (await p.WaitAsync(ct).ConfigureAwait(false) != 0)
            throw new IOException($"ffmpeg failed: {p.ErrorTail}");
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // It exited between the check and the kill: exactly the state we wanted.
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Win32Exception (access denied, or the process is exiting) or an AggregateException from the process tree:
            // Dispose runs on cleanup paths that must not throw, and the handle is released below either way.
            Log.Warning($"[Recorder] Could not stop ffmpeg: {e.GetType().Name}: {e.Message}");
        }

        _process.Dispose();
    }
}

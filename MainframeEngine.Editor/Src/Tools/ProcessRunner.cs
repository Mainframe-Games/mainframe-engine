using System.Diagnostics;

namespace MainframeEngine.Editor;

/// <summary>The outcome of <see cref="ProcessRunner.RunAsync"/>.</summary>
/// <param name="ExitCode">The process exit code (-1 when it could not start, was cancelled or timed out).</param>
/// <param name="Output">Every stdout and stderr line, in arrival order.</param>
/// <param name="Duration">Wall-clock time from start to exit.</param>
/// <param name="TimedOut">Killed because the timeout passed.</param>
/// <param name="Cancelled">Killed because the token was cancelled.</param>
/// <param name="StartError">Why the process could not start (null when it ran).</param>
public sealed record ProcessResult(int ExitCode, IReadOnlyList<string> Output, TimeSpan Duration, bool TimedOut, bool Cancelled, string? StartError)
{
    public bool Succeeded => ExitCode == 0 && !TimedOut && !Cancelled && StartError is null;

    /// <summary>The output as one string (LF-joined).</summary>
    public string OutputText => string.Join('\n', Output);
}

/// <summary>
/// Runs a tool process (<c>dotnet new</c>, <c>dotnet build</c>, <c>dotnet --list-sdks</c>) for the editor: no shell, stdout
/// and stderr read line by line (each line reported to <c>onLine</c> from a background thread), killed with its whole
/// process tree on cancellation or timeout. The child gets a clean .NET CLI environment: no telemetry/logo banners, and
/// none of the MSBuild variables a parent <c>dotnet run</c>/<c>dotnet test</c> sets (they would make a nested
/// <c>dotnet build</c> use the parent's MSBuild and SDK resolver).
/// </summary>
public static class ProcessRunner
{
    /// <summary>Environment variables removed from child processes (set by a parent dotnet/MSBuild process).</summary>
    public static readonly IReadOnlyList<string> InheritedMsBuildVariables =
    [
        "MSBuildExtensionsPath", "MSBUILD_EXE_PATH", "MSBuildSDKsPath", "MSBuildLoadMicrosoftTargetsReadOnly",
        "MSBUILDNOINPROCNODE", "DOTNET_HOST_PATH", "VSTEST_HOST_DEBUG", "VSTEST_RUNNER_DEBUG",
    ];

    /// <summary>
    /// How long <see cref="RunAsync"/> waits for the output streams to close once the process has exited. Processes it
    /// started can keep them open: MSBuild worker nodes (node reuse) and build servers inherit them and outlive the build.
    /// </summary>
    public static readonly TimeSpan OutputDrainTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Starts <paramref name="fileName"/> with <paramref name="arguments"/> (passed as an argument list, never through a
    /// shell) and waits for it to exit (not for processes it left running: see <see cref="OutputDrainTimeout"/>). Never
    /// throws for process failures: a missing executable is a
    /// <see cref="ProcessResult.StartError"/>. <paramref name="environment"/> adds or (null value) removes variables.
    /// </summary>
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string? workingDirectory = null,
        Action<string>? onLine = null,
        IReadOnlyDictionary<string, string?>? environment = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);
        var start = CreateStartInfo(fileName, arguments, workingDirectory, environment);
        var lines = new List<string>();
        var stopwatch = Stopwatch.StartNew();
        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Exited += (_, _) => exited.TrySetResult();

        void OnData(object sender, DataReceivedEventArgs e)
        {
            if (e.Data is null)
                return;
            lock (lines)
                lines.Add(e.Data);
            try
            {
                onLine?.Invoke(e.Data);
            }
            catch (Exception ex) when (EditorCommands.IsRecoverable(ex))
            {
                // A failing observer must not kill the reader thread.
                Console.Error.WriteLine($"[ProcessRunner] Output handler failed: {ex.Message}");
            }
        }

        process.OutputDataReceived += OnData;
        process.ErrorDataReceived += OnData;
        try
        {
            if (!process.Start())
                return new ProcessResult(-1, [], stopwatch.Elapsed, false, false, $"'{fileName}' did not start.");
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return new ProcessResult(-1, [], stopwatch.Elapsed, false, false, $"Could not start '{fileName}': {e.Message}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutSource = timeout is { } t ? new CancellationTokenSource(t) : new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        var timedOut = false;
        var cancelled = false;
        try
        {
            // The exit itself: WaitForExitAsync would also wait for the output streams to close, which a process the
            // child started (an MSBuild node) can delay past any timeout.
            await exited.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested;
            cancelled = !timedOut;
            Kill(process);
        }

        var duration = stopwatch.Elapsed;
        // Let the readers drain what the process wrote (WaitForExitAsync returns once both streams reached EOF); give up
        // after a moment if something still holds them open. Disposing the process closes our end.
        using var drain = new CancellationTokenSource(timedOut || cancelled ? TimeSpan.FromSeconds(5) : OutputDrainTimeout);
        try
        {
            await process.WaitForExitAsync(drain.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        string[] output;
        lock (lines)
            output = [.. lines];
        var exit = timedOut || cancelled ? -1 : process.ExitCode;
        return new ProcessResult(exit, output, duration, timedOut, cancelled, null);
    }

    /// <summary>The start info <see cref="RunAsync"/> uses (also for long-running processes the caller manages).</summary>
    public static ProcessStartInfo CreateStartInfo(
        string fileName,
        IEnumerable<string> arguments,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string?>? environment = null,
        bool redirectOutput = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);
        var start = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError = redirectOutput,
            RedirectStandardInput = false,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
        };
        if (redirectOutput)
        {
            start.StandardOutputEncoding = System.Text.Encoding.UTF8;
            start.StandardErrorEncoding = System.Text.Encoding.UTF8;
        }

        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        foreach (var name in InheritedMsBuildVariables)
            start.Environment.Remove(name);
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        start.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        if (environment is not null)
            foreach (var (name, value) in environment)
            {
                if (value is null)
                    start.Environment.Remove(name);
                else
                    start.Environment[name] = value;
            }

        return start;
    }

    /// <summary>Kills <paramref name="process"/> and its children; never throws.</summary>
    public static void Kill(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Already gone, or not ours to kill.
        }
    }
}

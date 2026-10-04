using System.Collections.Concurrent;
using System.Diagnostics;

namespace MainframeEngine.Editor;

/// <summary>What to launch for one Play instance.</summary>
/// <param name="ProgramPath">The launcher's apphost executable, or its <c>.dll</c> (run as <c>dotnet &lt;dll&gt;</c>).</param>
/// <param name="Arguments">Command-line arguments (<c>--editor-port</c>, <c>--scene</c>, …).</param>
/// <param name="WorkingDirectory">The process's working directory.</param>
/// <param name="Label">A display name (<c>Server</c>, <c>Client</c>).</param>
/// <param name="Environment">Variables to add, or (null value) remove.</param>
public sealed record GameLaunchRequest(
    string ProgramPath,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    string Label,
    IReadOnlyDictionary<string, string?>? Environment = null);

/// <summary>Starts game processes for <see cref="PlayService"/>.</summary>
public interface IGameLauncher
{
    /// <summary>
    /// Starts <paramref name="request"/>. A process that cannot start is reported as one that already exited
    /// (non-zero <see cref="IGameProcess.ExitCode"/>, the reason in its output) rather than by throwing.
    /// </summary>
    IGameProcess Launch(GameLaunchRequest request);
}

/// <summary>A launched game process.</summary>
public interface IGameProcess : IDisposable
{
    /// <summary>The OS process id (matched against the editor-link hello).</summary>
    int Id { get; }

    /// <summary>True once the process exited and its output has been read to the end.</summary>
    bool HasExited { get; }

    /// <summary>The exit code once <see cref="HasExited"/>; null while running.</summary>
    int? ExitCode { get; }

    /// <summary>The next stdout/stderr line (queued from background threads), if any. Never blocks.</summary>
    bool TryReadOutput(out string line);

    /// <summary>Kills the process (and its children); never throws.</summary>
    void Kill();
}

/// <summary>Launches games as OS processes (<see cref="Process"/>, start info from <see cref="ProcessRunner.CreateStartInfo"/>).</summary>
public sealed class ProcessGameLauncher(string? dotnetPath) : IGameLauncher
{
    /// <summary>The <c>dotnet</c> executable used for <c>.dll</c> programs (<c>dotnet</c> on the PATH when null).</summary>
    public string DotnetPath { get; } = string.IsNullOrWhiteSpace(dotnetPath) ? "dotnet" : dotnetPath;

    public IGameProcess Launch(GameLaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string fileName;
        List<string> arguments = [];
        if (request.ProgramPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            fileName = DotnetPath;
            arguments.Add(request.ProgramPath);
        }
        else
        {
            fileName = request.ProgramPath;
        }

        arguments.AddRange(request.Arguments);
        try
        {
            var start = ProcessRunner.CreateStartInfo(fileName, arguments, request.WorkingDirectory, request.Environment);
            return OsGameProcess.Start(start, fileName);
        }
        catch (ArgumentException e)
        {
            return new FailedGameProcess($"Could not start '{fileName}': {e.Message}");
        }
    }

    /// <summary>
    /// The launcher program a build of <paramref name="launcherProjectFile"/> (a <c>.csproj</c> or its folder) produced:
    /// the newest <c>bin/&lt;cfg&gt;/&lt;tfm&gt;/&lt;AssemblyName&gt;</c> apphost (<c>.exe</c> on Windows) when it exists, else
    /// its <c>.dll</c>; null when there is no build output (or the project cannot be identified).
    /// </summary>
    public static string? FindLauncherProgram(string launcherProjectFile, string configuration = "Debug")
    {
        if (string.IsNullOrWhiteSpace(launcherProjectFile) || string.IsNullOrWhiteSpace(configuration))
            return null;
        if (!File.Exists(launcherProjectFile) && !Directory.Exists(launcherProjectFile))
            return null;
        string? dll;
        try
        {
            dll = GameAssemblyLoader.FindBuildOutput(launcherProjectFile, configuration);
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (dll is null)
            return null;
        var apphost = Path.ChangeExtension(dll, OperatingSystem.IsWindows() ? ".exe" : null);
        return File.Exists(apphost) ? apphost : dll;
    }
}

/// <summary>An OS process with stdout/stderr queued line by line.</summary>
internal sealed class OsGameProcess : IGameProcess
{
    /// <summary>Most unread output lines kept; more are dropped (counted in a final line).</summary>
    public const int MaxQueuedLines = 20_000;

    // After the process exits, output still in flight is waited for at most this long (a child holding the pipes open).
    private static readonly TimeSpan OutputGrace = TimeSpan.FromSeconds(2);

    private readonly Process _process;
    private readonly ConcurrentQueue<string> _lines = new();
    private int _queued;
    private long _dropped;
    private int _openStreams = 2;
    private long _exitedTimestamp;
    private int? _exitCode;
    private bool _disposed;

    private OsGameProcess(Process process)
    {
        _process = process;
    }

    public int Id { get; private set; }

    public bool HasExited
    {
        get
        {
            if (_exitCode is not null)
                return true;
            if (_disposed || !ProcessExited())
                return false;
            var exited = Interlocked.Read(ref _exitedTimestamp);
            if (exited == 0)
            {
                Interlocked.CompareExchange(ref _exitedTimestamp, Stopwatch.GetTimestamp(), 0);
                exited = Interlocked.Read(ref _exitedTimestamp);
            }

            if (Volatile.Read(ref _openStreams) > 0 && Stopwatch.GetElapsedTime(exited) < OutputGrace)
                return false;
            try
            {
                _exitCode = _process.ExitCode;
            }
            catch (InvalidOperationException)
            {
                _exitCode = -1;
            }

            var dropped = Interlocked.Exchange(ref _dropped, 0);
            if (dropped > 0)
                _lines.Enqueue($"({dropped} output lines dropped)");
            return true;
        }
    }

    public int? ExitCode => HasExited ? _exitCode : null;

    public static IGameProcess Start(ProcessStartInfo start, string fileName)
    {
        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        var game = new OsGameProcess(process);
        process.OutputDataReceived += game.OnData;
        process.ErrorDataReceived += game.OnData;
        try
        {
            if (!process.Start())
            {
                process.Dispose();
                return new FailedGameProcess($"'{fileName}' did not start.");
            }
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            process.Dispose();
            return new FailedGameProcess($"Could not start '{fileName}': {e.Message}");
        }

        game.Id = process.Id;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return game;
    }

    public bool TryReadOutput(out string line)
    {
        if (!_lines.TryDequeue(out line!))
            return false;
        Interlocked.Decrement(ref _queued);
        return true;
    }

    public void Kill()
    {
        if (!_disposed)
            ProcessRunner.Kill(_process);
    }

    /// <summary>Kills the process if it is still running and releases it.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        Kill();
        _disposed = true;
        _process.OutputDataReceived -= OnData;
        _process.ErrorDataReceived -= OnData;
        _process.Dispose();
    }

    private bool ProcessExited()
    {
        try
        {
            return _process.HasExited;
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return true;
        }
    }

    private void OnData(object sender, DataReceivedEventArgs e)
    {
        if (e.Data is null)
        {
            Interlocked.Decrement(ref _openStreams); // EOF of stdout or stderr
            return;
        }

        if (Interlocked.Increment(ref _queued) > MaxQueuedLines)
        {
            Interlocked.Decrement(ref _queued);
            Interlocked.Increment(ref _dropped);
            return;
        }

        _lines.Enqueue(e.Data);
    }
}

/// <summary>A process that could not start: already exited (code -1) with the reason as its only output line.</summary>
internal sealed class FailedGameProcess : IGameProcess
{
    private readonly ConcurrentQueue<string> _lines = new();

    public FailedGameProcess(string reason)
    {
        _lines.Enqueue(reason);
    }

    public int Id => 0;

    public bool HasExited => true;

    public int? ExitCode => -1;

    public bool TryReadOutput(out string line) => _lines.TryDequeue(out line!);

    public void Kill()
    {
    }

    public void Dispose()
    {
    }
}

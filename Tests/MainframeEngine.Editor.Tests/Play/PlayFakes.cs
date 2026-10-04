using System.Collections.Concurrent;
using System.Globalization;
using MainframeEngine.EditorLink;

namespace MainframeEngine.Editor.Tests;

/// <summary>A builder that reports configured lines and a configured result (optionally held by a gate).</summary>
public sealed class FakeGameBuilder : IGameBuilder
{
    private int _calls;

    public GameBuildResult Result { get; set; } = new(true, [], TimeSpan.FromMilliseconds(5), false, null, ["Build succeeded."]);

    /// <summary>Output lines reported before the result.</summary>
    public IReadOnlyList<string> Lines { get; set; } = ["  Determining projects to restore...", "  MyGame -> /tmp/MyGame.dll"];

    /// <summary>When set, the build waits for it (cancellable).</summary>
    public ManualResetEventSlim? Gate { get; set; }

    public int Calls => Volatile.Read(ref _calls);

    public ConcurrentQueue<string> Paths { get; } = new();

    public static GameBuildResult Failure(params BuildDiagnostic[] diagnostics) =>
        new(false, diagnostics, TimeSpan.FromMilliseconds(5), false, null, [.. diagnostics.Select(d => d.Message)]);

    public async Task<GameBuildResult> BuildAsync(string projectOrSolutionPath, Action<string>? onLine, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        Paths.Enqueue(projectOrSolutionPath);
        foreach (var line in Lines)
            onLine?.Invoke(line);
        if (Gate is { } gate)
            await Task.Run(() => gate.Wait(cancellationToken), cancellationToken).ConfigureAwait(false);
        return Result;
    }
}

/// <summary>How a <see cref="FakeGame"/> behaves.</summary>
public enum FakeGameMode
{
    /// <summary>Connects, logs, reports status, honours commands; Stop → goodbye, exit 0.</summary>
    Normal,

    /// <summary>Like <see cref="Normal"/> but ignores Stop (only a kill ends it).</summary>
    IgnoresStop,

    /// <summary>Never opens the editor link; runs until killed or crashed.</summary>
    NeverConnects,
}

/// <summary>Launches <see cref="FakeGame"/>s in-process (fake process ids from 900001).</summary>
public sealed class FakeGameLauncher : IGameLauncher
{
    private readonly List<FakeGame> _games = [];
    private int _nextId = 900_000;

    public Func<GameLaunchRequest, FakeGameMode> ModeFor { get; set; } = _ => FakeGameMode.Normal;

    public IReadOnlyList<FakeGame> Games
    {
        get
        {
            lock (_games)
                return [.. _games];
        }
    }

    public IGameProcess Launch(GameLaunchRequest request)
    {
        var game = new FakeGame(Interlocked.Increment(ref _nextId), request, ModeFor(request));
        lock (_games)
            _games.Add(game);
        return game;
    }
}

/// <summary>
/// An in-process stand-in for a game process: a thread that drives an <see cref="EditorLinkClient"/> like
/// <see cref="GameSession"/> does (hello with its fake pid, three log entries, a status every tick, commands), and writes
/// "stdout"/"stderr" lines to its output queue.
/// </summary>
public sealed class FakeGame : IGameProcess
{
    public const int CrashExitCode = 3;
    public const int KilledExitCode = 137;

    private readonly ConcurrentQueue<string> _output = new();
    private readonly ManualResetEventSlim _wake = new(false);
    private readonly Thread _thread;
    private volatile bool _kill;
    private volatile bool _wasKilled;
    private volatile bool _crash;
    private volatile bool _exited;
    private volatile bool _paused;
    private int _exitCode;

    public FakeGame(int id, GameLaunchRequest request, FakeGameMode mode)
    {
        Id = id;
        Request = request;
        Mode = mode;
        var args = request.Arguments;
        for (var i = 0; i + 1 < args.Count; i++)
        {
            if (args[i] == "--editor-port")
                Port = int.Parse(args[i + 1], CultureInfo.InvariantCulture);
            else if (args[i] == "--scene")
                Scene = args[i + 1];
        }

        _thread = new Thread(Run) { IsBackground = true, Name = $"Fake game {id}" };
        _thread.Start();
    }

    public int Id { get; }

    public GameLaunchRequest Request { get; }

    public FakeGameMode Mode { get; }

    public int Port { get; }

    public string? Scene { get; }

    public bool Paused => _paused;

    /// <summary>True when it ended because it was killed (not by quitting or crashing).</summary>
    public bool WasKilled => _wasKilled;

    /// <summary>Every command received, in order.</summary>
    public ConcurrentQueue<EditorLinkCommand> Commands { get; } = new();

    public bool HasExited => _exited;

    public int? ExitCode => _exited ? Volatile.Read(ref _exitCode) : null;

    public bool TryReadOutput(out string line) => _output.TryDequeue(out line!);

    /// <summary>Makes the game die: a stderr line, no goodbye, exit code <see cref="CrashExitCode"/>.</summary>
    public void TriggerCrash()
    {
        _crash = true;
        _wake.Set();
    }

    public void Kill()
    {
        if (_exited)
            return;
        _kill = true;
        _wake.Set();
    }

    public void Dispose()
    {
        Kill();
        _thread.Join(TimeSpan.FromSeconds(5));
    }

    private void Run()
    {
        var code = 0;
        EditorLinkClient? client = null;
        try
        {
            _output.Enqueue($"[stdout] {Request.Label} starting");
            if (Mode == FakeGameMode.NeverConnects)
            {
                while (!_kill && !_crash)
                    _wake.Wait(50);
                code = Finish(null);
                return;
            }

            client = new EditorLinkClient(Port, new EditorLinkHello(EditorLinkProtocol.Version, Id, "FakeGame", "0.0.0-test"));
            Log("", $"{Request.Label} log 1");
            Log("Audio", $"{Request.Label} log 2");
            Log("", $"{Request.Label} log 3");
            var scene = Scene ?? "Content/Scenes/Main.mscene";
            ulong frame = 0;
            while (true)
            {
                if (_kill || _crash)
                {
                    code = Finish(client);
                    return;
                }

                frame++;
                while (client.TryReceiveCommand(out var command))
                {
                    Commands.Enqueue(command);
                    switch (command.Kind)
                    {
                        case EditorCommandKind.Stop when Mode != FakeGameMode.IgnoresStop:
                            Log("EditorLink", "Stop requested by the editor.");
                            client.ReportStatus(new EditorLinkStatus(GameRunState.Stopping, frame, 60f, scene));
                            client.SendGoodbye(0, TimeSpan.FromSeconds(2));
                            client.Dispose();
                            client = null;
                            _output.Enqueue($"[stdout] {Request.Label} exiting");
                            code = 0;
                            return;
                        case EditorCommandKind.Pause:
                            _paused = true;
                            break;
                        case EditorCommandKind.Resume:
                            _paused = false;
                            break;
                        case EditorCommandKind.ReloadScene:
                            if (command.Argument.Length > 0)
                                scene = command.Argument;
                            Log("Project", $"reloaded {scene}");
                            break;
                    }
                }

                client.ReportStatus(new EditorLinkStatus(_paused ? GameRunState.Paused : GameRunState.Running, frame, 60f, scene));
                _wake.Wait(5);
            }
        }
        finally
        {
            client?.Dispose();
            Volatile.Write(ref _exitCode, code);
            _exited = true;
        }

        void Log(string category, string message) =>
            client!.TryEnqueueLog(new LogEntry(MainframeEngine.Log.Level.Info, DateTime.UtcNow, category, message, nameof(Run), "FakeGame.cs", 1));
    }

    private int Finish(EditorLinkClient? client)
    {
        if (_kill)
        {
            _wasKilled = true;
            return KilledExitCode;
        }

        client?.Dispose(); // dies without a goodbye
        _output.Enqueue("[stderr] Unhandled exception. System.InvalidOperationException: boom");
        return CrashExitCode;
    }
}

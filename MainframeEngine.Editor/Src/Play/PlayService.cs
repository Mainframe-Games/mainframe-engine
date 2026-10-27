using System.Collections.Concurrent;
using System.Globalization;
using MainframeEngine.EditorLink;

namespace MainframeEngine.Editor;

/// <summary>What <see cref="PlayService.BuildAndLaunchAsync"/> builds and runs.</summary>
/// <param name="BuildPath">The project or solution to build.</param>
/// <param name="DesktopProject">The desktop project (<c>MyGame.Desktop.csproj</c>) whose output is run.</param>
/// <param name="Scene">The <c>--scene</c> (null: the project's main scene).</param>
/// <param name="Label">The instance's display name.</param>
/// <param name="ExtraArguments">More arguments: host flags go before <c>--editor-port</c>/<c>--scene</c>, anything from <c>++</c> on after them (the game's own).</param>
/// <param name="SkipBuild">Launch the existing build output.</param>
/// <param name="Configuration">The build configuration whose output is looked up.</param>
public sealed record PlayRequest(
    string BuildPath,
    string DesktopProject,
    string? Scene,
    string Label = "Game",
    IReadOnlyList<string>? ExtraArguments = null,
    bool SkipBuild = false,
    string Configuration = "Debug");

/// <summary>
/// Out-of-process Play: builds the game (<see cref="IGameBuilder"/>), launches it (<see cref="IGameLauncher"/>) with
/// <c>--editor-port</c>, and talks to it over the editor link (<see cref="EditorLinkServer"/>): logs, status and commands.
/// Several instances may run at once (e.g. a server and a client).
/// </summary>
/// <remarks>
/// <para>Threading: every public member is called from the editor's main thread. Build output, process output and link
/// messages arrive on background threads and are only queued; <see cref="Update"/> (once per editor frame) drains them,
/// updates the instances and raises every event. The tasks returned by <see cref="BuildAsync"/> and
/// <see cref="BuildAndLaunchAsync"/> complete from <see cref="Update"/> too (launching happens there). An idle
/// <see cref="Update"/> allocates nothing.</para>
/// <para>Matching: a hello is matched to the alive instance whose process id equals <see cref="EditorLinkHello.ProcessId"/>
/// (a reconnect matches again); failing that, to the oldest <see cref="PlayInstanceState.Launching"/> instance without a
/// connection. Connections matching nothing are ignored.</para>
/// <para>Exit rule: when the process exits the instance becomes <see cref="PlayInstanceState.Exited"/> if the exit code is
/// 0 or the editor asked it to stop (Stop, kill, timeout), else <see cref="PlayInstanceState.Crashed"/>.</para>
/// <para>Stopping: a connected instance is sent <see cref="EditorCommandKind.Stop"/> and killed if still alive
/// <see cref="StopTimeout"/> later; an instance that never connected is killed at once. A game that is recording (or
/// saving a recording) finalises the file before it exits, so it gets <see cref="RecordingStopTimeout"/> instead and its
/// recording shows as <see cref="PlayRecordingState.Saving"/> until it reports the file: killing it would kill the
/// ffmpeg mux with it (the whole process tree goes) and lose the recording. Once it reports the file (Stopped or Failed)
/// it gets a fresh <see cref="StopTimeout"/> to exit, measured from the report; a Started that arrives after Stop
/// extends the deadline to <see cref="RecordingStopTimeout"/> from the Stop.</para>
/// </remarks>
public sealed class PlayService : IDisposable
{
    /// <summary>A stopped instance still alive after this long is killed.</summary>
    public static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// A stopped instance that is recording or saving is killed only after this long: the game's own cap on finalising at
    /// quit (<c>GameHost.OnClose</c>: <c>StopAndWait</c>, two minutes) plus a margin for its goodbye and exit.
    /// </summary>
    public static readonly TimeSpan RecordingStopTimeout = TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(15);

    /// <summary>How long quitting the editor waits for a game that is not recording to exit before killing it.</summary>
    private static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(1);

    /// <summary>How often quitting the editor logs that it is still waiting for a recording to be saved.</summary>
    private static readonly TimeSpan DisposeProgressInterval = TimeSpan.FromSeconds(5);

    /// <summary>Exited/crashed instances kept in <see cref="Instances"/> (the oldest are dropped beyond this).</summary>
    public const int MaxExitedInstances = 8;

    /// <summary>The category given to game log entries that have none.</summary>
    public const string GameCategory = "game";

    private static readonly Func<DateTime> SystemClock = static () => DateTime.UtcNow;

    private readonly IGameBuilder _builder;
    private readonly IGameLauncher _launcher;
    private readonly Func<DateTime> _utcNow;
    private readonly List<PlayInstance> _instances = [];
    private readonly Dictionary<int, PlayInstance> _byGameId = [];
    private readonly ConcurrentQueue<string> _buildLines = new();
    private readonly List<PendingLaunch> _pendingLaunches = [];
    private readonly CancellationTokenSource _disposeSource = new();
    private EditorLinkServer? _server;
    private TaskCompletionSource<GameBuildResult>? _build;
    private GameBuildResult? _finishedBuild;
    private int _nextNumber;
    private long _lastRecordingTick;
    private bool _changed;
    private bool _disposed;

    public PlayService(IGameBuilder builder, IGameLauncher launcher, Func<DateTime>? utcNow = null)
    {
        _builder = builder ?? throw new ArgumentNullException(nameof(builder));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _utcNow = utcNow ?? SystemClock;
    }

    /// <summary>The editor-link server games connect to (created, on a free loopback port, on first use).</summary>
    public EditorLinkServer Link
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _server ??= new EditorLinkServer(0);
        }
    }

    /// <summary>The host flag that names the ffmpeg to record with (<c>GameHostOptions</c>).</summary>
    public const string FfmpegArgument = "--ffmpeg";

    /// <summary>The port passed to games as <c>--editor-port</c>.</summary>
    public int Port => Link.Port;

    /// <summary>
    /// The ffmpeg every launched game records with (Editor Settings › Video › ffmpeg), asked at each launch so a changed
    /// setting reaches the next game; null or empty passes nothing and the game looks for ffmpeg itself.
    /// </summary>
    public Func<string?>? FfmpegPath { get; set; }

    /// <summary>Alive instances and recently exited ones (until <see cref="ClearExited"/>), in launch order.</summary>
    public IReadOnlyList<PlayInstance> Instances => _instances;

    /// <summary>True while a build runs (until <see cref="Update"/> reports it finished).</summary>
    public bool IsBuilding => _build is not null;

    /// <summary>The last finished build.</summary>
    public GameBuildResult? LastBuild { get; private set; }

    /// <summary>Something about an instance or the build changed (raised at most once per <see cref="Update"/>).</summary>
    public event Action? Changed;

    /// <summary>An instance said hello for the first time (reconnects do not raise it). Raised from <see cref="Update"/>.</summary>
    public event Action<PlayInstance>? Connected;

    /// <summary>A recording stopped (saved) or failed. Raised on the editor thread from <see cref="Update"/>.</summary>
    public event Action<PlayInstance, EditorLinkRecording>? RecordingFinished;

    /// <summary>A game's log entry (category <see cref="GameCategory"/> when it had none).</summary>
    public event Action<PlayInstance, LogEntry>? GameLog;

    /// <summary>A raw stdout/stderr line of a game process.</summary>
    public event Action<PlayInstance, string>? GameOutput;

    /// <summary>
    /// A game crashed (G15): raised once per instance from <see cref="Update"/>, after its process exited, with the report
    /// it sent on the link (<see cref="PlayInstance.Crash"/>, protocol 5) or, without one, the decoded exit code and the
    /// native dump the runtime wrote (<see cref="PlayInstance.DumpPath"/>).
    /// </summary>
    public event Action<PlayInstance>? Crashed;

    /// <summary>How long an exited game's link may still deliver its crash report before the crash is reported without it.</summary>
    public static readonly TimeSpan CrashReportGrace = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The running project's crash folder (<c>{user data}/crashes</c>), asked at each launch: launched games write native
    /// dumps there (ADR 0221), and a crash without a link report looks for one. Null or empty: no dumps.
    /// </summary>
    public Func<string?>? CrashDirectory { get; set; }

    /// <summary>A build output line.</summary>
    public event Action<string>? BuildOutput;

    /// <summary>A build finished (also when it failed or was cancelled).</summary>
    public event Action<GameBuildResult>? BuildFinished;

    /// <summary>
    /// Starts building <paramref name="projectOrSolution"/> in the background. One build at a time: while one runs, its
    /// task is returned. The task completes from <see cref="Update"/>.
    /// </summary>
    public Task<GameBuildResult> BuildAsync(string projectOrSolution, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_build is { } running)
            return running.Task;
        var completion = new TaskCompletionSource<GameBuildResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _build = completion;
        _changed = true;
        _ = RunBuildAsync(projectOrSolution, ct);
        return completion.Task;
    }

    /// <summary>
    /// Launches <paramref name="programPath"/> with <paramref name="extraArguments"/>, <c>--editor-port</c> and (when given)
    /// <c>--scene</c>. The instance starts <see cref="PlayInstanceState.Launching"/>; a process that cannot start ends up
    /// <see cref="PlayInstanceState.Crashed"/> on the next <see cref="Update"/> with the reason in <see cref="GameOutput"/>.
    /// </summary>
    private static int IndexOf(IReadOnlyList<string> list, string value)
    {
        for (var i = 0; i < list.Count; i++)
            if (list[i] == value)
                return i;
        return -1;
    }

    public PlayInstance Launch(string programPath, string workingDirectory, string? scene, string label, IReadOnlyList<string>? extraArguments = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(programPath);
        ArgumentNullException.ThrowIfNull(workingDirectory);
        // Host flags first, then --editor-port/--scene, then the game's own arguments: everything after "++" belongs to the
        // game (GameHost.UserArgs), so the editor's flags must come before it.
        List<string> arguments = [];
        var separator = extraArguments is null ? -1 : IndexOf(extraArguments, GameHostOptions.UserArgsSeparator);
        if (extraArguments is not null)
            arguments.AddRange(separator < 0 ? extraArguments : extraArguments.Take(separator));
        // The ffmpeg setting is a host flag too, unless the caller already chose one for this launch.
        if (FfmpegPath?.Invoke() is { Length: > 0 } ffmpeg && IndexOf(arguments, FfmpegArgument) < 0)
        {
            arguments.Add(FfmpegArgument);
            arguments.Add(ffmpeg);
        }

        arguments.Add("--frame-capture"); // recording can start mid-session (the swapchain needs TRANSFER_SRC)
        arguments.Add("--editor-port");
        arguments.Add(Port.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrEmpty(scene))
        {
            arguments.Add("--scene");
            arguments.Add(scene);
        }

        if (separator >= 0)
            arguments.AddRange(extraArguments!.Skip(separator));

        var instance = new PlayInstance(++_nextNumber, string.IsNullOrEmpty(label) ? "Game" : label, scene, _utcNow(), arguments)
        {
            State = PlayInstanceState.Launching,
        };
        IGameProcess process;
        try
        {
            process = _launcher.Launch(new GameLaunchRequest(programPath, arguments, workingDirectory, instance.Label, DumpEnvironment()));
        }
        catch (Exception e) when (EditorCommands.IsRecoverable(e))
        {
            process = new FailedGameProcess($"Could not launch '{programPath}': {e.Message}");
        }

        instance.Process = process;
        instance.ProcessId = process.Id > 0 ? process.Id : null;
        _instances.Add(instance);
        _changed = true;
        return instance;
    }

    /// <summary>
    /// Builds <see cref="PlayRequest.BuildPath"/> (unless <see cref="PlayRequest.SkipBuild"/>; joins a build already
    /// running), then launches the desktop project's output from its own folder. Null when the build failed (see
    /// <see cref="LastBuild"/>/<see cref="BuildFinished"/>), was cancelled, or there is no program to run (logged).
    /// </summary>
    public Task<PlayInstance?> BuildAndLaunchAsync(PlayRequest request, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        if (request.SkipBuild)
            return Task.FromResult(LaunchBuilt(request));
        var completion = new TaskCompletionSource<PlayInstance?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingLaunches.Add(new PendingLaunch(request, completion, ct));
        _ = BuildAsync(request.BuildPath, ct);
        return completion.Task;
    }

    /// <summary>Drains build output, link messages and process output; updates instances; raises the events.</summary>
    public void Update()
    {
        if (_disposed)
            return;
        DrainBuild();
        if (_server is { } server)
            DrainLink(server);
        UpdateProcesses();
        TickRecording();
        if (_changed)
        {
            _changed = false;
            Changed?.Invoke();
        }
    }

    /// <summary>Stops <paramref name="instance"/> (null: every alive instance); see the remarks for the rules.</summary>
    public void Stop(PlayInstance? instance = null)
    {
        if (_disposed)
            return;
        if (instance is not null)
        {
            StopOne(instance);
            return;
        }

        foreach (var each in _instances)
            StopOne(each);
    }

    /// <summary>Stops every alive instance.</summary>
    public void StopAll() => Stop();

    /// <summary>Pauses <paramref name="instance"/>'s scene tree (null: every connected instance).</summary>
    public void Pause(PlayInstance? instance = null) => SendPause(instance, true);

    /// <summary>Resumes <paramref name="instance"/> (null: every connected instance).</summary>
    public void Resume(PlayInstance? instance = null) => SendPause(instance, false);

    /// <summary>Reloads <paramref name="scene"/> (null: the game's current scene) from disk in <paramref name="instance"/> (null: all).</summary>
    public void ReloadScene(PlayInstance? instance = null, string? scene = null) =>
        Send(instance, new EditorLinkCommand(EditorCommandKind.ReloadScene, scene ?? string.Empty));

    /// <summary>Asks <paramref name="instance"/> for a scene tree snapshot; it lands in <see cref="PlayInstance.RemoteTree"/> (false: not connected).</summary>
    public bool RequestTree(PlayInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return !_disposed && SendTo(instance, new EditorLinkCommand(EditorCommandKind.RequestTree));
    }

    /// <summary>True while any instance is recording or saving a recording.</summary>
    public bool AnyRecording
    {
        get
        {
            foreach (var instance in _instances)
                if (instance.Recording != PlayRecordingState.None)
                    return true;
            return false;
        }
    }

    /// <summary>
    /// Asks <paramref name="instance"/> to start recording to <paramref name="path"/> (false: not connected, already
    /// recording, or built against an engine whose link cannot record: <see cref="PlayInstance.CanRecord"/>).
    /// </summary>
    public bool StartRecording(PlayInstance instance, string path)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return !_disposed && instance.CanRecord && instance.Recording == PlayRecordingState.None &&
               SendTo(instance, new EditorLinkCommand(EditorCommandKind.StartRecording, path));
    }

    /// <summary>Asks <paramref name="instance"/> to stop recording; <see cref="RecordingFinished"/> follows once it has saved.</summary>
    public bool StopRecording(PlayInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (_disposed || instance.Recording != PlayRecordingState.Recording || !SendTo(instance, new EditorLinkCommand(EditorCommandKind.StopRecording)))
            return false;
        instance.Recording = PlayRecordingState.Saving;
        _changed = true;
        return true;
    }

    /// <summary>True when <paramref name="instance"/> is paused, or a pause was sent and not yet confirmed (false after a resume was sent).</summary>
    public bool IsPaused(PlayInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (_disposed || !instance.IsAlive)
            return false;
        return instance.PendingPause ?? instance.State == PlayInstanceState.Paused;
    }

    /// <summary>Removes exited and crashed instances from <see cref="Instances"/>.</summary>
    public void ClearExited()
    {
        for (var i = _instances.Count - 1; i >= 0; i--)
            if (!_instances[i].IsAlive)
                Remove(i);
    }

    /// <summary>
    /// Cancels the build, stops every game and closes the link. A game that does not exit within a second is killed, except
    /// one that is recording or saving: it is finalising its file, so the editor waits for it (logging every few seconds)
    /// up to <see cref="RecordingStopTimeout"/>, the same cap the game itself has.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        var saving = 0;
        foreach (var instance in _instances)
        {
            if (!instance.IsAlive)
                continue;
            if (instance.Recording != PlayRecordingState.None)
                saving++;
            if (instance.GameId is { } id && _server is { } server)
                server.SendCommand(id, new EditorLinkCommand(EditorCommandKind.Stop));
        }

        if (saving > 0)
            Log.Info($"[Play] Waiting for {saving} game(s) to save their recording before closing (at most {RecordingStopTimeout.TotalMinutes:0} minutes).");
        var start = DateTime.UtcNow;
        var nextReport = DisposeProgressInterval;
        while (WaitForExits(DateTime.UtcNow - start))
        {
            Thread.Sleep(10);
            var elapsed = DateTime.UtcNow - start;
            if (saving > 0 && elapsed >= nextReport)
            {
                Log.Info($"[Play] Still saving the recording… ({elapsed.TotalSeconds:0} s)");
                nextReport += DisposeProgressInterval;
            }
        }

        foreach (var instance in _instances)
        {
            if (instance.Process is not { } process)
                continue;
            if (!process.HasExited)
                process.Kill();
            process.Dispose();
        }

        _disposed = true;
        _disposeSource.Cancel();
        _server?.Dispose();
        _build?.TrySetResult(GameBuildResult.Failed("The editor closed.", cancelled: true));
        foreach (var pending in _pendingLaunches)
            pending.Completion.TrySetResult(null);
        _pendingLaunches.Clear();
        _disposeSource.Dispose();
    }

    private async Task RunBuildAsync(string path, CancellationToken ct)
    {
        GameBuildResult result;
        CancellationTokenSource? linked = null;
        try
        {
            linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposeSource.Token);
            var token = linked.Token;
            result = await Task.Run(() => _builder.BuildAsync(path, _buildLines.Enqueue, token), token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            result = GameBuildResult.Failed("The build was cancelled.", cancelled: true);
        }
        catch (Exception e) when (EditorCommands.IsRecoverable(e))
        {
            result = GameBuildResult.Failed($"The build failed: {e.Message}");
        }
        finally
        {
            linked?.Dispose();
        }

        Volatile.Write(ref _finishedBuild, result);
    }

    private void DrainBuild()
    {
        while (_buildLines.TryDequeue(out var line))
            BuildOutput?.Invoke(line);
        if (_build is not { } completion || Interlocked.Exchange(ref _finishedBuild, null) is not { } result)
            return;
        while (_buildLines.TryDequeue(out var line))
            BuildOutput?.Invoke(line);
        _build = null;
        LastBuild = result;
        _changed = true;
        BuildFinished?.Invoke(result);
        completion.TrySetResult(result);
        if (_pendingLaunches.Count == 0)
            return;
        PendingLaunch[] launches = [.. _pendingLaunches];
        _pendingLaunches.Clear();
        foreach (var pending in launches)
        {
            var launched = result.Succeeded && !pending.Cancellation.IsCancellationRequested && !_disposed
                ? LaunchBuilt(pending.Request)
                : null;
            pending.Completion.TrySetResult(launched);
        }
    }

    private PlayInstance? LaunchBuilt(PlayRequest request)
    {
        var program = ProcessGameLauncher.FindLauncherProgram(request.DesktopProject, request.Configuration);
        if (program is null)
        {
            Log.Error($"[Play] No build output for '{request.DesktopProject}' ({request.Configuration}); build the desktop project first.");
            return null;
        }

        return Launch(program, ProcessGameLauncher.WorkingDirectoryOf(program), request.Scene, request.Label, request.ExtraArguments);
    }

    // G15 (ADR 0221): the runtime writes a dump of a native crash into the game's crash folder.
    private Dictionary<string, string?>? DumpEnvironment()
    {
        if (CrashDirectory?.Invoke() is not { Length: > 0 } directory)
            return null;
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (name, value) in NativeDumps.Environment(directory, NativeDumpMode.Mini))
            environment[name] = value;
        return environment;
    }

    private void OnCrashReport(PlayInstance instance, in EditorLinkCrashReport report)
    {
        try
        {
            instance.Crash = CrashReport.FromJson(report.Json);
            instance.Crash.Folder = report.Folder.Length > 0 ? report.Folder : null;
            instance.CrashFolder = instance.Crash.Folder;
        }
        catch (System.Text.Json.JsonException e)
        {
            Log.Warning($"[Play] {instance.Label} #{instance.Number} sent a crash report the editor cannot read: {e.Message}");
            return;
        }

        _changed = true;
        if (!instance.IsAlive && !instance.CrashRaised && instance.State == PlayInstanceState.Crashed)
            RaiseCrashed(instance);
    }

    // Once per instance: the report, or else the dump the runtime wrote for its process.
    private void RaiseCrashed(PlayInstance instance)
    {
        if (instance.CrashRaised)
            return;
        instance.CrashRaised = true;
        instance.CrashReportDeadlineUtc = null;
        if (instance.Crash is null && instance.ProcessId is { } pid && CrashDirectory?.Invoke() is { Length: > 0 } directory)
        {
            var dump = NativeDumps.PathFor(directory, pid);
            if (File.Exists(dump))
                instance.DumpPath = dump;
        }

        _changed = true;
        Crashed?.Invoke(instance);
    }

    private void DrainLink(EditorLinkServer server)
    {
        while (server.TryRead(out var message))
        {
            switch (message.Type)
            {
                case EditorLinkMessageType.Hello:
                    OnHello(message.GameId, message.Hello);
                    break;
                case EditorLinkMessageType.Log:
                    if (_byGameId.TryGetValue(message.GameId, out var logger))
                    {
                        var entry = message.Log;
                        GameLog?.Invoke(logger, entry.Category.Length == 0 ? entry with { Category = GameCategory } : entry);
                    }

                    break;
                case EditorLinkMessageType.Status:
                    if (_byGameId.TryGetValue(message.GameId, out var reporter))
                        OnStatus(reporter, message.Status);
                    break;
                case EditorLinkMessageType.Tree:
                    if (_byGameId.TryGetValue(message.GameId, out var snapshotter) && message.Tree is { } nodes)
                    {
                        snapshotter.RemoteTree = nodes;
                        snapshotter.RemoteTreeTruncated = message.TreeTruncated;
                        snapshotter.RemoteTreeVersion++;
                        _changed = true;
                    }

                    break;
                case EditorLinkMessageType.Recording:
                    if (_byGameId.TryGetValue(message.GameId, out var recorder))
                        OnRecording(recorder, message.Recording);
                    break;
                case EditorLinkMessageType.CrashReport:
                    if (_byGameId.TryGetValue(message.GameId, out var crasher))
                        OnCrashReport(crasher, message.CrashReport);
                    break;
                case EditorLinkMessageType.LogDropped:
                    if (_byGameId.TryGetValue(message.GameId, out var dropper))
                        GameLog?.Invoke(dropper, new LogEntry(Log.Level.Warning, _utcNow(), GameCategory,
                            $"{message.DroppedCount} log entries were dropped (the game logged faster than the link could send).", "", "", 0));
                    break;
                case EditorLinkMessageType.Goodbye:
                    if (_byGameId.TryGetValue(message.GameId, out var leaving))
                    {
                        leaving.GoodbyeReceived = true;
                        if (leaving.IsAlive)
                        {
                            leaving.ExitCode = message.ExitCode;
                            leaving.State = PlayInstanceState.Stopping;
                            _changed = true;
                        }
                    }

                    break;
                case EditorLinkMessageType.Disconnected:
                    if (_byGameId.Remove(message.GameId, out var gone))
                    {
                        if (gone.GameId == message.GameId)
                            gone.GameId = null;
                        AbandonRecording(gone);
                        _changed = true;
                    }

                    break;
            }
        }
    }

    private void OnHello(int gameId, in EditorLinkHello hello)
    {
        PlayInstance? match = null;
        foreach (var instance in _instances)
            if (instance.IsAlive && instance.ProcessId == hello.ProcessId)
            {
                match = instance;
                break;
            }

        if (match is null)
            foreach (var instance in _instances)
                if (instance.State == PlayInstanceState.Launching && instance.GameId is null)
                {
                    match = instance;
                    break;
                }

        if (match is null)
            return; // not one of ours (a game started by hand with --editor-port)

        if (match.GameId is { } previous)
            _byGameId.Remove(previous);
        match.GameId = gameId;
        _byGameId[gameId] = match;
        var first = !match.HasConnected;
        if (first)
        {
            match.HasConnected = true;
            match.ConnectTime = _utcNow() - match.StartedUtc;
        }

        if (match.State == PlayInstanceState.Launching)
            match.State = PlayInstanceState.Running;
        _changed = true;
        match.ProtocolVersion = hello.ProtocolVersion;
        if (hello.ProtocolVersion != EditorLinkProtocol.Version)
            GameLog?.Invoke(match, new LogEntry(Log.Level.Warning, _utcNow(), GameCategory,
                $"The game speaks editor-link protocol {hello.ProtocolVersion}; the editor speaks {EditorLinkProtocol.Version}. Rebuild the game.", "", "", 0));
        if (first)
            Connected?.Invoke(match);
        if (match.StopRequestedUtc is not null && match.IsAlive)
            Link.SendCommand(gameId, new EditorLinkCommand(EditorCommandKind.Stop)); // stopped while connecting
    }

    private void OnStatus(PlayInstance instance, in EditorLinkStatus status)
    {
        if (instance.Frame != status.Frame || instance.FramesPerSecond != status.FramesPerSecond)
        {
            instance.Frame = status.Frame;
            instance.FramesPerSecond = status.FramesPerSecond;
            _changed = true;
        }

        if (!string.Equals(instance.CurrentScene, status.ScenePath, StringComparison.Ordinal))
        {
            instance.CurrentScene = status.ScenePath;
            _changed = true;
        }

        if (!instance.IsAlive || instance.State == PlayInstanceState.Stopping)
            return; // never back from Stopping
        var state = status.State switch
        {
            GameRunState.Paused => PlayInstanceState.Paused,
            GameRunState.Stopping => PlayInstanceState.Stopping,
            _ => PlayInstanceState.Running,
        };
        if (instance.PendingPause is { } pause && pause == (state == PlayInstanceState.Paused))
            instance.PendingPause = null;
        if (instance.State != state)
        {
            instance.State = state;
            _changed = true;
        }
    }

    private void OnRecording(PlayInstance instance, in EditorLinkRecording recording)
    {
        // Nothing may resurrect state on an instance whose link is gone. A finishing report is still taken from an exited
        // process whose link is not yet seen as closed: the game sends Stopped, then exits, and the exit can be polled
        // before the reader thread has queued that Stopped.
        if (instance.GameId is null || (!instance.IsAlive && recording.State == EditorLinkRecordingState.Started))
            return;
        switch (recording.State)
        {
            case EditorLinkRecordingState.Started:
                // A Start that reached a game already recording is answered with the running recording's Started (e.g.
                // --record had started it): the same recording, so its elapsed time keeps counting from the first.
                var same = instance.Recording == PlayRecordingState.Recording && string.Equals(instance.RecordingPath, recording.Path, StringComparison.Ordinal);
                instance.Recording = PlayRecordingState.Recording;
                instance.RecordingPath = recording.Path;
                if (!same || instance.RecordingStartedUtc is null)
                    instance.RecordingStartedUtc = _utcNow();
                if (instance.StopDeadlineUtc is { } deadline)
                {
                    // Started after Stop (e.g. Play and Record stopped while launching): the game finalises this file before it exits.
                    instance.Recording = PlayRecordingState.Saving;
                    var saveDeadline = instance.StopRequestedUtc!.Value + RecordingStopTimeout;
                    if (saveDeadline > deadline)
                        instance.StopDeadlineUtc = saveDeadline;
                }

                break;
            default:
                instance.Recording = PlayRecordingState.None;
                instance.RecordingStartedUtc = null;
                if (instance.StopDeadlineUtc is not null)
                    instance.StopDeadlineUtc = _utcNow() + StopTimeout; // saved: a fresh grace for its goodbye and exit, not the deadline left from Stop
                RecordingFinished?.Invoke(instance, recording);
                break;
        }

        _changed = true;
    }

    /// <summary>
    /// The game's link is gone (disconnected, or exited with no link) with no Stopped/Failed received: finish the recording as failed so the
    /// editor never waits for a save that cannot come.
    /// </summary>
    private void AbandonRecording(PlayInstance instance)
    {
        if (instance.Recording == PlayRecordingState.None)
            return;
        instance.Recording = PlayRecordingState.None;
        instance.RecordingStartedUtc = null;
        _changed = true;
        RecordingFinished?.Invoke(instance, new EditorLinkRecording(EditorLinkRecordingState.Failed, instance.RecordingPath ?? string.Empty, 0, 0, 0,
            "The game exited before the recording was saved."));
    }

    /// <summary>Marks the service changed once per wall-clock second while recording, so the elapsed-time readout refreshes.</summary>
    private void TickRecording()
    {
        if (!AnyRecording)
            return;
        var second = _utcNow().Ticks / TimeSpan.TicksPerSecond;
        if (second == _lastRecordingTick)
            return;
        _lastRecordingTick = second;
        _changed = true;
    }

    private void UpdateProcesses()
    {
        var exitedCount = 0;
        for (var i = 0; i < _instances.Count; i++)
        {
            var instance = _instances[i];
            if (instance.Process is not { } process)
                continue;
            if (!instance.IsAlive)
            {
                exitedCount++;
                DrainOutput(instance, process); // late lines
                if (instance.CrashReportDeadlineUtc is { } crashDeadline && (instance.GameId is null || _utcNow() >= crashDeadline))
                    RaiseCrashed(instance); // the link closed (or the grace ran out) without a report
                continue;
            }

            // Exit first, then the output: a process that exited has all of its output queued.
            var exited = process.HasExited;
            DrainOutput(instance, process);
            if (exited)
            {
                var code = process.ExitCode ?? instance.ExitCode ?? -1;
                instance.ExitCode = code;
                instance.State = code == 0 || instance.StopRequestedUtc is not null ? PlayInstanceState.Exited : PlayInstanceState.Crashed;
                instance.PendingPause = null;
                if (instance.GameId is null)
                    AbandonRecording(instance); // with a link still up, its Stopped/Failed (then Disconnected) may be unread: wait for them
                if (instance.State == PlayInstanceState.Crashed)
                {
                    // G15: a protocol-5 game sends its report before the goodbye; the reader may not have queued it yet.
                    if (instance.Crash is null && instance.GameId is not null && instance.ProtocolVersion >= EditorLinkProtocol.MinCrashReportVersion)
                        instance.CrashReportDeadlineUtc = _utcNow() + CrashReportGrace;
                    else if (instance.ProcessId is not null) // a launch that failed is reported by its output, not as a crash
                        RaiseCrashed(instance);
                }

                _changed = true;
                exitedCount++;
                continue;
            }

            if (instance.StopDeadlineUtc is { } deadline && !instance.Killed && _utcNow() >= deadline)
            {
                instance.Killed = true;
                process.Kill();
            }
        }

        if (exitedCount > MaxExitedInstances)
            TrimExited(exitedCount - MaxExitedInstances);
    }

    private void DrainOutput(PlayInstance instance, IGameProcess process)
    {
        while (process.TryReadOutput(out var line))
            GameOutput?.Invoke(instance, line);
    }

    private void StopOne(PlayInstance instance)
    {
        if (!instance.IsAlive || instance.StopRequestedUtc is not null)
            return;
        instance.StopRequestedUtc = _utcNow();
        var sent = instance.GameId is { } id && _server is { } server && server.SendCommand(id, new EditorLinkCommand(EditorCommandKind.Stop));
        if (!sent)
        {
            // Never connected (or the link is gone): nothing can ask it to quit.
            instance.Killed = true;
            instance.Process?.Kill();
        }

        instance.State = PlayInstanceState.Stopping;
        if (instance.Recording == PlayRecordingState.Recording && !instance.Killed)
            instance.Recording = PlayRecordingState.Saving; // the game finalises the file before it exits
        instance.StopDeadlineUtc = instance.StopRequestedUtc + (instance.Recording == PlayRecordingState.None ? StopTimeout : RecordingStopTimeout);
        _changed = true;
    }

    private void SendPause(PlayInstance? instance, bool pause)
    {
        if (_disposed)
            return;
        var command = new EditorLinkCommand(pause ? EditorCommandKind.Pause : EditorCommandKind.Resume);
        if (instance is not null)
        {
            if (SendTo(instance, command))
                instance.PendingPause = pause;
            return;
        }

        foreach (var each in _instances)
            if (SendTo(each, command))
                each.PendingPause = pause;
    }

    private void Send(PlayInstance? instance, in EditorLinkCommand command)
    {
        if (_disposed)
            return;
        if (instance is not null)
        {
            SendTo(instance, command);
            return;
        }

        foreach (var each in _instances)
            SendTo(each, command);
    }

    private bool SendTo(PlayInstance instance, in EditorLinkCommand command)
    {
        if (!instance.IsAlive || instance.State == PlayInstanceState.Stopping || instance.GameId is not { } id || _server is not { } server)
            return false;
        if (!server.SendCommand(id, command))
            return false;
        _changed = true;
        return true;
    }

    private void TrimExited(int count)
    {
        for (var i = 0; i < _instances.Count && count > 0;)
        {
            if (!_instances[i].IsAlive)
            {
                Remove(i);
                count--;
            }
            else
            {
                i++;
            }
        }
    }

    private void Remove(int index)
    {
        var instance = _instances[index];
        _instances.RemoveAt(index);
        // An exited game's last messages (its Stopped, then the Disconnected) can still be queued on the link: a recording
        // instance stays reachable by its connection until the Disconnected, so the saved file is still reported (or the
        // recording abandoned) even after the instance left the list.
        if (instance.GameId is { } id && instance.Recording == PlayRecordingState.None)
            _byGameId.Remove(id);
        instance.Process?.Dispose();
        _changed = true;
    }

    /// <summary>
    /// One step of <see cref="Dispose"/>'s wait: kills each game still running past its limit (a second, or
    /// <see cref="RecordingStopTimeout"/> while it records or saves); true while one is still within its limit.
    /// </summary>
    private bool WaitForExits(TimeSpan elapsed)
    {
        var waiting = false;
        foreach (var instance in _instances)
        {
            if (instance.Process is not { HasExited: false } process || instance.Killed)
                continue;
            var limit = instance.Recording == PlayRecordingState.None ? DisposeTimeout : RecordingStopTimeout;
            if (elapsed < limit)
            {
                waiting = true;
                continue;
            }

            instance.Killed = true;
            process.Kill();
        }

        return waiting;
    }

    private sealed record PendingLaunch(PlayRequest Request, TaskCompletionSource<PlayInstance?> Completion, CancellationToken Cancellation);
}

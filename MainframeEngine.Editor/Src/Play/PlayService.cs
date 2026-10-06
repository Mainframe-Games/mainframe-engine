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
/// <see cref="StopTimeout"/> later; an instance that never connected is killed at once.</para>
/// </remarks>
public sealed class PlayService : IDisposable
{
    /// <summary>A stopped instance still alive after this long is killed.</summary>
    public static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(3);

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

    /// <summary>The port passed to games as <c>--editor-port</c>.</summary>
    public int Port => Link.Port;

    /// <summary>Alive instances and recently exited ones (until <see cref="ClearExited"/>), in launch order.</summary>
    public IReadOnlyList<PlayInstance> Instances => _instances;

    /// <summary>True while a build runs (until <see cref="Update"/> reports it finished).</summary>
    public bool IsBuilding => _build is not null;

    /// <summary>The last finished build.</summary>
    public GameBuildResult? LastBuild { get; private set; }

    /// <summary>Something about an instance or the build changed (raised at most once per <see cref="Update"/>).</summary>
    public event Action? Changed;

    /// <summary>A game's log entry (category <see cref="GameCategory"/> when it had none).</summary>
    public event Action<PlayInstance, LogEntry>? GameLog;

    /// <summary>A raw stdout/stderr line of a game process.</summary>
    public event Action<PlayInstance, string>? GameOutput;

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
            process = _launcher.Launch(new GameLaunchRequest(programPath, arguments, workingDirectory, instance.Label));
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

    /// <summary>Cancels the build, stops every game (killing those that do not exit within a second) and closes the link.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        foreach (var instance in _instances)
            if (instance.IsAlive && instance.GameId is { } id && _server is { } server)
                server.SendCommand(id, new EditorLinkCommand(EditorCommandKind.Stop));

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(1);
        while (DateTime.UtcNow < deadline && AnyProcessRunning())
            Thread.Sleep(10);
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

        return Launch(program, Path.GetDirectoryName(program)!, request.Scene, request.Label, request.ExtraArguments);
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
        if (!match.HasConnected)
        {
            match.HasConnected = true;
            match.ConnectTime = _utcNow() - match.StartedUtc;
        }

        if (match.State == PlayInstanceState.Launching)
            match.State = PlayInstanceState.Running;
        _changed = true;
        if (hello.ProtocolVersion != EditorLinkProtocol.Version)
            GameLog?.Invoke(match, new LogEntry(Log.Level.Warning, _utcNow(), GameCategory,
                $"The game speaks editor-link protocol {hello.ProtocolVersion}; the editor speaks {EditorLinkProtocol.Version}. Rebuild the game.", "", "", 0));
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
                _changed = true;
                exitedCount++;
                continue;
            }

            if (instance.StopRequestedUtc is { } asked && !instance.Killed && _utcNow() - asked >= StopTimeout)
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
        if (instance.GameId is { } id)
            _byGameId.Remove(id);
        instance.Process?.Dispose();
        _changed = true;
    }

    private bool AnyProcessRunning()
    {
        foreach (var instance in _instances)
            if (instance.Process is { HasExited: false })
                return true;
        return false;
    }

    private sealed record PendingLaunch(PlayRequest Request, TaskCompletionSource<PlayInstance?> Completion, CancellationToken Cancellation);
}

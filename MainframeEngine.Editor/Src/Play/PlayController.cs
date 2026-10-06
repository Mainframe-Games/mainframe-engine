using System.Globalization;

namespace MainframeEngine.Editor;

/// <summary>
/// The editor side of Play (toolbar ▶ / scene ▶ / ⏸ / ⏹, F5–F8, the Run menu): saves the scenes, builds and launches
/// the game through the <see cref="PlayService"/>, streams game logs (category <c>game</c>), process output and build
/// diagnostics (click-to-source) into the Output panel, and drives pause/resume/stop/reload-scene for one or several
/// running instances.
/// </summary>
public sealed class PlayController : IDisposable
{
    private readonly EditorWorkspace _workspace;
    private PlayRequest? _lastRequest;
    private int _extraInstances;

    public PlayController(EditorWorkspace workspace, IGameBuilder builder, IGameLauncher launcher)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        Service = new PlayService(builder, launcher);
        Service.GameLog += OnGameLog;
        Service.GameOutput += OnGameOutput;
        Service.BuildOutput += OnBuildOutput;
        Service.BuildFinished += OnBuildFinished;
        Service.Changed += () => Changed?.Invoke();
    }

    public PlayService Service { get; }

    /// <summary>True while any game instance is alive.</summary>
    public bool IsPlaying
    {
        get
        {
            foreach (var instance in Service.Instances)
                if (instance.IsAlive)
                    return true;
            return false;
        }
    }

    /// <summary>True when every alive instance is paused (and one is).</summary>
    public bool IsPaused
    {
        get
        {
            var any = false;
            foreach (var instance in Service.Instances)
            {
                if (!instance.IsAlive)
                    continue;
                if (!Service.IsPaused(instance))
                    return false;
                any = true;
            }

            return any;
        }
    }

    /// <summary>A build or a launch waiting for its build is in progress.</summary>
    public bool IsBuilding => Service.IsBuilding;

    /// <summary>Instances or build state changed (the toolbar refreshes).</summary>
    public event Action? Changed;

    /// <summary>Main thread, every frame.</summary>
    public void Update()
    {
        Service.Update();
        LaunchQueuedInstances();
    }

    // ── Play Instances (project.mfproj "playInstances", Godot's Customize Run Instances) ──

    private readonly List<(PlayRequest Request, double Delay)> _queued = [];
    private long _queueStart;

    /// <summary>
    /// Ctrl/Cmd+F5: every <see cref="ProjectSettings.PlayInstances"/> entry, built once, each started its delay after the
    /// first (its game arguments after <c>++</c>); the main scene once when the project lists none.
    /// </summary>
    public void PlayInstances()
    {
        var instances = _workspace.Session.Project?.PlayInstances;
        if (instances is not { Count: > 0 })
        {
            PlayMain();
            return;
        }

        if (IsBuilding || _queued.Count > 0)
        {
            Log.Info("[Play] A build or a launch is already running.");
            return;
        }

        var first = instances[0];
        Play(scene: null, first.Label, ["++", .. first.Arguments]);
        if (_lastRequest is not { } started)
            return; // Play refused (no project, no desktop project)
        for (var i = 1; i < instances.Count; i++)
            _queued.Add((started with { Label = instances[i].Label, ExtraArguments = ["++", .. instances[i].Arguments], SkipBuild = true },
                Math.Max(0, instances[i].DelaySeconds - first.DelaySeconds)));
        _queueStart = 0;
    }

    /// <summary>Instances of the last Play Instances still waiting for their delay (tests, the toolbar).</summary>
    public int QueuedInstances => _queued.Count;

    private void LaunchQueuedInstances()
    {
        if (_queued.Count == 0)
            return;
        if (Service.IsBuilding)
            return; // the delays count from the first instance's start, after the build
        if (_queueStart == 0)
        {
            if (!Service.Instances.Any(i => i.IsAlive))
            {
                _queued.Clear(); // the build failed or the first instance never started
                return;
            }

            _queueStart = System.Diagnostics.Stopwatch.GetTimestamp();
        }

        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(_queueStart).TotalSeconds;
        for (var i = 0; i < _queued.Count; i++)
        {
            if (_queued[i].Delay > elapsed)
                continue;
            var request = _queued[i].Request;
            _queued.RemoveAt(i--);
            _ = Service.BuildAndLaunchAsync(request);
            Changed?.Invoke();
        }
    }

    // ── Commands ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>F5: runs the project's main scene.</summary>
    public void PlayMain() => Play(scene: null, label: "Game");

    /// <summary>Runs the main scene with extra game arguments (QA scripts: <c>--screenshot</c>).</summary>
    public void PlayMain(IReadOnlyList<string> extraArguments) => Play(scene: null, label: "Game", extraArguments);

    /// <summary>F6: runs the active scene (saving it first; an untitled scene asks for a file).</summary>
    public void PlayCurrent()
    {
        if (_workspace.Session.Active is not { } scene)
        {
            Log.Warning("[Play] No scene is open.");
            return;
        }

        if (scene.FilePath is null)
        {
            _workspace.Commands.Save(scene, saveAs: false, then: saved =>
            {
                if (saved)
                    PlayCurrent();
            });
            return;
        }

        Play(SceneArgument(scene.FilePath), label: Path.GetFileNameWithoutExtension(scene.FilePath));
    }

    /// <summary>Launches one more instance of the last run (a second client, a server + client test).</summary>
    public void PlayAnotherInstance()
    {
        if (_lastRequest is null)
        {
            PlayMain();
            return;
        }

        var label = $"{_lastRequest.Label} {++_extraInstances + 1}";
        Launch(_lastRequest with { Label = label, SkipBuild = !NeedsBuild() });
    }

    /// <summary>F7: pauses every running game, or resumes them when all are paused.</summary>
    public void TogglePause()
    {
        if (!IsPlaying)
            return;
        if (IsPaused)
            Service.Resume();
        else
            Service.Pause();
        Changed?.Invoke();
    }

    /// <summary>F8: stops every running game (and instances still waiting to start).</summary>
    public void Stop()
    {
        _queued.Clear();
        Service.Stop();
        Changed?.Invoke();
    }

    /// <summary>Asks the running games to reload their scene from disk (after saving the open scenes).</summary>
    public void ReloadScene()
    {
        if (!IsPlaying)
            return;
        _workspace.Commands.SaveAll();
        Service.ReloadScene();
    }

    private void Play(string? scene, string label, IReadOnlyList<string>? extraArguments = null)
    {
        var project = _workspace.Project;
        if (project.Root is null || project.DesktopProject is null || project.BuildPath is null)
        {
            _workspace.Message.Show(new MessageRequest
            {
                Title = "Play",
                Message = project.Root is null
                    ? "Open a game project to play it (File › Open Project…)."
                    : "This project has no desktop project (*.Desktop/*.Desktop.csproj) to run.",
                Buttons = ["OK"],
            });
            return;
        }

        if (IsBuilding)
        {
            Log.Info("[Play] A build is already running.");
            return;
        }

        // Play saves (Godot does too): the game reads the scenes from disk. Untitled scenes stay unsaved.
        if (!_workspace.Commands.SaveAll())
            Log.Warning("[Play] Some scenes could not be saved; the game runs what is on disk.");
        _extraInstances = 0;
        Launch(new PlayRequest(project.BuildPath, project.DesktopProject, scene, label, extraArguments));
    }

    private void Launch(PlayRequest request)
    {
        _lastRequest = request with { SkipBuild = false };
        if (!request.SkipBuild)
            Log.Info($"[Play] Building {Path.GetFileName(request.BuildPath)}…");
        _ = Service.BuildAndLaunchAsync(request);
        Changed?.Invoke();
    }

    private bool NeedsBuild() => _workspace.Project.NeedsRebuild;

    /// <summary>The <c>--scene</c> argument for a scene file: its UID when known, else its project path.</summary>
    public static string SceneArgument(string file)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        var database = AssetDatabase.Current;
        return database.GetUid(file) ?? database.ToProjectPath(file);
    }

    // ── Output ───────────────────────────────────────────────────────────────────────────────────────────────────

    private string? LabelFor(PlayInstance instance)
    {
        var alive = 0;
        foreach (var each in Service.Instances)
            if (each.IsAlive || ReferenceEquals(each, instance))
                alive++;
        return alive > 1 ? instance.Label : null;
    }

    private void OnGameLog(PlayInstance instance, LogEntry entry) => _workspace.Output.AddGame(entry, LabelFor(instance));

    private void OnGameOutput(PlayInstance instance, string line)
    {
        // A connected game's log arrives on the link; its console copy is noise. Before it connects (or when it
        // crashes before connecting), stdout/stderr is all there is.
        if (instance.HasConnected && instance.IsAlive)
            return;
        var (level, text) = ParseConsoleLine(line);
        _workspace.Output.Add(level, $"[game·{instance.Label}] {text}");
    }

    /// <summary>
    /// A game's console line as an Output level and text: the engine's own lines (<c>[12:00:00.123] [WARN]\t[Audio]
    /// message</c>, <see cref="ConsoleLogSink"/>) lose the time stamp (the Output panel shows its own) and keep their
    /// level; other lines are Info, or Error when they look like a crash.
    /// </summary>
    internal static (OutputLevel Level, string Text) ParseConsoleLine(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var tab = line.IndexOf('\t');
        var split = tab > 0 ? line.IndexOf("] [", 0, tab, StringComparison.Ordinal) : -1;
        if (split > 0 && line[0] == '[' && line[tab - 1] == ']')
        {
            OutputLevel? tagged = line.AsSpan(split + 3, tab - split - 4) switch
            {
                "Debug" => OutputLevel.Debug,
                "INFO" => OutputLevel.Info,
                "WARN" => OutputLevel.Warning,
                "ERROR" or "FATAL" => OutputLevel.Error,
                _ => null,
            };
            if (tagged is { } known)
                return (known, line[(tab + 1)..]);
        }

        var crash = line.Contains("[ERROR]", StringComparison.Ordinal) || line.Contains("[FATAL]", StringComparison.Ordinal) ||
                    line.Contains("Unhandled exception", StringComparison.Ordinal);
        return (crash ? OutputLevel.Error : OutputLevel.Info, line);
    }

    private void OnBuildOutput(string line)
    {
        // Diagnostics are reported once, parsed, when the build finishes; the rest of MSBuild's chatter is dropped.
    }

    private void OnBuildFinished(GameBuildResult result)
    {
        var output = _workspace.Output;
        foreach (var diagnostic in result.Diagnostics)
        {
            var level = diagnostic.Severity switch
            {
                BuildDiagnosticSeverity.Error => OutputLevel.Error,
                BuildDiagnosticSeverity.Warning => OutputLevel.Warning,
                _ => OutputLevel.Info,
            };
            var where = diagnostic.File is null ? "" : $"{Path.GetFileName(diagnostic.File)}({diagnostic.Line},{diagnostic.Column}): ";
            output.Add(level, $"[build] {where}{diagnostic.Code}: {diagnostic.Message}", diagnostic.File, diagnostic.Line);
        }

        var seconds = result.Duration.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture);
        if (result.Succeeded)
            output.Add(OutputLevel.Info, $"[build] Build succeeded in {seconds} s.");
        else if (result.Cancelled)
            output.Add(OutputLevel.Warning, "[build] Build cancelled.");
        else
            output.Add(OutputLevel.Error, $"[build] Build failed in {seconds} s: {result.Error ?? $"{Count(result, BuildDiagnosticSeverity.Error)} error(s)"}. Click an error to open it.");
        Changed?.Invoke();
    }

    private static int Count(GameBuildResult result, BuildDiagnosticSeverity severity)
    {
        var count = 0;
        foreach (var diagnostic in result.Diagnostics)
            if (diagnostic.Severity == severity)
                count++;
        return count;
    }

    public void Dispose()
    {
        Service.GameLog -= OnGameLog;
        Service.GameOutput -= OnGameOutput;
        Service.BuildOutput -= OnBuildOutput;
        Service.BuildFinished -= OnBuildFinished;
        Service.Dispose();
    }
}

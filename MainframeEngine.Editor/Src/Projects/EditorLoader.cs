using System.Diagnostics;

namespace MainframeEngine.Editor;

/// <summary>
/// Opening a project or a scene behind the loading window (the <see cref="SplashScreen"/>), without blocking the main
/// loop: the window keeps pumping events and redrawing, so the OS never reports the editor as hung. A job
/// (<see cref="Run"/>) runs its work two frames after the window shows; the work may keep the job open with a
/// <see cref="Hold"/> (the game code building) or a tracked <see cref="SceneLoad"/> (<see cref="Track"/>: a scene opening
/// through <see cref="EditorSession.OpenAsync"/>, whose worker builds it while frames go on). The job ends when its work
/// has run and nothing holds it; each frame shows its title, step, detail and progress, and Cancel stops it.
/// </summary>
/// <remarks>
/// Without the splash (<see cref="EditorWorkspaceOptions.ShowSplash"/> off: tests and scripted runs) a job runs at once
/// and scenes open synchronously (<see cref="IsAsync"/> false), as before. While a job runs, a code reload waits
/// (<see cref="ProjectService"/>), so no worker holds game objects while their assembly unloads.
/// </remarks>
public sealed class EditorLoader
{
    private readonly EditorWorkspace _workspace;
    private readonly Queue<(string Title, Action Work)> _queued = [];
    private readonly Stopwatch _watch = new();
    private string? _title;
    private Action? _work;
    private int _workDelay;
    private int _holds;
    private SceneLoad? _scene;
    private string? _sceneName;
    private float _sceneFrom, _sceneTo;
    private Action<SceneLoad>? _sceneEnded;
    private long _lastFrame;
    private TimeSpan _longestFrame;
    private string _step = "";
    private string? _detail;
    private float _progress;

    internal EditorLoader(EditorWorkspace workspace) => _workspace = workspace;

    /// <summary>True when jobs run behind the loading window and scenes open asynchronously (the splash is on).</summary>
    public bool IsAsync => _workspace.Options.ShowSplash;

    /// <summary>A job is running (its work has not run yet, something holds it, or its scene is loading).</summary>
    public bool IsBusy => _title is not null;

    /// <summary>Cancel was pressed during this job: work that continues it (the start scene after a build) stops.</summary>
    public bool IsCancelled { get; private set; }

    /// <summary>The running job's title ("Opening Forest Demo"), or null.</summary>
    public string? Title => _title;

    /// <summary>What the job is doing now ("Building game code", "Growing trees", "Decoding textures").</summary>
    public string Step => _step;

    /// <summary>A detail line for the step ("112 / 274 textures"), or null.</summary>
    public string? Detail => _detail;

    /// <summary>The job's progress, 0–1, monotonic.</summary>
    public float Progress => _progress;

    /// <summary>Time since the job started.</summary>
    public TimeSpan Elapsed => _watch.Elapsed;

    /// <summary>The longest frame of the running job so far (the time between two editor frames).</summary>
    public TimeSpan LongestFrame => _longestFrame;

    /// <summary>The last finished job: its title, duration and longest frame (QA's <c>state</c>), or null before one.</summary>
    public (string Title, TimeSpan Duration, TimeSpan LongestFrame)? LastJob { get; private set; }

    /// <summary>
    /// Runs <paramref name="work"/> (opening a project, a scene) behind the loading window: the window shows
    /// <paramref name="title"/> first, the work runs once that frame has been presented. A job started while another runs
    /// waits for it. Without the splash it runs at once.
    /// </summary>
    public void Run(string title, Action work)
    {
        ArgumentException.ThrowIfNullOrEmpty(title);
        ArgumentNullException.ThrowIfNull(work);
        if (!IsAsync)
        {
            work();
            return;
        }

        if (IsBusy)
        {
            _queued.Enqueue((title, work));
            return;
        }

        Begin(title, work);
    }

    /// <summary>
    /// Keeps the running job open until the returned action is called (once; later calls do nothing): work that finishes
    /// in a later frame, such as the game code's build. Does nothing outside a job.
    /// </summary>
    public Action Hold()
    {
        if (!IsBusy)
            return static () => { };
        _holds++;
        var released = false;
        return () =>
        {
            if (released)
                return;
            released = true;
            _holds = Math.Max(0, _holds - 1);
        };
    }

    /// <summary>Shows <paramref name="step"/> and moves the bar to at least <paramref name="progress"/> (inside a job).</summary>
    public void SetStep(string step, float progress, string? detail = null)
    {
        if (!IsBusy)
            return;
        _step = step;
        _detail = detail;
        _progress = Math.Max(_progress, Math.Clamp(progress, 0f, 1f));
    }

    /// <summary>
    /// Keeps the job open until <paramref name="load"/> completes, showing its stages between <paramref name="from"/> and
    /// <paramref name="to"/> of the bar. <paramref name="ended"/> runs when it completed (done, failed or cancelled); without
    /// it a failure is reported. A cancelled load whose scene already entered a tab closes that tab.
    /// </summary>
    public void Track(SceneLoad load, string name, float from = 0f, float to = 1f, Action<SceneLoad>? ended = null)
    {
        ArgumentNullException.ThrowIfNull(load);
        if (_scene is not null)
            throw new InvalidOperationException("A job tracks one scene load at a time.");
        _scene = load;
        _sceneName = name;
        _sceneFrom = from;
        _sceneTo = Math.Max(from, to);
        _sceneEnded = ended;
        if (IsCancelled)
            load.Cancel();
    }

    /// <summary>
    /// Cancels the running job: work not started yet is dropped, a scene load stops (its worker at its next check; a scene
    /// already in a tab is closed), and the job ends once what holds it lets go (a build runs to its end).
    /// </summary>
    public void Cancel()
    {
        if (!IsBusy || IsCancelled)
            return;
        IsCancelled = true;
        _step = "Cancelling";
        _detail = null;
        Log.Info($"[Editor] {_title}: cancelled.");
        if (_work is not null)
        {
            _work = null;
            AfterCancelled();
        }

        _scene?.Cancel();
        while (_queued.Count > 0)
            _queued.Dequeue();
    }

    /// <summary>Every frame, first in the workspace's process: runs the work when due, follows what holds the job, updates the window.</summary>
    internal void Tick()
    {
        if (!IsBusy)
            return;
        var now = Stopwatch.GetTimestamp();
        if (_lastFrame != 0)
        {
            var frame = Stopwatch.GetElapsedTime(_lastFrame, now);
            if (frame > _longestFrame)
                _longestFrame = frame;
        }

        _lastFrame = now;

        if (_work is not null)
        {
            if (--_workDelay > 0)
            {
                ShowWindow();
                return;
            }

            var work = _work;
            _work = null;
            try
            {
                work();
            }
            catch (Exception e) when (EditorCommands.IsRecoverable(e))
            {
                End(failed: true);
                _workspace.Commands.ReportError("Loading failed", e);
                return;
            }
        }

        if (_scene is { } load)
            FollowScene(load);
        else if (_holds > 0 && _workspace.Project.IsBuilding)
            SetStep("Building game code", 0.1f, $"dotnet build · {(int)_watch.Elapsed.TotalSeconds} s");

        if (_work is null && _scene is null && _holds == 0)
        {
            End(failed: false);
            return;
        }

        ShowWindow();
    }

    private void Begin(string title, Action work)
    {
        _title = title;
        _work = work;
        _workDelay = 2; // the window must reach the screen before the work starts
        _holds = 0;
        _step = title;
        _detail = null;
        _progress = 0f;
        _longestFrame = TimeSpan.Zero;
        _lastFrame = 0;
        IsCancelled = false;
        _watch.Restart();
        var splash = _workspace.Splash;
        if (!splash.Visible)
            splash.Show(title, 0f, panel: true); // the editor is up: a panel over it (at start-up the splash is already showing)
        else if (splash.IsFinishing)
            splash.Show(title, 0f, panel: splash.IsPanel); // the last job's window was fading out: it stays
        ShowWindow();
    }

    private void FollowScene(SceneLoad load)
    {
        _progress = Math.Max(_progress, _sceneFrom + (_sceneTo - _sceneFrom) * load.Progress);
        if (!IsCancelled)
        {
            _step = load.StageText;
            _detail = load.StageDetail ?? _sceneName;
        }

        if (!load.IsCompleted)
            return;
        _scene = null;
        var ended = _sceneEnded;
        _sceneEnded = null;
        if (load.Stage == SceneLoadStage.Canceled && load.Scene is { } entered &&
            _workspace.Session.Scenes.FirstOrDefault(s => ReferenceEquals(s.Root, entered)) is { } tab)
            _workspace.Session.Close(tab); // cancelled while warming up: the tab goes
        if (ended is not null)
        {
            ended(load);
        }
        else if (load.Stage == SceneLoadStage.Failed && load.Error is { } error)
        {
            _workspace.Commands.ReportError($"Could not open {_sceneName}", error);
        }

        if (load.Stage == SceneLoadStage.Canceled)
            AfterCancelled();
    }

    // Nothing left open after a cancel: the Project Manager, as when an open fails.
    private void AfterCancelled()
    {
        if (_workspace.Session.Tabs.Count == 0 && _workspace.Session.ProjectRoot is null)
            _workspace.ProjectManager.Open();
        else if (_workspace.Session.Scenes.Count == 0 && _workspace.Mode == EditorMode.Game)
            _workspace.Session.NewScene();
    }

    private void End(bool failed)
    {
        var title = _title!;
        var duration = _watch.Elapsed;
        _watch.Stop();
        LastJob = (title, duration, _longestFrame);
        Log.Info($"[Editor] {title}: {(failed ? "failed" : IsCancelled ? "cancelled" : "done")} in {duration.TotalSeconds:0.0} s " +
                 $"(longest frame {_longestFrame.TotalMilliseconds:0} ms).");
        _title = null;
        _work = null;
        _scene = null;
        _sceneEnded = null;
        _holds = 0;
        _workspace.Splash.Finish(failed ? "Failed" : IsCancelled ? "Cancelled" : "Ready");
        if (_queued.Count > 0)
        {
            var (next, work) = _queued.Dequeue();
            Begin(next, work);
        }
    }

    private void ShowWindow()
    {
        var splash = _workspace.Splash;
        if (!splash.Visible)
            return;
        splash.SetLoading(_title ?? "", _step, _detail, _progress, _watch.Elapsed.TotalSeconds, canCancel: !IsCancelled);
    }
}

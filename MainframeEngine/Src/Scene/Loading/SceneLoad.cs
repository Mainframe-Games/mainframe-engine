using System.Diagnostics;

namespace MainframeEngine;

/// <summary>The stages of a <see cref="SceneLoad"/>, in order (the last three are final).</summary>
public enum SceneLoadStage
{
    /// <summary>Worker: reading the scene file (<see cref="ResourceLoader"/>, with its external resources).</summary>
    Loading,

    /// <summary>Worker: instantiating the <see cref="PackedScene"/> outside the tree.</summary>
    Instantiating,

    /// <summary>Worker: the scene's <see cref="ISceneLoadable"/> nodes run <see cref="ISceneLoadable.LoadInBackground"/>.</summary>
    Preparing,

    /// <summary>Worker: the images of the textures created so far finish decoding (<see cref="ImagePrefetch"/>).</summary>
    Decoding,

    /// <summary>Main thread: the scene replaces the current one (its nodes' <see cref="Node.OnReady"/> run).</summary>
    Entering,

    /// <summary>
    /// Main thread: the scene runs and renders behind the loading screen — its first frames create the GPU resources and
    /// pipelines — until <see cref="SceneLoadOptions.WarmUpFrames"/> have passed and every loadable reports loaded
    /// (<see cref="ISceneLoadable.PollLoaded"/>).
    /// </summary>
    WarmingUp,

    /// <summary>The scene is current and loaded.</summary>
    Done,

    /// <summary>The load threw (<see cref="SceneLoad.Error"/>); the current scene was left as it was, or is the half-entered new one when <see cref="Node.OnReady"/> threw.</summary>
    Failed,

    /// <summary>Cancelled (<see cref="SceneLoad.Cancel"/>, another scene change, or the tree shut down) before it completed.</summary>
    Canceled,
}

/// <summary>Options of <see cref="SceneTree.ChangeSceneToFileAsync"/>.</summary>
public sealed record SceneLoadOptions
{
    /// <summary>Frames the new scene renders behind the loading screen before the load completes (default 2: its first frames create its GPU resources and pipelines).</summary>
    public int WarmUpFrames { get; init; } = 2;

    /// <summary>Decode the images of textures created during the load on worker threads (<see cref="ImagePrefetch"/>; default on).</summary>
    public bool PrefetchImages { get; init; } = true;

    /// <summary>
    /// Keep the load at least this long (seconds) before the scene enters the tree (QA of a loading screen, <c>--loading-hold</c>);
    /// default 0.
    /// </summary>
    public float HoldSeconds { get; init; }
}

/// <summary>
/// An asynchronous scene change (<see cref="SceneTree.ChangeSceneToFileAsync"/>, ADR 0183), Godot's
/// <c>load_threaded_request</c> + <c>change_scene_to_packed</c> in one: a worker thread reads the scene, instantiates it
/// outside the tree, lets its <see cref="ISceneLoadable"/> nodes do their heavy work (<see cref="ISceneLoadable.LoadInBackground"/>)
/// and decodes the images of the textures it created; then, on the main thread, the scene replaces the current one and runs
/// a few frames behind the loading screen until its loadables report loaded. Meanwhile the main loop keeps running:
/// window events, audio and the loading screen (<see cref="LoadingScreen"/>) never stall on the load.
/// </summary>
/// <remarks>
/// The tree advances the load at the start of every <see cref="SceneTree.Tick"/>. <see cref="Progress"/> combines the
/// stages with fixed weights (the loadables' share by <see cref="ISceneLoadable.LoadWeight"/>) and never goes back.
/// Nothing here allocates per frame once the load is done; while it runs it may.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "The cancellation source has no timer; nothing to dispose.")]
public sealed class SceneLoad
{
    // Stage weights of the progress bar (sum 1): the loadables' background work dominates heavy scenes.
    private const float LoadingWeight = 0.06f, InstantiatingWeight = 0.04f, PreparingWeight = 0.55f, DecodingWeight = 0.12f,
        EnteringWeight = 0.05f, WarmingUpWeight = 0.18f;

    private readonly SceneTree _tree;
    private readonly CancellationTokenSource _cancel = new();
    private readonly Stopwatch _watch = new();
    private readonly List<(ISceneLoadable Node, SceneLoadProgress Progress)> _background = [];
    private readonly List<(ISceneLoadable Node, SceneLoadProgress Progress)> _warming = [];
    private Task? _worker;
    private volatile int _stage;
    private PackedScene? _packed;
    private Node? _root;
    private volatile ImagePrefetch? _prefetch;
    private int _warmFrames;
    private float _progress;
    private string _stageText = "Loading";
    private bool _completedRaised;

    internal SceneLoad(SceneTree tree, string pathOrUid, SceneLoadOptions options)
    {
        _tree = tree;
        Path = pathOrUid;
        Options = options;
    }

    /// <summary>The scene's path or UID.</summary>
    public string Path { get; }

    public SceneLoadOptions Options { get; }

    public SceneLoadStage Stage => (SceneLoadStage)_stage;

    /// <summary>True once the load is done, failed or was cancelled.</summary>
    public bool IsCompleted => Stage >= SceneLoadStage.Done;

    /// <summary>True when the scene is current and loaded.</summary>
    public bool IsDone => Stage == SceneLoadStage.Done;

    /// <summary>Why the load failed (null otherwise).</summary>
    public Exception? Error { get; private set; }

    /// <summary>The new scene's root once it entered the tree (<see cref="SceneLoadStage.Entering"/> on), else null.</summary>
    public Node? Scene { get; private set; }

    /// <summary>Overall progress, 0–1, monotonic (updated by the main thread each frame).</summary>
    public float Progress => Stage == SceneLoadStage.Done ? 1f : _progress;

    /// <summary>
    /// What the load is doing, for the loading screen: the newest stage label of the loadable at work ("Growing trees"),
    /// else the stage's own ("Loading", "Decoding textures", "Preparing shaders"). Updated by the main thread each frame.
    /// </summary>
    public string StageText => _stageText;

    /// <summary>Time since the load started.</summary>
    public TimeSpan Elapsed => _watch.Elapsed;

    /// <summary>Raised on the main thread when the load completes (done, failed or cancelled).</summary>
    public event Action<SceneLoad>? Completed;

    /// <summary>
    /// Cancels the load: the worker stops at its next check (<see cref="SceneLoadProgress.CancellationToken"/>), the
    /// instantiated scene is freed and the current scene stays. A load that already entered the tree only stops waiting
    /// (the scene stays current). No effect once completed.
    /// </summary>
    public void Cancel()
    {
        if (IsCompleted)
            return;
        _cancel.Cancel();
        if (Stage >= SceneLoadStage.Entering)
            Complete(SceneLoadStage.Canceled, null);
    }

    internal void Start()
    {
        _watch.Start();
        _prefetch = Options.PrefetchImages ? ImagePrefetch.Begin() : null;
        _worker = Task.Run(RunWorker, _cancel.Token);
    }

    // ── Worker ───────────────────────────────────────────────────────────────────────────────────────────────

    private void RunWorker()
    {
        var token = _cancel.Token;
        _prefetch?.CollectOnCurrentFlow(); // the textures this flow (and the tasks it starts) creates decode ahead
        SetStage(SceneLoadStage.Loading);
        _packed = ResourceLoader.Load<PackedScene>(Path);
        token.ThrowIfCancellationRequested();

        SetStage(SceneLoadStage.Instantiating);
        _root = _packed.Instantiate();
        token.ThrowIfCancellationRequested();

        SetStage(SceneLoadStage.Preparing);
        var loadables = new List<ISceneLoadable>();
        CollectLoadables(_root, loadables);
        lock (_background)
            foreach (var loadable in loadables)
                _background.Add((loadable, new SceneLoadProgress(token, Weight(loadable))));
        for (var i = 0; i < loadables.Count; i++)
        {
            SceneLoadProgress progress;
            lock (_background)
                progress = _background[i].Progress;
            loadables[i].LoadInBackground(progress);
            progress.Report(1f);
            token.ThrowIfCancellationRequested();
        }

        SetStage(SceneLoadStage.Decoding);
        _prefetch?.WhenAll().Wait(token);
        token.ThrowIfCancellationRequested();
    }

    private void SetStage(SceneLoadStage stage) => _stage = (int)stage;

    private static float Weight(ISceneLoadable loadable) => float.IsFinite(loadable.LoadWeight) ? Math.Max(0f, loadable.LoadWeight) : 1f;

    /// <summary>The loadable nodes under <paramref name="root"/> (itself included), depth-first in tree order.</summary>
    internal static void CollectLoadables(Node root, List<ISceneLoadable> into)
    {
        if (root is ISceneLoadable loadable)
            into.Add(loadable);
        var children = root.Children;
        for (var i = 0; i < children.Count; i++)
            CollectLoadables(children[i], into);
    }

    // ── Main thread ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Advances the load; the tree calls it at the start of every tick (main thread).</summary>
    internal void Advance()
    {
        if (IsCompleted)
        {
            ReleaseCancelled();
            return;
        }

        try
        {
            AdvanceCore();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.Error($"[Scene] Loading '{Path}' failed: {e.Message}");
            Complete(SceneLoadStage.Failed, e);
        }

        UpdateProgress();
    }

    private void AdvanceCore()
    {
        if (Stage < SceneLoadStage.Entering)
        {
            if (_worker is not { IsCompleted: true } worker)
                return;
            if (worker.IsCanceled || _cancel.IsCancellationRequested || worker.Exception?.InnerException is OperationCanceledException)
            {
                Complete(SceneLoadStage.Canceled, null);
                return;
            }

            if (worker.Exception is { } failure)
            {
                var error = failure.InnerException ?? failure;
                Log.Error($"[Scene] Loading '{Path}' failed: {error.Message}");
                Complete(SceneLoadStage.Failed, error);
                return;
            }

            if (_watch.Elapsed.TotalSeconds < Options.HoldSeconds)
                return;
            Enter();
            return;
        }

        // Warming up: count the frames rendered since the scene entered and poll its loadables.
        _warmFrames++;
        var loaded = true;
        foreach (var (node, progress) in _warming)
        {
            if (progress.Fraction >= 1f)
                continue;
            if (node.PollLoaded(progress))
                progress.Report(1f);
            else
                loaded = false;
        }

        if (loaded && _warmFrames >= Options.WarmUpFrames)
            Complete(SceneLoadStage.Done, null);
    }

    private void Enter()
    {
        SetStage(SceneLoadStage.Entering);
        var root = _root!;
        var packed = _packed!;
        _root = null;
        _packed = null;
        Scene = root;
        _tree.EnterLoadedScene(root, packed);

        // The loadables in the tree now (OnReady may have added some), polled until they report loaded.
        var loadables = new List<ISceneLoadable>();
        CollectLoadables(root, loadables);
        foreach (var loadable in loadables)
            _warming.Add((loadable, new SceneLoadProgress(_cancel.Token, Weight(loadable))));
        SetStage(SceneLoadStage.WarmingUp);
        Log.Info($"[Scene] '{Path}' entered the tree after {_watch.Elapsed.TotalSeconds:0.00} s; warming up.");
    }

    private void Complete(SceneLoadStage stage, Exception? error)
    {
        if (IsCompleted)
            return;
        Error = error;
        SetStage(stage);
        _watch.Stop();
        _prefetch?.Dispose(); // decoded images not used by now decode on demand again
        _prefetch = null;
        if (stage == SceneLoadStage.Done)
        {
            _progress = 1f;
            Log.Info($"[Scene] '{Path}' loaded in {_watch.Elapsed.TotalSeconds:0.00} s.");
        }

        ReleaseCancelled();
        if (_completedRaised)
            return;
        _completedRaised = true;
        Completed?.Invoke(this);
    }

    // A failed or cancelled load frees what its worker built, once the worker has stopped.
    private void ReleaseCancelled()
    {
        if (Stage == SceneLoadStage.Done || _worker is { IsCompleted: false })
            return;
        if (_root is { } root)
        {
            _root = null;
            root.Free();
        }

        if (_packed is { } packed)
        {
            _packed = null;
            packed.Release();
        }
    }

    /// <summary>True when the worker has stopped (or never started): nothing is left to release.</summary>
    internal bool IsWorkerIdle => _worker is null or { IsCompleted: true };

    /// <summary>Waits up to <paramref name="timeout"/> for the worker to stop (tree shutdown).</summary>
    internal void WaitForWorker(TimeSpan timeout)
    {
        try
        {
            _worker?.Wait(timeout);
        }
        catch (AggregateException)
        {
            // reported by Advance; at shutdown nobody listens
        }

        ReleaseCancelled();
    }

    private void UpdateProgress()
    {
        var stage = Stage;
        float value;
        string text;
        switch (stage)
        {
            case SceneLoadStage.Loading:
                value = 0f;
                text = "Loading";
                break;
            case SceneLoadStage.Instantiating:
                value = LoadingWeight;
                text = "Building the scene";
                break;
            case SceneLoadStage.Preparing:
            case SceneLoadStage.Decoding:
                {
                    float fraction;
                    string? label;
                    lock (_background)
                        (fraction, label) = Combine(_background);
                    value = LoadingWeight + InstantiatingWeight + PreparingWeight * (stage == SceneLoadStage.Decoding ? 1f : fraction);
                    if (stage == SceneLoadStage.Decoding)
                    {
                        value += DecodingWeight * (_prefetch?.Fraction ?? 1f);
                        text = _prefetch is { Count: > 0 } prefetch ? $"Decoding textures ({prefetch.Completed}/{prefetch.Count})" : "Decoding textures";
                    }
                    else
                    {
                        text = label ?? "Preparing the world";
                    }

                    break;
                }
            case SceneLoadStage.Entering:
                value = 1f - EnteringWeight - WarmingUpWeight;
                text = "Entering the world";
                break;
            case SceneLoadStage.WarmingUp:
                {
                    var (fraction, label) = Combine(_warming);
                    var frames = Options.WarmUpFrames <= 0 ? 1f : Math.Min(1f, _warmFrames / (float)Options.WarmUpFrames);
                    var weights = 1f;
                    foreach (var (_, progress) in _warming)
                        weights += progress.Weight;
                    var warm = (frames + fraction * (weights - 1f)) / weights;
                    value = 1f - WarmingUpWeight + WarmingUpWeight * warm;
                    text = label ?? "Preparing shaders";
                    break;
                }
            case SceneLoadStage.Done:
                value = 1f;
                text = "Ready";
                break;
            default:
                value = _progress;
                text = stage == SceneLoadStage.Failed ? "Failed" : "Cancelled";
                break;
        }

        _progress = Math.Max(_progress, Math.Clamp(value, 0f, 1f));
        _stageText = text;
    }

    // The weighted fraction of a list of reporters (1 when empty) and the newest stage label among those still working.
    private static (float Fraction, string? Label) Combine(List<(ISceneLoadable Node, SceneLoadProgress Progress)> list)
    {
        if (list.Count == 0)
            return (1f, null);
        float total = 0f, done = 0f;
        string? label = null;
        long newest = long.MinValue;
        foreach (var (_, progress) in list)
        {
            var fraction = progress.Fraction;
            total += progress.Weight;
            done += progress.Weight * fraction;
            if (fraction < 1f && progress.Stage is { } stage && progress.ReportedAt >= newest)
            {
                newest = progress.ReportedAt;
                label = stage;
            }
        }

        return (total <= 0f ? 1f : done / total, label);
    }
}

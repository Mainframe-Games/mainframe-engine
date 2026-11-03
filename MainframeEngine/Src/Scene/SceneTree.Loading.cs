namespace MainframeEngine;

// ADR 0183: asynchronous scene changes and the loading state the hosts and the loading screen read.
public sealed partial class SceneTree
{
    private SceneLoad? _sceneLoad;
    private readonly List<SceneLoad> _retiredLoads = [];
    private readonly List<SceneLoad> _instanceLoads = []; // InstantiateSceneAsync: until completed and their worker stopped
    private int _loadingScreens;

    /// <summary>
    /// Loads a scene by path or UID on a worker thread and makes it current when it is ready (Godot's
    /// <c>ResourceLoader.load_threaded_request</c> then <c>change_scene_to_packed</c>): see <see cref="SceneLoad"/>.
    /// The current scene keeps running until the new one enters the tree. Show progress with a <see cref="LoadingScreen"/>
    /// (<see cref="GameHost"/> does for the start scene). A newer scene change — this or the synchronous ones — cancels a
    /// load still in progress. The tree holds the loader reference, as <see cref="ChangeSceneToFile"/> does.
    /// </summary>
    public SceneLoad ChangeSceneToFileAsync(string pathOrUid, SceneLoadOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(pathOrUid);
        ObjectDisposedException.ThrowIf(_shutDown, this);
        CancelSceneLoad();
        var load = new SceneLoad(this, pathOrUid, options ?? new SceneLoadOptions());
        _sceneLoad = load;
        load.Start();
        return load;
    }

    /// <summary>
    /// Loads a scene by path or UID on a worker thread like <see cref="ChangeSceneToFileAsync"/> — load, instantiate,
    /// <see cref="ISceneLoadable.LoadInBackground"/>, image decoding, then the warm-up frames — but the scene does not
    /// replace the current one: when it is ready, <paramref name="enter"/> places its root (main thread, at the start of a
    /// tick) and takes ownership of the <see cref="PackedScene"/> (release it when the root is freed). The editor opens
    /// scene tabs this way. These loads never cancel, and are never cancelled by, scene changes or each other; they do not
    /// count for <see cref="IsLoading"/>. When <paramref name="enter"/> throws, the load fails with the root half placed.
    /// </summary>
    public SceneLoad InstantiateSceneAsync(string pathOrUid, Action<Node, PackedScene> enter, SceneLoadOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(pathOrUid);
        ArgumentNullException.ThrowIfNull(enter);
        ObjectDisposedException.ThrowIf(_shutDown, this);
        var load = new SceneLoad(this, pathOrUid, options ?? new SceneLoadOptions(), enter);
        _instanceLoads.Add(load);
        load.Start();
        return load;
    }

    /// <summary>The asynchronous scene change in progress (<see cref="ChangeSceneToFileAsync"/>), or null.</summary>
    public SceneLoad? CurrentLoad => _sceneLoad;

    /// <summary>
    /// True while a scene is loading (<see cref="CurrentLoad"/>) or a <see cref="LoadingScreen"/> is still shown (fading
    /// out). Hosts count frames (<c>--max-frames</c>, <c>--screenshot</c>) only when it is false, so captures and timings
    /// start once the game is on screen.
    /// </summary>
    public bool IsLoading => _sceneLoad is not null || _loadingScreens > 0;

    internal void AddLoadingScreen() => _loadingScreens++;

    internal void RemoveLoadingScreen() => _loadingScreens = Math.Max(0, _loadingScreens - 1);

    // A synchronous scene change (or an unload) outranks a load still in progress.
    private void CancelSceneLoad()
    {
        if (_sceneLoad is not { } load)
            return;
        _sceneLoad = null;
        load.Cancel();
        if (!load.IsCompleted || !load.IsWorkerIdle)
            _retiredLoads.Add(load); // advanced until it completes and its worker stops, then it frees what it built
    }

    /// <summary>Called by a <see cref="SceneLoad"/> when its scene is ready: it replaces the current scene now (main thread, outside a tick).</summary>
    internal void EnterLoadedScene(Node root, PackedScene packed)
    {
        ChangeSceneNow(root);
        _currentSceneResources.Add((root, packed));
    }

    // At the start of every tick, before anything processes: the scene enters as ChangeSceneToFile would have entered it
    // from the game loop, so its first tick is a whole one.
    private void AdvanceSceneLoads()
    {
        if (_sceneLoad is { } load)
        {
            load.Advance();
            if (load.IsCompleted && ReferenceEquals(_sceneLoad, load))
                _sceneLoad = null;
        }

        AdvanceUntilIdle(_retiredLoads);
        AdvanceUntilIdle(_instanceLoads);
    }

    // Advances each load; one that completed and whose worker stopped is advanced once more (a cancelled or failed load
    // frees what its worker built) and dropped. Index loop: an enter callback may start another load.
    private static void AdvanceUntilIdle(List<SceneLoad> loads)
    {
        for (var i = loads.Count - 1; i >= 0; i--)
        {
            if (i >= loads.Count)
                continue;
            var load = loads[i];
            load.Advance();
            if (load.IsCompleted && load.IsWorkerIdle)
            {
                load.Advance(); // frees what the worker built
                loads.Remove(load);
            }
        }
    }

    private void ShutdownSceneLoads()
    {
        CancelSceneLoad();
        foreach (var load in _instanceLoads)
            load.Cancel();
        foreach (var retired in _retiredLoads)
            retired.WaitForWorker(TimeSpan.FromSeconds(5));
        foreach (var load in _instanceLoads)
            load.WaitForWorker(TimeSpan.FromSeconds(5));
        _retiredLoads.Clear();
        _instanceLoads.Clear();
    }
}

namespace MainframeEngine;

// ADR 0183: asynchronous scene changes and the loading state the hosts and the loading screen read.
public sealed partial class SceneTree
{
    private SceneLoad? _sceneLoad;
    private readonly List<SceneLoad> _retiredLoads = [];
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

        for (var i = _retiredLoads.Count - 1; i >= 0; i--)
        {
            var retired = _retiredLoads[i];
            retired.Advance();
            if (retired.IsCompleted && retired.IsWorkerIdle)
            {
                retired.Advance(); // frees what the worker built
                _retiredLoads.RemoveAt(i);
            }
        }
    }

    private void ShutdownSceneLoads()
    {
        CancelSceneLoad();
        foreach (var retired in _retiredLoads)
            retired.WaitForWorker(TimeSpan.FromSeconds(5));
        _retiredLoads.Clear();
    }
}

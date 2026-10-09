using System.Diagnostics;
using System.Numerics;

namespace MainframeEngine.Tests.Scene;

/// <summary>
/// A node with a heavy load (ADR 0183): builds <see cref="Pieces"/> children in <see cref="ISceneLoadable.LoadInBackground"/>
/// (waiting on <see cref="Gate"/> first, when set) or, loaded synchronously, in ready; stays loading for
/// <see cref="WarmFrames"/> polls.
/// </summary>
internal sealed class LoadableTestNode : Node3D, ISceneLoadable
{
    /// <summary>Holds every background load until set (null: no wait).</summary>
    public static ManualResetEventSlim? Gate { get; set; }

    [Export] public int Pieces { get; set; } = 3;
    [Export] public bool Fail { get; set; }
    [Export] public int WarmFrames { get; set; } = 3;

    private bool _built;

    public int? BackgroundThread { get; private set; }
    public bool BuiltInReady { get; private set; }
    public int Polls { get; private set; }

    float ISceneLoadable.LoadWeight => 2f;

    void ISceneLoadable.LoadInBackground(SceneLoadProgress progress)
    {
        BackgroundThread = Environment.CurrentManagedThreadId;
        progress.Report(0.25f, "Building pieces");
        Gate?.Wait(progress.CancellationToken);
        if (Fail)
            throw new InvalidDataException("A broken piece.");
        Build();
        progress.Report(0.9f, "Polishing");
    }

    bool ISceneLoadable.PollLoaded(SceneLoadProgress progress)
    {
        Polls++;
        progress.Report(Polls / (float)WarmFrames, "Warming up pieces");
        return Polls >= WarmFrames;
    }

    protected override void OnReady()
    {
        if (_built)
            return;
        Build();
        BuiltInReady = true;
    }

    private void Build()
    {
        _built = true;
        for (var i = 0; i < Pieces; i++)
            AddChild(new MeshInstance3D { Name = $"Piece{i}", Position = new Vector3(i, 0f, 0f), Mesh = new BoxMesh() });
    }
}

/// <summary>Asynchronous scene changes (ADR 0183): the loader's state machine, progress, errors, cancellation, equivalence.</summary>
[Collection(nameof(SerialResources))]
public sealed class SceneLoadTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private readonly string _project = Path.Combine(Path.GetTempPath(), "mf-scene-load", Guid.NewGuid().ToString("N"));
    private readonly AssetDatabase _previousAssets = AssetDatabase.Current;
    private readonly SceneTree _tree = new();

    public SceneLoadTests()
    {
        Directory.CreateDirectory(Path.Combine(_project, AssetDatabase.ContentFolder));
        AssetDatabase.Current = new AssetDatabase(_project);
        ResourceLoader.ClearCache();
        LoadableTestNode.Gate = null;
    }

    public void Dispose()
    {
        LoadableTestNode.Gate?.Set();
        LoadableTestNode.Gate = null;
        _tree.Shutdown();
        ResourceLoader.ClearCache();
        AssetDatabase.Current = _previousAssets;
        try
        {
            Directory.Delete(_project, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string ContentPath(string relative) => Path.Combine(_project, AssetDatabase.ContentFolder, relative);

    private string SaveLevel(string name, Action<Node3D>? build = null)
    {
        var level = new Node3D { Name = name };
        var loadable = new LoadableTestNode { Name = "Loadable" };
        level.AddChild(loadable);
        loadable.Owner = level;
        build?.Invoke(level);
        var uid = SceneSaver.Save(level, ContentPath($"{name}.mscene"));
        level.Free();
        ResourceLoader.ClearCache();
        return uid;
    }

    private void Tick(int frames = 1)
    {
        for (var i = 0; i < frames; i++)
            _tree.Tick(new GameTime { DeltaTime = 1f / 60f });
    }

    // Ticks the tree like a game loop until the load completes, recording what the loading screen would show.
    private List<(SceneLoadStage Stage, float Progress)> RunToCompletion(SceneLoad load)
    {
        var seen = new List<(SceneLoadStage, float)>();
        var watch = Stopwatch.StartNew();
        while (!load.IsCompleted)
        {
            Assert.True(watch.Elapsed < Timeout, $"The load is stuck in {load.Stage}.");
            Tick();
            seen.Add((load.Stage, load.Progress));
            Thread.Sleep(1);
        }

        return seen;
    }

    [Fact]
    public void TheSceneLoadsInTheBackgroundEntersAndWarmsUp()
    {
        var uid = SaveLevel("Level");
        var completed = 0;
        var load = _tree.ChangeSceneToFileAsync(uid);
        load.Completed += _ => completed++;
        Assert.Same(load, _tree.CurrentLoad);
        Assert.True(_tree.IsLoading);

        var seen = RunToCompletion(load);

        Assert.Equal(SceneLoadStage.Done, load.Stage);
        Assert.True(load.IsDone);
        Assert.Null(load.Error);
        Assert.Equal(1, completed);
        Assert.Null(_tree.CurrentLoad);
        Assert.False(_tree.IsLoading);
        Assert.Same(load.Scene, _tree.CurrentScene);
        Assert.Equal("Level", _tree.CurrentScene!.Name);

        // The loadable built on a worker thread, outside the tree, and was polled until it reported loaded.
        var loadable = _tree.CurrentScene.GetNode<LoadableTestNode>("Loadable");
        Assert.NotNull(loadable.BackgroundThread);
        Assert.NotEqual(Environment.CurrentManagedThreadId, loadable.BackgroundThread);
        Assert.False(loadable.BuiltInReady);
        Assert.Equal(3, loadable.ChildCount);
        Assert.Equal(loadable.WarmFrames, loadable.Polls);

        // Stages only move forward; progress never goes back and ends at 1.
        for (var i = 1; i < seen.Count; i++)
        {
            Assert.True(seen[i].Stage >= seen[i - 1].Stage, $"Stage went back: {seen[i - 1].Stage} → {seen[i].Stage}.");
            Assert.True(seen[i].Progress >= seen[i - 1].Progress, $"Progress went back: {seen[i - 1].Progress} → {seen[i].Progress}.");
        }

        Assert.Contains(seen, s => s.Stage == SceneLoadStage.WarmingUp);
        Assert.Equal(1f, load.Progress);
        Assert.Equal("Ready", load.StageText);

        // The tree holds the loader reference, as ChangeSceneToFile does.
        Assert.True(ResourceLoader.IsCached(uid));
        _tree.UnloadCurrentScene();
        Assert.False(ResourceLoader.IsCached(uid));
    }

    [Fact]
    public void TheMainLoopKeepsTickingWhileALoadableWorks()
    {
        var uid = SaveLevel("Slow");
        var previous = new Node3D { Name = "Previous" };
        _tree.ChangeScene(previous);
        using var gate = new ManualResetEventSlim(false);
        LoadableTestNode.Gate = gate;
        var load = _tree.ChangeSceneToFileAsync(uid);

        // The worker blocks inside LoadInBackground; the game loop runs on, the old scene stays current.
        var watch = Stopwatch.StartNew();
        var start = _tree.ProcessFrames;
        while (load.Stage != SceneLoadStage.Preparing || !string.Equals(load.StageText, "Building pieces", StringComparison.Ordinal))
        {
            Assert.True(watch.Elapsed < Timeout, $"The load never reached the loadable ({load.Stage}).");
            Tick();
            Thread.Sleep(1);
        }

        Tick(60);
        Assert.True(_tree.ProcessFrames - start >= 60);
        Assert.Equal(SceneLoadStage.Preparing, load.Stage);
        Assert.Same(previous, _tree.CurrentScene);
        Assert.True(_tree.IsLoading);
        // Its report shows: the background stage is 55 % of the bar, after 10 % for loading and instantiating.
        Assert.True(Math.Abs(load.Progress - (0.1f + 0.55f * 0.25f)) < 1e-3f, $"Progress {load.Progress}, stage {load.Stage} \"{load.StageText}\".");

        gate.Set();
        RunToCompletion(load);
        Assert.Equal(SceneLoadStage.Done, load.Stage);
        Assert.Equal("Slow", _tree.CurrentScene!.Name);
        Assert.True(previous.IsFreed);
    }

    [Fact]
    public void AsyncInstantiationBuildsTheSameTreeAsSync()
    {
        var sub = new Node3D { Name = "Sub" };
        sub.AddChild(new AllTypesNode { Name = "Inner", Int = 11, Text = "inner" });
        sub.GetChild(0).Owner = sub;
        SceneSaver.Save(sub, ContentPath("Sub.mscene"));
        sub.Free();
        var uid = SaveLevel("Rich", level =>
        {
            var all = new AllTypesNode
            {
                Name = "All",
                Int = 42,
                Text = "hello",
                Tint = System.Drawing.Color.Coral,
                Weights = [1f, 2f],
                Tags = ["a", "b"],
                Data = new TestResource { Value = 5, Label = "five", Next = new TestResource { Value = 6 } },
            };
            level.AddChild(all);
            all.Owner = level;
            var instance = ResourceLoader.Load<PackedScene>("Content/Sub.mscene").Instantiate();
            level.AddChild(instance);
            instance.Owner = level;
        });

        _tree.ChangeSceneToFile(uid);
        var sync = Dump(_tree.CurrentScene!);
        var syncJson = SceneSaver.ToJson(_tree.CurrentScene!);
        _tree.UnloadCurrentScene();

        var load = _tree.ChangeSceneToFileAsync(uid);
        RunToCompletion(load);
        Assert.Equal(SceneLoadStage.Done, load.Stage);
        var scene = _tree.CurrentScene!;

        // The same saved content, and the same live tree (the loadable's generated pieces are unowned: in both trees,
        // built in ready by the sync load and on the worker by the async one).
        Assert.Equal(syncJson, SceneSaver.ToJson(scene));
        Assert.Equal(sync, Dump(scene));
    }

    private static string Dump(Node root)
    {
        var lines = new List<string>();
        Walk(root, 0);
        return string.Join('\n', lines);

        void Walk(Node node, int depth)
        {
            var extra = node switch
            {
                AllTypesNode a => $" int={a.Int} text={a.Text} data={a.Data?.Value}/{a.Data?.Next?.Value}",
                Node3D n => $" at {n.Position}",
                _ => "",
            };
            lines.Add($"{new string(' ', depth * 2)}{node.Name} ({node.GetType().Name}){extra} owner={node.Owner?.Name}");
            foreach (var child in node.Children)
                Walk(child, depth + 1);
        }
    }

    [Fact]
    public void ALoadableThatThrowsFailsTheLoadAndKeepsTheCurrentScene()
    {
        var uid = SaveLevel("Broken", level => level.GetNode<LoadableTestNode>("Loadable").Fail = true);
        var previous = new Node3D { Name = "Previous" };
        _tree.ChangeScene(previous);

        var load = _tree.ChangeSceneToFileAsync(uid);
        RunToCompletion(load);

        Assert.Equal(SceneLoadStage.Failed, load.Stage);
        Assert.IsType<InvalidDataException>(load.Error);
        Assert.Contains("broken piece", load.Error!.Message, StringComparison.Ordinal);
        Assert.Same(previous, _tree.CurrentScene);
        Assert.Null(load.Scene);
        Assert.False(_tree.IsLoading);
        Assert.False(ResourceLoader.IsCached(uid)); // the worker's reference was released with its nodes
    }

    [Fact]
    public void AMissingSceneFails()
    {
        var load = _tree.ChangeSceneToFileAsync("Content/Nowhere.mscene");
        RunToCompletion(load);
        Assert.Equal(SceneLoadStage.Failed, load.Stage);
        Assert.IsType<FileNotFoundException>(load.Error);
        Assert.Null(_tree.CurrentScene);
    }

    [Fact]
    public void CancellingStopsTheLoadAndFreesWhatItBuilt()
    {
        var uid = SaveLevel("Cancelled");
        var previous = new Node3D { Name = "Previous" };
        _tree.ChangeScene(previous);
        using var gate = new ManualResetEventSlim(false);
        LoadableTestNode.Gate = gate;
        var load = _tree.ChangeSceneToFileAsync(uid);
        var watch = Stopwatch.StartNew();
        while (load.Stage != SceneLoadStage.Preparing)
        {
            Assert.True(watch.Elapsed < Timeout);
            Tick();
            Thread.Sleep(1);
        }

        load.Cancel(); // the gate's wait observes the token
        RunToCompletion(load);

        Assert.Equal(SceneLoadStage.Canceled, load.Stage);
        Assert.Null(load.Error);
        Assert.Same(previous, _tree.CurrentScene);
        Assert.False(_tree.IsLoading);
        Assert.False(ResourceLoader.IsCached(uid));
    }

    [Fact]
    public void ANewerSceneChangeCancelsALoadInProgress()
    {
        var uid = SaveLevel("Overtaken");
        using var gate = new ManualResetEventSlim(false);
        LoadableTestNode.Gate = gate;
        var load = _tree.ChangeSceneToFileAsync(uid);

        var replacement = new Node3D { Name = "Replacement" };
        _tree.ChangeScene(replacement);
        Assert.Null(_tree.CurrentLoad);

        gate.Set();
        var watch = Stopwatch.StartNew();
        while (!load.IsCompleted)
        {
            Assert.True(watch.Elapsed < Timeout);
            Tick();
            Thread.Sleep(1);
        }

        Assert.Equal(SceneLoadStage.Canceled, load.Stage);
        Assert.Same(replacement, _tree.CurrentScene);

        // A second async change replaces the first the same way.
        LoadableTestNode.Gate = null;
        var first = _tree.ChangeSceneToFileAsync(uid);
        var second = _tree.ChangeSceneToFileAsync(uid);
        RunToCompletion(second);
        Assert.True(first.IsCompleted);
        Assert.Equal(SceneLoadStage.Canceled, first.Stage);
        Assert.Equal(SceneLoadStage.Done, second.Stage);
        Assert.Same(second.Scene, _tree.CurrentScene);
    }

    [Fact]
    public void SynchronousLoadsStillBuildInReady()
    {
        var uid = SaveLevel("Sync");
        _tree.ChangeSceneToFile(uid);
        var loadable = _tree.CurrentScene!.GetNode<LoadableTestNode>("Loadable");
        Assert.True(loadable.BuiltInReady);
        Assert.Null(loadable.BackgroundThread);
        Assert.Equal(0, loadable.Polls);
        Assert.False(_tree.IsLoading);
    }

    [Fact]
    public void HoldKeepsTheLoadBeforeEntering()
    {
        var uid = SaveLevel("Held");
        var load = _tree.ChangeSceneToFileAsync(uid, new SceneLoadOptions { HoldSeconds = 0.3f, WarmUpFrames = 0 });
        RunToCompletion(load);
        Assert.Equal(SceneLoadStage.Done, load.Stage);
        Assert.True(load.Elapsed >= TimeSpan.FromSeconds(0.3));
    }

    [Fact]
    public void ProgressReportsAreClampedMonotonicAndKeepTheNewestLabel()
    {
        var progress = SceneLoadProgress.None;
        progress.Report(0.5f, "Half");
        progress.Report(0.2f); // never back
        Assert.Equal(0.5f, progress.Fraction);
        progress.Report(7f);
        Assert.Equal(1f, progress.Fraction);
        progress.Report(float.NaN, "Label only");
        Assert.Equal(1f, progress.Fraction);
        Assert.Equal("Label only", progress.Stage);
        progress.Report("Another");
        Assert.Equal("Another", progress.Stage);
        progress.ThrowIfCancellationRequested();
    }

    [Fact]
    public void LoadThreadedLoadsOnAWorkerAndSharesTheCache()
    {
        var uid = ResourceSaver.Save(new TestResource { Value = 9 }, ContentPath("Data.mres"));
        ResourceLoader.ClearCache();

        using var task = ResourceLoader.LoadThreaded<TestResource>(uid);
        var watch = Stopwatch.StartNew();
        while (task.Status == ResourceLoadStatus.InProgress)
        {
            Assert.True(watch.Elapsed < Timeout);
            Thread.Sleep(1);
        }

        Assert.Equal(ResourceLoadStatus.Loaded, task.Status);
        Assert.Equal(1f, task.Progress);
        var loaded = task.Result;
        Assert.Equal(9, loaded.Value);
        var again = ResourceLoader.Load<TestResource>(uid);
        Assert.Same(loaded, again);
        again.Release();
        loaded.Release();
        Assert.False(ResourceLoader.IsCached(uid));

        using var missing = ResourceLoader.LoadThreaded<TestResource>("Content/Missing.mres");
        Assert.Throws<FileNotFoundException>(() => missing.Result);
        Assert.Equal(ResourceLoadStatus.Failed, missing.Status);
        Assert.IsType<FileNotFoundException>(missing.Error);
    }

    [Fact]
    public async Task ADisposedLoadTaskReleasesAResultNobodyTook()
    {
        var uid = ResourceSaver.Save(new TestResource { Value = 3 }, ContentPath("Unused.mres"));
        ResourceLoader.ClearCache();
        var task = ResourceLoader.LoadThreaded<TestResource>(uid);
        await task.AsTask().WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.True(ResourceLoader.IsCached(uid));
        task.Dispose();
        Assert.False(ResourceLoader.IsCached(uid));
    }
}

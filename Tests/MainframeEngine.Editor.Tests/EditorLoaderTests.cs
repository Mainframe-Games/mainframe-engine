using System.Diagnostics;
using System.Numerics;

namespace MainframeEngine.Editor.Tests;

/// <summary>A [Tool] loadable whose background work waits on <see cref="Gate"/> (a slow valley, in tests).</summary>
[Tool]
public sealed class GatedLoadableNode : Node3D, ISceneLoadable
{
    /// <summary>Holds every background load until set (null: no wait).</summary>
    public static ManualResetEventSlim? Gate { get; set; }

    public bool BuiltInBackground { get; private set; }

    void ISceneLoadable.LoadInBackground(SceneLoadProgress progress)
    {
        progress.Report(0.5f, "Growing test trees");
        Gate?.Wait(progress.CancellationToken);
        BuiltInBackground = progress.EditMode;
    }
}

/// <summary>
/// ADR 0238: projects and scenes open behind the loading window across frames (<see cref="EditorLoader"/>): the scene
/// builds on a worker while the editor ticks, the window follows the stages, Cancel leaves no tab.
/// </summary>
[Collection(nameof(SerialEditor))]
public sealed class EditorLoaderTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private readonly HeadlessEditor _editor = new(configure: o => o with { ShowSplash = true });

    public EditorLoaderTests()
    {
        RunUntilIdle(); // the start-up's new scene, behind the splash
        for (var i = 0; i < 120 && W.Splash.Visible; i++)
            _editor.Tick(); // its fade
    }

    public void Dispose()
    {
        GatedLoadableNode.Gate?.Set();
        GatedLoadableNode.Gate = null;
        _editor.Dispose();
    }

    private EditorWorkspace W => _editor.Workspace;

    // A scene file with a slow loadable, saved from a fresh tab that is closed again.
    private string SaveScene(string name)
    {
        W.Session.NewScene();
        var scene = W.Session.Active!;
        scene.AddNode(new GatedLoadableNode { Name = "Valley" }, scene.Root);
        scene.AddNode(new MeshInstance3D { Name = "Box", Mesh = new BoxMesh(), Position = new Vector3(1f, 0f, 0f) }, scene.Root);
        var path = Path.Combine(_editor.Directory, "Assets", "Scenes", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        W.Session.Save(scene, path);
        W.Session.Close(scene);
        _editor.Tick();
        return path;
    }

    private List<float> RunUntilIdle()
    {
        var progress = new List<float>();
        var watch = Stopwatch.StartNew();
        while (W.Loader.IsBusy)
        {
            Assert.True(watch.Elapsed < Timeout, $"The job is stuck at '{W.Loader.Step}'.");
            _editor.Tick();
            progress.Add(W.Loader.Progress);
            Thread.Sleep(1);
        }

        return progress;
    }

    private bool IsOpen(string path) => W.Session.Scenes.Any(s => s.FilePath is { } file && Path.GetFullPath(file) == Path.GetFullPath(path));

    [Fact]
    public void ASceneOpensAcrossFramesBehindTheLoadingPanel()
    {
        var path = SaveScene("Valley.mfscene");
        using var gate = new ManualResetEventSlim(false);
        GatedLoadableNode.Gate = gate;
        W.OpenSceneLoading(path);

        Assert.True(W.Loader.IsBusy);
        Assert.True(W.Splash.Visible);
        Assert.True(W.Splash.IsPanel); // the editor is up: a panel over it, not the start-up splash
        Assert.Equal("Loading Valley.mfscene…", W.Splash.Title);

        // The worker waits inside the loadable: the editor keeps ticking and the panel shows its stage.
        var watch = Stopwatch.StartNew();
        while (W.Loader.Step != "Growing test trees")
        {
            Assert.True(watch.Elapsed < Timeout, $"Never reached the loadable ('{W.Loader.Step}').");
            _editor.Tick();
            Thread.Sleep(1);
        }

        _editor.Tick(30);
        Assert.True(W.Loader.IsBusy);
        Assert.False(IsOpen(path));
        Assert.Equal("Growing test trees", W.Splash.Status);
        Assert.Equal("Valley.mfscene", W.Splash.Detail);

        gate.Set();
        var progress = RunUntilIdle();
        for (var i = 1; i < progress.Count; i++)
            Assert.True(progress[i] >= progress[i - 1], "The bar went back.");
        Assert.True(IsOpen(path));
        Assert.Same(W.Session.Active, W.Session.Scenes.Single(s => s.FilePath is not null && Path.GetFileName(s.FilePath) == "Valley.mfscene"));
        Assert.True(W.Session.Active!.Root.GetNode<GatedLoadableNode>("Valley").BuiltInBackground); // told it loads for the editor
        Assert.Equal(1f, W.Splash.Progress);
        Assert.Equal("Loading Valley.mfscene…", W.Loader.LastJob!.Value.Title);
        Assert.True(W.Loader.LastJob.Value.LongestFrame > TimeSpan.Zero);
    }

    [Fact]
    public void CancelStopsTheLoadAndLeavesNoTab()
    {
        var path = SaveScene("Cancelled.mfscene");
        using var gate = new ManualResetEventSlim(false);
        GatedLoadableNode.Gate = gate;
        W.OpenSceneLoading(path);
        var watch = Stopwatch.StartNew();
        while (W.Loader.Step != "Growing test trees")
        {
            Assert.True(watch.Elapsed < Timeout);
            _editor.Tick();
            Thread.Sleep(1);
        }

        W.Loader.Cancel(); // the gate's wait observes the load's token
        Assert.True(W.Loader.IsCancelled);
        RunUntilIdle();

        Assert.False(IsOpen(path));
        Assert.NotEmpty(W.Session.Scenes); // the editor goes on with a scene
        Assert.Equal("Cancelled", W.Splash.Status);

        // It opens again.
        gate.Set();
        W.OpenSceneLoading(path);
        RunUntilIdle();
        Assert.True(IsOpen(path));
    }

    [Fact]
    public void AJobStartedDuringAnotherWaitsAndAHoldKeepsAJobOpen()
    {
        var order = new List<string>();
        Action? release = null;
        W.Loader.Run("First", () =>
        {
            order.Add("first");
            release = W.Loader.Hold(); // a build, say
        });
        W.Loader.Run("Second", () => order.Add("second"));
        _editor.Tick(10);
        Assert.Equal(["first"], order);
        Assert.Equal("First", W.Loader.Title);

        release!();
        release(); // twice is once
        RunUntilIdle();
        Assert.Equal(["first", "second"], order);
        Assert.Equal("Second", W.Loader.LastJob!.Value.Title);
    }

    [Fact]
    public void AFailingOpenIsReportedAndEndsTheJob()
    {
        W.OpenSceneLoading(Path.Combine(_editor.Directory, "Assets", "Missing.mfscene"));
        RunUntilIdle();
        Assert.True(W.Message.Visible); // "Could not open Missing.mfscene"
        Assert.NotEmpty(W.Session.Scenes);
    }
}

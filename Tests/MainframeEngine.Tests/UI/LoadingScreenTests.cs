namespace MainframeEngine.Tests.UI;

/// <summary>The loading screen (ADR 0183) headless: the engine's document binds the load's state, glides, fades and frees itself.</summary>
[Collection(nameof(SerialRmlUi))]
public sealed class LoadingScreenTests
{
    [Fact]
    public void TheDefaultScreenShowsTheTitleStageAndProgress()
    {
        using var ui = new UiTestTree();
        var screen = new LoadingScreen { Title = "Test Game", Stage = "Growing trees", Progress = 0.4f };
        ui.Tree.Root.AddChild(screen);
        Assert.True(ui.Tree.IsLoading);
        Assert.Equal(LoadingScreen.DefaultSource, screen.Source);
        Assert.Equal(LoadingScreen.DefaultLayer, screen.Layer);

        ui.Tick(120); // the bar glides to the target
        var document = screen.Document!;
        Assert.True(document.IsLoaded);
        Assert.True(document.Modal); // the game below gets no input while loading
        Assert.Contains("Test Game", document.GetElementById("title")!.InnerRml, StringComparison.Ordinal);
        Assert.Contains("Growing trees", document.GetElementById("stage")!.InnerRml, StringComparison.Ordinal);
        Assert.Equal(0.4f, screen.ShownProgress, 3);
        Assert.Contains("40%", document.GetElementById("percent")!.InnerRml, StringComparison.Ordinal);

        // Progress only goes forward; the stage follows.
        screen.Progress = 0.2f;
        screen.Stage = "Compiling shaders";
        ui.Tick(10);
        Assert.Equal(0.4f, screen.ShownProgress, 3);
        Assert.Contains("Compiling shaders", document.GetElementById("stage")!.InnerRml, StringComparison.Ordinal);
    }

    [Fact]
    public void FadingOutFreesTheScreenAndEndsTheLoadingState()
    {
        using var ui = new UiTestTree();
        var screen = new LoadingScreen { Title = "Fade", FadeSeconds = 0.25f, Progress = 1f };
        ui.Tree.Root.AddChild(screen);
        ui.Tick(2);
        Assert.True(ui.Tree.IsLoading);

        screen.FadeOut();
        Assert.True(screen.IsFading);
        Assert.False(screen.Document!.Modal); // the game gets input while the screen fades
        ui.Tick(5);
        Assert.False(screen.IsFreed);
        Assert.True(ui.Tree.IsLoading);
        ui.Tick(20); // 0.25 s at 60 fps
        Assert.True(screen.IsFreed);
        Assert.False(ui.Tree.IsLoading);
    }

    [Fact]
    public void AScreenFollowingALoadFadesWhenTheLoadCompletes()
    {
        // An absolute path: this collection runs beside the ones that swap AssetDatabase.Current.
        var folder = Directory.CreateTempSubdirectory("mf-loading-screen").FullName;
        try
        {
            var path = Path.Combine(folder, "Level.mscene");
            File.WriteAllBytes(path, SceneSaver.ToJson(new Node3D { Name = "Level" }));
            using var ui = new UiTestTree();
            var load = ui.Tree.ChangeSceneToFileAsync(path);
            var screen = LoadingScreen.Show(ui.Tree, load, title: "Loaded");
            Assert.Same(load, screen.Load);

            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (!screen.IsFreed)
            {
                Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"Stuck: load {load.Stage}, fading {screen.IsFading}.");
                ui.Tick();
                Thread.Sleep(1);
            }

            Assert.True(load.IsDone);
            Assert.Equal("Level", ui.Tree.CurrentScene!.Name);
            Assert.False(ui.Tree.IsLoading);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}

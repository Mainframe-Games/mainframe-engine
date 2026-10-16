using System.Reflection;

namespace MainframeEngine.Tests.Project;

public sealed class GameHostUiOptionsTests
{
    [Fact]
    public void HotReloadWatchesTheGameAssembliesContentSource()
    {
        // The test assembly carries no MainframeContentSource; the engine assembly does in Debug.
        var settings = new ProjectSettings { Name = "T" };
        var options = GameHost.CreateUiOptions(settings, [typeof(GameHost).Assembly], hotReload: true);
        Assert.True(options.HotReload);
        Assert.Equal(UiServerOptions.SourceDirectoriesOf(typeof(GameHost).Assembly), options.SourceContentDirectories);
    }

    [Fact]
    public void WithoutHotReloadNoSourceDirectoriesAreWatched()
    {
        var options = GameHost.CreateUiOptions(new ProjectSettings { Name = "T" }, [typeof(GameHost).Assembly], hotReload: false);
        Assert.False(options.HotReload);
        Assert.Empty(options.SourceContentDirectories);
    }

    [Fact]
    public void TheProjectsUiScaleReachesTheUiServer()
    {
        // ADR 0181: every Project-mode layer, the dev overlay and the debugger scale by project.mfproj's "ui" section.
        var settings = new ProjectSettings { Name = "T" };
        settings.Ui.ReferenceResolution = new Vector2I(1280, 720);
        settings.Ui.MinScale = 0.5f;
        var options = GameHost.CreateUiOptions(settings, [], hotReload: false);
        Assert.Equal(UiScalingMode.ScaleWithScreenSize, options.Scaling.Mode);
        Assert.Equal(new System.Numerics.Vector2(1280, 720), options.Scaling.ReferenceResolution);
        Assert.Equal(0.5f, options.Scaling.MinScale);

        Assert.Equal(UiScaling.ConstantPixelSize, new UiServerOptions().Scaling); // engine subclasses (the editor, tests)
    }

    [Fact]
    public void TheDevOverlayFlagShowsTheOverlay()
    {
        var options = GameHostOptions.Parse(["--dev-overlay", "--hidden"]);
        Assert.True(options.DevOverlay);
        Assert.True(options.Apply(new ProjectSettings { Name = "T" }.ToEngineOptions()).DevOverlayVisible);
        Assert.False(GameHostOptions.Parse([]).Apply(new ProjectSettings { Name = "T" }.ToEngineOptions()).DevOverlayVisible);
    }
}

public sealed class GameHostDemoBuildTests
{
    // A game assembly as build/MainframeGame.props stamps it: [assembly: AssemblyMetadata("MainframeDemo", value)].
    private static System.Reflection.Emit.AssemblyBuilder Built(string value) => System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(
        new AssemblyName("DemoBuild" + Guid.NewGuid().ToString("N")), System.Reflection.Emit.AssemblyBuilderAccess.Run,
        [new System.Reflection.Emit.CustomAttributeBuilder(typeof(AssemblyMetadataAttribute).GetConstructor([typeof(string), typeof(string)])!,
            [GameHost.DemoMetadataKey, value])]);

    [Fact]
    public void TheBuildsDemoFlagComesFromTheFirstStampedAssembly()
    {
        Assert.Null(GameHost.BuiltAsDemo([null, typeof(GameHostDemoBuildTests).Assembly])); // not built with the game props
        Assert.True(GameHost.BuiltAsDemo([typeof(GameHostDemoBuildTests).Assembly, Built("true")]));
        Assert.False(GameHost.BuiltAsDemo([Built("false"), Built("true")]));
        Assert.Null(GameHost.BuiltAsDemo([Built("maybe")]));
    }

    [Fact]
    public void TheEditorsPlayIsADevelopmentRun()
    {
        Assert.True(GameHost.IsDevelopmentRun(new GameHostOptions { EditorPort = 4242 }));
    }
}

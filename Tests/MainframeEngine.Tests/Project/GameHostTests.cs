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
}

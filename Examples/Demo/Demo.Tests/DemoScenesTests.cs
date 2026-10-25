namespace Demo.Tests;

public sealed class DemoScenesTests
{
    [Fact]
    public void TabsAreTheElevenFeaturesInOrder() =>
        Assert.Equal(["basic_3d", "basic_2d", "audio_2d", "audio_3d", "sound_fx", "ui", "physics_2d", "physics_3d", "spine", "particles", "animation_3d"],
            DemoScenes.All.Select(s => s.Id));

    [Fact]
    public void EverySceneFileIsUnderContentScenes()
    {
        foreach (var scene in DemoScenes.All)
            Assert.Equal($"Assets/Scenes/{scene.Id}.mscene", scene.Path);
    }

    [Theory]
    [InlineData("physics_3d", "physics_3d")]
    [InlineData("nope", null)]
    [InlineData(null, null)]
    public void RootNamesMapBackToTabs(string? rootName, string? expected) =>
        Assert.Equal(expected, DemoScenes.ByRootName(rootName)?.Id);
}

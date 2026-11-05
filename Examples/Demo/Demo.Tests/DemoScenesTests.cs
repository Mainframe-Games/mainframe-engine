namespace Demo.Tests;

public sealed class DemoScenesTests
{
    [Fact]
    public void TabsAreTheFeaturesInOrder() =>
        Assert.Equal(["basic_3d", "particles", "shaders", "basic_2d", "sprites_2d", "tiles_2d", "navigation_2d", "paths_2d", "physics_2d", "physics_3d", "paths_3d", "spine", "animation_3d", "audio_2d", "audio_3d", "sound_fx", "adaptive_music", "ui"],
            DemoScenes.All.Select(s => s.Id));

    [Fact]
    public void CategoriesAreTheTopRowInOrder() =>
        Assert.Equal(["3d", "2d", "physics", "animation", "audio", "ui"], DemoScenes.Categories.Select(c => c.Id));

    [Fact]
    public void EveryCategoryHoldsAContiguousRunOfScenes()
    {
        // Page Down walks a category's scenes before moving to the next category.
        var order = DemoScenes.All.Select(s => DemoScenes.CategoryIndex(s.Category)).ToList();
        Assert.DoesNotContain(-1, order);
        Assert.Equal(order.Order(), order);
        Assert.Equal(Enumerable.Range(0, DemoScenes.Categories.Count), order.Distinct());
    }

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

namespace MainframeEngine.Editor.Tests.Projects.Demo;

public sealed class DemoReleaseTests
{
    [Fact]
    public void ReleaseBuildsUseTheTaggedAsset() =>
        Assert.Equal("https://github.com/Mainframe-Games/mainframe-engine/releases/download/v1.4.2/MainframeEngine.Demo-v1.4.2.zip",
            DemoRelease.AssetUrl("1.4.2").ToString());

    [Fact]
    public void DevelopmentBuildsHaveNoReleaseOfTheirOwn()
    {
        Assert.True(DemoRelease.IsDevelopment("0.0.0-dev"));
        Assert.Throws<ArgumentException>(() => DemoRelease.AssetUrl("0.0.0-dev"));
    }

    [Fact]
    public void AssetNamesCarryTheTag()
    {
        Assert.Equal("MainframeEngine.Demo-v1.4.2.zip", DemoRelease.AssetName("v1.4.2"));
        Assert.Equal("MainframeEngine.Forest-v1.4.2.zip", DemoRelease.AssetName("v1.4.2", ExampleProject.Forest));
    }

    [Theory]
    [InlineData("1.4.2+abc123")]
    [InlineData(" 1.4.2+abc-1 ")]
    public void BuildMetadataIsNotPartOfTheTag(string version)
    {
        Assert.False(DemoRelease.IsDevelopment(version));
        Assert.Equal("https://github.com/Mainframe-Games/mainframe-engine/releases/download/v1.4.2/MainframeEngine.Demo-v1.4.2.zip",
            DemoRelease.AssetUrl(version).ToString());
    }

    [Fact]
    public void TheForestHasItsOwnAsset()
    {
        Assert.Equal("https://github.com/Mainframe-Games/mainframe-engine/releases/download/v1.4.2/MainframeEngine.Forest-v1.4.2.zip",
            DemoRelease.AssetUrl("1.4.2", ExampleProject.Forest).ToString());
    }

    [Theory]
    [InlineData("1.5.0-rc.1")]
    [InlineData("1.5.0-rc.1+abc")]
    [InlineData("")]
    public void PrereleasesUseTheLatestRelease(string version)
    {
        Assert.True(DemoRelease.IsDevelopment(version));
        Assert.Throws<ArgumentException>(() => DemoRelease.AssetUrl(version));
    }
}

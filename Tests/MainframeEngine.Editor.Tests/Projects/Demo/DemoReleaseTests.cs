namespace MainframeEngine.Editor.Tests.Projects.Demo;

public sealed class DemoReleaseTests
{
    [Fact]
    public void ReleaseBuildsUseTheTaggedAsset() =>
        Assert.Equal("https://github.com/Mainframe-Games/mainframe-engine/releases/download/v1.4.2/MainframeEngine.Demo-v1.4.2.zip",
            DemoRelease.AssetUrl("1.4.2").ToString());

    [Fact]
    public void DevelopmentBuildsUseTheLatestRelease()
    {
        Assert.True(DemoRelease.IsDevelopment("0.0.0-dev"));
        Assert.Equal("https://github.com/Mainframe-Games/mainframe-engine/releases/latest/download/MainframeEngine.Demo.zip",
            DemoRelease.AssetUrl("0.0.0-dev").ToString());
    }
}

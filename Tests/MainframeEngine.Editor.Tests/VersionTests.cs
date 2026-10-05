namespace MainframeEngine.Editor.Tests;

public sealed class VersionTests
{
    [Fact]
    public void EditorAndEngineCoreShareOneVersion()
    {
        Assert.Equal(EngineInfo.Version, EditorBrand.AssemblyVersion);
        Assert.True(EditorBrand.VersionsMatch);
        Assert.Equal("v" + EngineInfo.Version, EditorBrand.Version);
        Assert.Equal("Mainframe Editor v" + EngineInfo.Version, EditorBrand.NameWithVersion);
    }
}

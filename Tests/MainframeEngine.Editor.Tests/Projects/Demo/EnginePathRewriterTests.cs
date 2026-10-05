namespace MainframeEngine.Editor.Tests.Projects.Demo;

public sealed class EnginePathRewriterTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("mf-props").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void OnlyTheEnginePathChanges()
    {
        var props = Path.Combine(_directory, "Directory.Build.props");
        File.WriteAllText(props, DemoZips.Props);
        EnginePathRewriter.Rewrite(props, "/Users/me/My Engine");

        var text = File.ReadAllText(props);
        Assert.Contains("<MainframeEnginePath>/Users/me/My Engine</MainframeEnginePath>", text);
        Assert.Contains("<TargetFramework>net10.0</TargetFramework>", text);
        Assert.Contains("<!-- The engine checkout this game builds against. -->", text);
    }

    [Fact]
    public void MissingPropertyIsAnError()
    {
        var props = Path.Combine(_directory, "Directory.Build.props");
        File.WriteAllText(props, "<Project><PropertyGroup /></Project>");
        Assert.Throws<DemoDownloadException>(() => EnginePathRewriter.Rewrite(props, "/x"));
    }
}

using MainframeEngine.UI.Rml;

namespace MainframeEngine.Tests.UI;

public sealed class RmlPathsTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("mf rml é").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void JoinKeepsExistingAbsolutePathsVerbatim()
    {
        var icon = Path.Combine(_directory, "icon.png");
        File.WriteAllBytes(icon, [1]);
        // Unix: rooted + exists (rule 1). Windows: drive path (rule 3).
        Assert.Equal(icon, RmlPaths.Join("Assets/UI/doc.rml", icon));
    }

    [Fact]
    public void LeadingSlashContentPathsStayContentRelative() =>
        Assert.Equal("Assets/Brand/logo-256.png", RmlPaths.Join("Assets/Editor/project_manager.rml", "/Assets/Brand/logo-256.png"));

    [Fact]
    public void MissingAbsolutePathsFallBackToRmlUisRule() =>
        Assert.Equal("nope/x.png", RmlPaths.Join("Assets/UI/doc.rml", "/nope/x.png"));

    [Theory]
    [InlineData("engine://viewport")]
    [InlineData("C:/Games/icon.png")]
    [InlineData("file:/x.png")]
    public void SchemesAndDrivesPassThrough(string path) => Assert.Equal(path, RmlPaths.Join("Assets/UI/doc.rml", path));

    [Theory]
    [InlineData("Assets/UI/doc.rml", "img/a.png", "Assets/UI/img/a.png")]
    [InlineData("Assets/UI/doc.rml", "../Brand/a.png", "Assets/Brand/a.png")]
    [InlineData("Assets/UI/doc.rml", "./a.png", "Assets/UI/a.png")]
    [InlineData("doc.rml", "a\\b.png", "a/b.png")]
    [InlineData("/Users/x/Assets/UI/doc.rml", "a.png", "/Users/x/Assets/UI/a.png")]
    [InlineData("/Users/x/Assets/UI/doc.rml", "../b.png", "/Users/x/Assets/b.png")]
    [InlineData("C:/Games/UI/doc.rml", "a.png", "C:/Games/UI/a.png")]
    public void RelativePathsJoinTheDocumentFolder(string document, string path, string expected) =>
        Assert.Equal(expected, RmlPaths.Join(document, path));
}

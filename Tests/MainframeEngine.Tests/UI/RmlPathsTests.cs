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
        Assert.Equal(icon, RmlPaths.Join("Content/UI/doc.rml", icon));
    }

    [Fact]
    public void LeadingSlashContentPathsStayContentRelative() =>
        Assert.Equal("Content/Brand/logo-256.png", RmlPaths.Join("Content/Editor/project_manager.rml", "/Content/Brand/logo-256.png"));

    [Fact]
    public void MissingAbsolutePathsFallBackToRmlUisRule() =>
        Assert.Equal("nope/x.png", RmlPaths.Join("Content/UI/doc.rml", "/nope/x.png"));

    [Theory]
    [InlineData("engine://viewport")]
    [InlineData("C:/Games/icon.png")]
    [InlineData("file:/x.png")]
    public void SchemesAndDrivesPassThrough(string path) => Assert.Equal(path, RmlPaths.Join("Content/UI/doc.rml", path));

    [Theory]
    [InlineData("Content/UI/doc.rml", "img/a.png", "Content/UI/img/a.png")]
    [InlineData("Content/UI/doc.rml", "../Brand/a.png", "Content/Brand/a.png")]
    [InlineData("Content/UI/doc.rml", "./a.png", "Content/UI/a.png")]
    [InlineData("doc.rml", "a\\b.png", "a/b.png")]
    public void RelativePathsJoinTheDocumentFolder(string document, string path, string expected) =>
        Assert.Equal(expected, RmlPaths.Join(document, path));
}

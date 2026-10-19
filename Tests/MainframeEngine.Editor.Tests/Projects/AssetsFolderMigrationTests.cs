namespace MainframeEngine.Editor.Tests.Projects;

public sealed class AssetsFolderMigrationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mf-assets-migration").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Write(string relative, string text)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private string Read(string relative) => File.ReadAllText(Path.Combine(_root, relative));

    [Fact]
    public void OnlyAProjectWithContentAndNoAssetsNeedsIt()
    {
        Assert.False(AssetsFolderMigration.IsNeeded(_root));
        Directory.CreateDirectory(Path.Combine(_root, "Content"));
        Assert.True(AssetsFolderMigration.IsNeeded(_root));
        Directory.CreateDirectory(Path.Combine(_root, "Assets"));
        Assert.False(AssetsFolderMigration.IsNeeded(_root));
    }

    [Fact]
    public void TheFolderMovesAndContentPathsFollowItEverywhere()
    {
        Write("project.mfproj", """{ "format": 3, "name": "Content", "mainScene": "Content/Scenes/Main.mscene" }""");
        Write("Content/Scenes/Main.mscene", """{ "File": "Content/Audio/hit.ogg", "path": "Content/Sky/sky.png" }""");
        Write("Content/Scenes/Main.mscene.meta", """{ "uid": "scn_0123456789ab" }""");
        Write("Content/UI/hud.rml", """<link href="/Content/UI/widgets/widgets.rcss"/><p>Content</p>""");
        Write("Game/Src/Player.cs", """var s = "Content/Audio/jump.ogg"; var label = "Content"; var mine = "MyContent/x";""");
        Write("Game/Game.csproj", """<MainframeContentSource>$(MSBuildProjectDirectory)/../Content</MainframeContentSource>""");
        Write("Game.Desktop/Game.Desktop.csproj",
            """<None Include="../Content/**" LinkBase="Content" /><LocaleContentRoot>../Content</LocaleContentRoot>""");
        Write("Game/obj/project.assets.json", """{ "x": "Content/ignored" }""");

        var rewritten = AssetsFolderMigration.Run(_root);

        Assert.False(Directory.Exists(Path.Combine(_root, "Content")));
        Assert.Equal("""{ "uid": "scn_0123456789ab" }""", Read("Assets/Scenes/Main.mscene.meta"));
        Assert.Equal("""{ "format": 3, "name": "Content", "mainScene": "Assets/Scenes/Main.mscene" }""", Read("project.mfproj"));
        Assert.Equal("""{ "File": "Assets/Audio/hit.ogg", "path": "Assets/Sky/sky.png" }""", Read("Assets/Scenes/Main.mscene"));
        // UI text that merely says "Content" is the game's, not a path.
        Assert.Equal("""<link href="/Assets/UI/widgets/widgets.rcss"/><p>Content</p>""", Read("Assets/UI/hud.rml"));
        Assert.Equal("""var s = "Assets/Audio/jump.ogg"; var label = "Content"; var mine = "MyContent/x";""", Read("Game/Src/Player.cs"));
        Assert.Equal("""<MainframeContentSource>$(MSBuildProjectDirectory)/../Assets</MainframeContentSource>""", Read("Game/Game.csproj"));
        Assert.Equal("""<None Include="../Assets/**" LinkBase="Assets" /><LocaleContentRoot>../Assets</LocaleContentRoot>""",
            Read("Game.Desktop/Game.Desktop.csproj"));
        Assert.Equal("""{ "x": "Content/ignored" }""", Read("Game/obj/project.assets.json")); // build output is left alone
        Assert.Equal(
            ["Assets/Scenes/Main.mscene", "Assets/UI/hud.rml", "Game.Desktop/Game.Desktop.csproj", "Game/Game.csproj", "Game/Src/Player.cs", "project.mfproj"],
            rewritten);
    }

    [Fact]
    public void AMigratedProjectStillLoadsItsSettings()
    {
        Write("project.mfproj", """{ "format": 3, "name": "Old", "mainScene": "Content/Scenes/Main.mscene", "window": { "icon": "Content/icon.png" } }""");
        Write("Content/Scenes/Main.mscene", "{}");

        AssetsFolderMigration.Run(_root);
        var settings = ProjectSettings.Load(Path.Combine(_root, ProjectSettings.FileName));

        Assert.Equal("Assets/Scenes/Main.mscene", settings.MainScene);
        Assert.Equal("Assets/icon.png", settings.Window.Icon);
        Assert.True(File.Exists(Path.Combine(_root, settings.MainScene!)));
    }

    [Fact]
    public void RunningItWithoutAContentFolderThrows() =>
        Assert.Throws<InvalidOperationException>(() => AssetsFolderMigration.Run(_root));
}

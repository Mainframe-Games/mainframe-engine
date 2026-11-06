namespace MainframeEngine.Editor.Tests.Projects;

public sealed class FileExtensionMigrationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mf-ext-migration").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Write(string relative, string text)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private string Read(string relative) => File.ReadAllText(Path.Combine(_root, relative));

    private bool Exists(string relative) => File.Exists(Path.Combine(_root, relative));

    [Fact]
    public void OnlyAProjectWithALegacyFileNeedsIt()
    {
        Write("Assets/Scenes/Main.mfscene", "{}");
        Write("Assets/editor_layout.json", "{}"); // not a shader layout
        Assert.False(FileExtensionMigration.IsNeeded(_root));
        Write("Assets/logo.png.meta", "{}");
        Assert.True(FileExtensionMigration.IsNeeded(_root));
    }

    [Fact]
    public void EveryLegacyFileIsRenamedAndItsPathsFollow()
    {
        Write("project.mfproj", """{ "format": 5, "mainScene": "Assets/Scenes/Main.mscene" }""");
        Write("Assets/Scenes/Main.mscene", """{ "uid": "scn_0123456789ab", "resources": { "1": { "ref": "res_0123456789ab", "path": "Assets/Data/Stats.mres" } } }""");
        Write("Assets/Data/Stats.mres", """{ "uid": "res_0123456789ab" }""");
        Write("Assets/Lighting/probes.mres", """{ "DataFile": "probes.probes" }""");
        Write("Assets/Lighting/probes.probes", "binary");
        Write("Assets/Music/theme.mmusic", """{ "song": "Assets/Music/theme.msong" }""");
        Write("Assets/Music/theme.msong", "{}");
        Write("Assets/logo.png.meta", """{ "uid": "tex_0123456789ab" }""");
        Write("Assets/Shaders/wave.slang.layout.json", "{}");
        Write("Assets/Shaders/wave.slang.spvlock", "hash");
        Write("Assets/UI/settings.layout.json", "{}"); // someone's own file, not a shader's
        Write("Game/Src/Player.cs", """Tree.ChangeSceneToFile("Assets/Scenes/Main.mscene"); var r = "x.mresults";""");
        Write("Game/obj/project.assets.json", """{ "x": "Main.mscene" }""");

        var result = FileExtensionMigration.Run(_root);

        Assert.Equal(
        [
            "Assets/Data/Stats.mfres", "Assets/Lighting/probes.mfprobes", "Assets/Lighting/probes.mfres", "Assets/Music/theme.mfmusic",
            "Assets/Music/theme.mfsong", "Assets/Scenes/Main.mfscene", "Assets/Shaders/wave.slang.mflayout.json",
            "Assets/Shaders/wave.slang.mfspvlock", "Assets/logo.png.mfmeta",
        ], result.Renamed);
        Assert.Empty(result.Skipped);
        Assert.True(Exists("Assets/UI/settings.layout.json"));
        Assert.False(FileExtensionMigration.IsNeeded(_root));

        Assert.Equal("""{ "format": 5, "mainScene": "Assets/Scenes/Main.mfscene" }""", Read("project.mfproj"));
        Assert.Contains("\"Assets/Data/Stats.mfres\"", Read("Assets/Scenes/Main.mfscene"), StringComparison.Ordinal);
        Assert.Equal("""{ "DataFile": "probes.mfprobes" }""", Read("Assets/Lighting/probes.mfres"));
        Assert.Equal("""{ "song": "Assets/Music/theme.mfsong" }""", Read("Assets/Music/theme.mfmusic"));
        Assert.Equal("""Tree.ChangeSceneToFile("Assets/Scenes/Main.mfscene"); var r = "x.mresults";""", Read("Game/Src/Player.cs"));
        Assert.Equal("""{ "x": "Main.mscene" }""", Read("Game/obj/project.assets.json")); // build output is left alone
        Assert.Equal(
            ["Assets/Lighting/probes.mfres", "Assets/Music/theme.mfmusic", "Assets/Scenes/Main.mfscene", "Game/Src/Player.cs", "project.mfproj"],
            result.Rewritten);
    }

    [Fact]
    public void AFileWhoseNewNameIsTakenIsLeftAlone()
    {
        Write("Assets/logo.png.meta", "old");
        Write("Assets/logo.png.mfmeta", "new");

        var result = FileExtensionMigration.Run(_root);

        Assert.Equal(["Assets/logo.png.meta"], result.Skipped);
        Assert.Equal("new", Read("Assets/logo.png.mfmeta"));
        Assert.Equal("old", Read("Assets/logo.png.meta"));
    }

    [Theory]
    [InlineData("Main.mscene", "Main.mfscene")]
    [InlineData("a.png.meta", "a.png.mfmeta")]
    [InlineData("wave.slang.layout.json", "wave.slang.mflayout.json")]
    [InlineData("editor_layout.json", null)]
    [InlineData("a.layout.json", null)]
    [InlineData("Main.mfscene", null)]
    [InlineData("a.png.mfmeta", null)]
    public void UpgradedNameMapsOnlyLegacyNames(string name, string? expected) =>
        Assert.Equal(expected, FileExtensionMigration.UpgradedName(name));
}

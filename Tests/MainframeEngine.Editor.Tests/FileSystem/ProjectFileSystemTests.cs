namespace MainframeEngine.Editor.Tests.FileSystem;

[Collection(nameof(SerialEditor))]
public sealed class ProjectFileSystemTests : IDisposable
{
    private readonly FileSystemProject _project = new();
    private readonly ProjectFileSystem _fs;

    public ProjectFileSystemTests()
    {
        _fs = new ProjectFileSystem(_project.Root, _project.Database);
    }

    public void Dispose()
    {
        _fs.Dispose();
        _project.Dispose();
    }

    private ProjectFileEntry Entry(string projectPath) =>
        _fs.Find(projectPath) ?? throw new InvalidOperationException($"'{projectPath}' is not listed.");

    private static string[] Names(IEnumerable<ProjectFileEntry> entries) => [.. entries.Select(e => e.Name)];

    [Fact]
    public void TreeListsFoldersFirstThenFilesByNameAndFlattensDepthFirst()
    {
        Assert.Equal(["Content", "project.mfproj"], Names(_fs.Root.Children));
        Assert.Equal(["Materials", "Scenes", "Textures"], Names(Entry("Content").Children));
        Assert.Equal(["Main.mscene", "Player.mscene"], Names(Entry("Content/Scenes").Children));
        Assert.Equal(["wood.png"], Names(_fs.ListFolder(Entry("Content/Textures"))));
        Assert.Empty(_fs.ListFolder(Entry(FileSystemProject.WoodPath)));

        var wood = Entry(FileSystemProject.WoodPath);
        Assert.Equal(FileSystemProject.WoodPath, wood.ProjectPath);
        Assert.Equal(_project.Abs(FileSystemProject.WoodPath), wood.FullPath);
        Assert.Equal(".png", wood.Extension);
        Assert.Equal(3, wood.Depth);
        Assert.Same(Entry("Content/Textures"), wood.Parent);
        Assert.True(wood.Size > 0);
        Assert.Same(wood, _fs.Find(wood.FullPath));

        var rows = _fs.Flatten(e => e.Depth < 1 || e.Name is "Content" or "Scenes");
        Assert.Equal(
            [_fs.Root.Name, "Content", "Materials", "Scenes", "Main.mscene", "Player.mscene", "Textures", "project.mfproj"],
            Names(rows));
        Assert.Equal([0, 1, 2, 2, 3, 3, 2, 1], rows.Select(r => r.Depth));
    }

    [Fact]
    public void MetaAndHiddenFoldersAreHiddenUntilToggled()
    {
        Assert.Null(_fs.Find("bin"));
        Assert.Null(_fs.Find(".git"));
        Assert.Null(_fs.Find(".gitignore"));
        Assert.Null(_fs.Find(FileSystemProject.WoodPath + ".meta"));

        var version = _fs.Version;
        _fs.ShowMeta = true;
        Assert.True(_fs.Version > version);
        Assert.Equal(["wood.png", "wood.png.meta"], Names(Entry("Content/Textures").Children));
        Assert.Null(_fs.Find("obj"));

        _fs.ShowHidden = true;
        Assert.Equal([".git", "bin", "Content", "obj", ".gitignore", "project.mfproj"], Names(_fs.Root.Children));
        Assert.NotNull(_fs.Find("bin/Game.dll"));

        _fs.ShowHidden = false;
        _fs.ShowMeta = false;
        Assert.Null(_fs.Find("bin"));
        Assert.Null(_fs.Find(FileSystemProject.WoodPath + ".meta"));
    }

    [Fact]
    public void KindsIconsAndFamiliesComeFromExtensionsAndRootTypes()
    {
        var main = Entry(FileSystemProject.MainPath);
        Assert.Equal(FileKind.Scene, main.Kind);
        Assert.Equal("Node3D", main.RootTypeName);
        Assert.Equal(EditorIcons.For(typeof(Node3D)), main.Icon);
        Assert.Equal("icon-3d", main.Family);
        Assert.Equal(_project.MainUid, main.Uid);

        var player = Entry(FileSystemProject.PlayerPath);
        Assert.Equal("AllHintsNode", player.RootTypeName);
        Assert.Equal("icon-3d", player.Family); // a Node3D subclass

        var settings = Entry(FileSystemProject.SettingsPath);
        Assert.Equal(FileKind.Resource, settings.Kind);
        Assert.Equal("TestSettings", settings.RootTypeName);
        Assert.Equal(EditorIcons.For(typeof(TestSettings)), settings.Icon);
        Assert.Equal("icon-resource", settings.Family);
        Assert.Equal(_project.SettingsUid, settings.Uid);

        var sky = Entry(FileSystemProject.SkyPath);
        Assert.Equal("cloud", sky.Icon); // by resource type, not the generic resource icon

        var wood = Entry(FileSystemProject.WoodPath);
        Assert.Equal(FileKind.Texture, wood.Kind);
        Assert.Equal(EditorIcons.ForFile(wood.FullPath), wood.Icon);
        Assert.Equal(EditorIcons.FileFamily(wood.FullPath), wood.Family);
        Assert.Equal(_project.WoodUid, wood.Uid);

        var folder = Entry("Content");
        Assert.Equal(FileKind.Folder, folder.Kind);
        Assert.Equal("folder", folder.Icon);

        Assert.Equal(FileKind.Project, Entry("project.mfproj").Kind);
        Assert.Equal(EditorIcons.ForFile("project.mfproj"), Entry("project.mfproj").Icon);

        File.WriteAllText(_project.Abs("Content/Scenes/Ui.mscene"), """{ "format": 1, "uid": "scn_00000000aaaa", "root": { "type": "UiLayer", "name": "Ui" } }""");
        File.WriteAllText(_project.Abs("Content/Scenes/Body.mscene"), """{ "format": 1, "root": { "type": "RigidBody3D", "name": "Body" } }""");
        File.WriteAllText(_project.Abs("Content/Scenes/Odd.mscene"), """{ "format": 1, "root": { "type": "NoSuchNode", "name": "Odd" } }""");
        File.WriteAllText(_project.Abs("Content/Scenes/Inherited.mscene"), """{ "format": 1, "root": { "instance": "scn_0000000000ab", "path": "Content/Scenes/Player.mscene", "name": "P" } }""");
        File.WriteAllText(_project.Abs("Content/locale.po"), "");
        _fs.Refresh();
        Assert.Equal((EditorIcons.For(typeof(UiLayer)), "icon-ui"), (Entry("Content/Scenes/Ui.mscene").Icon, Entry("Content/Scenes/Ui.mscene").Family));
        Assert.Equal("scn_00000000aaaa", Entry("Content/Scenes/Ui.mscene").Uid);
        Assert.Equal("icon-physics", Entry("Content/Scenes/Body.mscene").Family);
        Assert.Equal((EditorIcons.ForFile("x.mscene"), EditorIcons.FileFamily("x.mscene")), (Entry("Content/Scenes/Odd.mscene").Icon, Entry("Content/Scenes/Odd.mscene").Family));
        Assert.Equal("instance", Entry("Content/Scenes/Inherited.mscene").RootTypeName);
        Assert.Equal(FileKind.Translation, Entry("Content/locale.po").Kind);
    }

    [Fact]
    public void RefreshIsCheapForUnchangedFilesAndBumpsVersionOnlyOnChanges()
    {
        var main = Entry(FileSystemProject.MainPath);
        var peek = main.Peek;
        Assert.NotNull(peek);
        var version = _fs.Version;

        _fs.Refresh();
        Assert.Equal(version, _fs.Version);
        Assert.Same(main, Entry(FileSystemProject.MainPath)); // entries are kept
        Assert.Same(peek, main.Peek);                       // and not re-read

        File.WriteAllText(_project.Abs("Content/notes.txt"), "hi");
        _fs.Refresh();
        Assert.True(_fs.Version > version);
        Assert.Equal(FileKind.Data, Entry("Content/notes.txt").Kind);

        File.Delete(_project.Abs("Content/notes.txt"));
        _fs.Refresh();
        Assert.Null(_fs.Find("Content/notes.txt"));
    }

    [Fact]
    public void MissingDependenciesAreFlaggedForPathsAndUids()
    {
        Assert.Equal(FileBadges.None, Entry(FileSystemProject.PlayerPath).Badges);
        Assert.Equal(FileBadges.None, Entry(FileSystemProject.MainPath).Badges);
        Assert.Null(Entry(FileSystemProject.MainPath).BadgeText);

        File.Delete(_project.Abs(FileSystemProject.WoodPath));
        var version = _fs.Version;
        _fs.Refresh();
        Assert.True(_fs.Version > version);
        var player = Entry(FileSystemProject.PlayerPath);
        Assert.Equal(FileBadges.MissingDependency, player.Badges);
        Assert.Equal("Missing: Content/Textures/wood.png", player.BadgeText);
        Assert.True(Entry(FileSystemProject.SkyPath).Badges.HasFlag(FileBadges.MissingDependency));

        // Main references Settings.mres by UID; the hint is gone too once it is deleted.
        File.Delete(_project.Abs(FileSystemProject.SettingsPath));
        _fs.Refresh();
        Assert.Equal(FileBadges.MissingDependency, Entry(FileSystemProject.MainPath).Badges);
        Assert.Contains("Missing: Content/Materials/Settings.mres", Entry(FileSystemProject.MainPath).BadgeText, StringComparison.Ordinal);
    }

    [Fact]
    public void BrokenJsonIsAnImportError()
    {
        File.WriteAllText(_project.Abs("Content/Scenes/Broken.mscene"), "{ \"format\": 1, \"root\": { oops } }");
        File.WriteAllText(_project.Abs("project.mfproj"), "{ \"name\": ");
        _fs.Refresh();

        var broken = Entry("Content/Scenes/Broken.mscene");
        Assert.Equal(FileBadges.ImportError, broken.Badges);
        Assert.StartsWith("Invalid JSON:", broken.BadgeText, StringComparison.Ordinal);
        Assert.Equal("movie", broken.Icon);
        Assert.Equal(FileBadges.ImportError, Entry("project.mfproj").Badges);

        File.WriteAllText(_project.Abs("Content/Scenes/Broken.mscene"), "{ \"format\": 1, \"root\": { \"type\": \"Node2D\", \"name\": \"Fixed\" } }");
        _fs.Refresh();
        Assert.Equal(FileBadges.None, broken.Badges);
        Assert.Equal("icon-2d", broken.Family);
    }

    [Fact]
    public void UnsavedAndReportedImportErrorsAreBadges()
    {
        var main = Entry(FileSystemProject.MainPath);
        var version = _fs.Version;
        _fs.SetUnsaved(new HashSet<string> { main.FullPath });
        Assert.Equal(FileBadges.Unsaved, main.Badges);
        Assert.Equal("Unsaved changes", main.BadgeText);
        Assert.True(_fs.Version > version);

        _fs.SetUnsaved(new HashSet<string>());
        Assert.Equal(FileBadges.None, main.Badges);

        var wood = Entry(FileSystemProject.WoodPath);
        _fs.ReportImportError(wood.FullPath, "Cannot read the image 'wood.png': bad data");
        Assert.Equal(FileBadges.ImportError, wood.Badges);
        Assert.Equal("Cannot read the image 'wood.png': bad data", wood.BadgeText);

        // A changed file clears the error (it gets another chance).
        FileSystemProject.WritePng(wood.FullPath, 16, 16);
        File.SetLastWriteTimeUtc(wood.FullPath, DateTime.UtcNow.AddMinutes(1));
        _fs.Refresh();
        Assert.Equal(FileBadges.None, wood.Badges);
    }

    [Fact]
    public void TheWatcherReportsChangesThroughPump()
    {
        _fs.StartWatching(TimeSpan.FromMilliseconds(30));
        Assert.True(_fs.IsWatching);
        Assert.False(_fs.PumpChanges());

        File.WriteAllText(_project.Abs("Content/Scenes/new.txt"), "x");
        Assert.True(FileSystemProject.WaitUntil(_fs.PumpChanges), "No change was reported.");
        Assert.NotNull(_fs.Find("Content/Scenes/new.txt"));
    }
}

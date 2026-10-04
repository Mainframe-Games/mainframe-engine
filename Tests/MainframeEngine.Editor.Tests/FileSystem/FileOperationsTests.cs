namespace MainframeEngine.Editor.Tests.FileSystem;

[Collection(nameof(SerialEditor))]
public sealed class FileOperationsTests : IDisposable
{
    private readonly FileSystemProject _project = new();
    private readonly FakeTrash _trash = new();
    private readonly FileOperations _ops;

    public FileOperationsTests()
    {
        _ops = new FileOperations(_project.Root, _project.Database, _trash);
    }

    public void Dispose()
    {
        _trash.Dispose();
        _project.Dispose();
    }

    private static void Succeeded(FileOperationResult result) => Assert.True(result.Succeeded, result.Error);

    /// <summary>Loads a scene from disk by path (fresh cache: UIDs resolve through the database) and returns its root.</summary>
    private static Node LoadScene(string projectPath)
    {
        ResourceLoader.ClearCache();
        return ResourceLoader.Load<PackedScene>(projectPath).Instantiate();
    }

    [Fact]
    public void RenamingATextureFixesStringPathsAndKeepsItsMetaAndUid()
    {
        var mainBefore = File.ReadAllBytes(_project.Abs(FileSystemProject.MainPath));

        var result = _ops.Rename(_project.Abs(FileSystemProject.WoodPath), "oak.png");

        Succeeded(result);
        Assert.Equal(_project.Abs("Content/Textures/oak.png"), result.Path);
        Assert.False(File.Exists(_project.Abs(FileSystemProject.WoodPath)));
        Assert.False(File.Exists(_project.Abs(FileSystemProject.WoodPath + ".meta")));
        Assert.True(File.Exists(_project.Abs("Content/Textures/oak.png.meta")));
        Assert.Equal("Content/Textures/oak.png", _project.Database.GetPath(_project.WoodUid));
        Assert.Equal(_project.WoodUid, _project.Database.ReadOrCreateMeta("Content/Textures/oak.png", create: false)!.Uid);

        Assert.Equal(
            new[] { "project.mfproj", "Content/Materials/Sky.mres", FileSystemProject.PlayerPath }.Select(_project.Abs).Order(),
            result.UpdatedFiles.Order());
        Assert.Contains("\"Content/Textures/oak.png\"", _project.Read(FileSystemProject.PlayerPath), StringComparison.Ordinal);
        Assert.DoesNotContain("wood.png", _project.Read(FileSystemProject.PlayerPath), StringComparison.Ordinal);
        Assert.Contains("\"icon\": \"Content/Textures/oak.png\"", _project.Read("project.mfproj"), StringComparison.Ordinal);
        Assert.EndsWith("}\n", _project.Read("project.mfproj"), StringComparison.Ordinal); // the trailing newline is kept
        Assert.Equal(mainBefore, File.ReadAllBytes(_project.Abs(FileSystemProject.MainPath))); // untouched files are not rewritten

        var player = (AllHintsNode)LoadScene(FileSystemProject.PlayerPath);
        Assert.Equal("Content/Textures/oak.png", player.Texture);
        player.Free();
    }

    [Fact]
    public void MovingScenesFixesInstanceHintsAndTheMainScene()
    {
        Succeeded(_ops.CreateFolder("Content/Scenes", "Levels"));
        Succeeded(_ops.Move(FileSystemProject.PlayerPath, "Content/Scenes/Levels"));
        var moveMain = _ops.Move(_project.Abs(FileSystemProject.MainPath), _project.Abs("Content/Scenes/Levels"));
        Succeeded(moveMain);
        Assert.Equal([_project.Abs("project.mfproj")], moveMain.UpdatedFiles);

        const string newMain = "Content/Scenes/Levels/Main.mscene";
        var mainJson = _project.Read(newMain);
        Assert.Contains("\"path\": \"Content/Scenes/Levels/Player.mscene\"", mainJson, StringComparison.Ordinal);
        Assert.Contains("\"path\": \"Content/Materials/Settings.mres\"", mainJson, StringComparison.Ordinal); // unrelated hint kept
        Assert.Contains($"\"mainScene\": \"{newMain}\"", _project.Read("project.mfproj"), StringComparison.Ordinal);
        Assert.Equal(newMain, _project.Database.GetPath(_project.MainUid));
        Assert.Equal("Content/Scenes/Levels/Player.mscene", _project.Database.GetPath(_project.PlayerUid));

        AssertProjectLoadsAndRoundTrips(newMain, "Content/Textures/wood.png");
    }

    [Fact]
    public void MovingAFolderFixesReferencesToEveryFileInside()
    {
        Succeeded(_ops.CreateFolder("Content", "Art"));
        var result = _ops.Move("Content/Textures", "Content/Art");
        Succeeded(result);
        Assert.True(File.Exists(_project.Abs("Content/Art/Textures/wood.png.meta")));
        Assert.Equal("Content/Art/Textures/wood.png", _project.Database.GetPath(_project.WoodUid));
        Assert.Contains("\"Content/Art/Textures/wood.png\"", _project.Read(FileSystemProject.PlayerPath), StringComparison.Ordinal);

        var scenes = _ops.Rename("Content/Scenes", "Levels");
        Succeeded(scenes);
        Assert.Contains("\"path\": \"Content/Levels/Player.mscene\"", _project.Read("Content/Levels/Main.mscene"), StringComparison.Ordinal);
        Assert.Contains("\"mainScene\": \"Content/Levels/Main.mscene\"", _project.Read("project.mfproj"), StringComparison.Ordinal);
        Assert.Equal("Content/Levels/Main.mscene", _project.Database.GetPath(_project.MainUid));

        AssertProjectLoadsAndRoundTrips("Content/Levels/Main.mscene", "Content/Art/Textures/wood.png");
    }

    private void AssertProjectLoadsAndRoundTrips(string mainPath, string texturePath)
    {
        var main = LoadScene(mainPath);
        var player = Assert.IsType<AllHintsNode>(main.GetNode("Player"));
        Assert.Equal(texturePath, player.Texture);
        var settings = Assert.IsType<AllHintsNode>(main.GetNode("Hinted")).Settings;
        Assert.NotNull(settings);
        Assert.Equal("über", settings.Label);

        // The rewritten file is exactly what the scene writer produces.
        var onDisk = File.ReadAllBytes(_project.Abs(mainPath));
        Assert.Equal(System.Text.Encoding.UTF8.GetString(onDisk), System.Text.Encoding.UTF8.GetString(SceneSaver.ToJson(main, _project.MainUid)));
        main.Free();
    }

    [Fact]
    public void CreatesFoldersScenesAndResources()
    {
        var folder = _ops.CreateFolder(_project.Abs("Content"), "Prefabs");
        Succeeded(folder);
        Assert.True(Directory.Exists(folder.Path));

        var scene = _ops.CreateScene("Content/Prefabs", "Enemy");
        Succeeded(scene);
        Assert.Equal(_project.Abs("Content/Prefabs/Enemy.mscene"), scene.Path);
        Assert.NotNull(_project.Database.GetUid("Content/Prefabs/Enemy.mscene"));
        var enemy = LoadScene("Content/Prefabs/Enemy.mscene");
        Assert.IsType<Node3D>(enemy);
        Assert.Equal("Enemy", enemy.Name);
        enemy.Free();

        Succeeded(_ops.CreateScene("Content/Prefabs", "Hud.mscene", "Node2D"));
        Assert.IsType<Node2D>(LoadScene("Content/Prefabs/Hud.mscene"));

        var resource = _ops.CreateResource("Content/Prefabs", "Night", typeof(Sky));
        Succeeded(resource);
        ResourceLoader.ClearCache();
        Assert.IsType<Sky>(ResourceLoader.Load<Resource>("Content/Prefabs/Night.mres"));

        Assert.False(_ops.CreateScene("Content/Prefabs", "Bad", "NoSuchType").Succeeded);
        Assert.False(_ops.CreateResource("Content/Prefabs", "Bad", typeof(string)).Succeeded);
        var duplicate = _ops.CreateScene("Content/Prefabs", "Enemy");
        Assert.False(duplicate.Succeeded);
        Assert.Equal("'Enemy.mscene' already exists in 'Content/Prefabs'.", duplicate.Error);
    }

    [Fact]
    public void DeleteGoesThroughTheTrashWithItsMetaAndForgetsTheUid()
    {
        var result = _ops.Delete(FileSystemProject.WoodPath);

        Succeeded(result);
        Assert.Null(result.Error);
        Assert.Equal([_project.Abs(FileSystemProject.WoodPath), _project.Abs(FileSystemProject.WoodPath + ".meta")], _trash.Trashed);
        Assert.False(File.Exists(_project.Abs(FileSystemProject.WoodPath)));
        Assert.Null(_project.Database.GetPath(_project.WoodUid));

        Succeeded(_ops.Delete("Content/Scenes"));
        Assert.Equal(_project.Abs("Content/Scenes"), _trash.Trashed[^1]);
        Assert.Null(_project.Database.GetPath(_project.MainUid));
        Assert.Null(_project.Database.GetPath(_project.PlayerUid));
    }

    [Fact]
    public void AFailingTrashDeletesNothingAndSaysSo()
    {
        using var failing = new FakeTrash(fail: true);
        var ops = new FileOperations(_project.Root, _project.Database, failing);

        var result = ops.Delete(FileSystemProject.WoodPath);

        Assert.False(result.Succeeded);
        Assert.True(result.TrashUnavailable);
        Assert.Contains("wood.png", result.Error, StringComparison.Ordinal);
        Assert.True(File.Exists(_project.Abs(FileSystemProject.WoodPath)));
        Assert.True(File.Exists(_project.Abs(FileSystemProject.WoodPath + ".meta")));
        Assert.Equal(FileSystemProject.WoodPath, _project.Database.GetPath(_project.WoodUid));

        Succeeded(ops.DeletePermanently(FileSystemProject.WoodPath));
        Assert.False(File.Exists(_project.Abs(FileSystemProject.WoodPath)));
        Assert.False(File.Exists(_project.Abs(FileSystemProject.WoodPath + ".meta")));
        Assert.Null(_project.Database.GetPath(_project.WoodUid));
        Assert.Empty(failing.Trashed);
    }

    [Theory]
    [InlineData("", "The name cannot be empty.")]
    [InlineData("   ", "The name cannot be empty.")]
    [InlineData("a/b.png", "'a/b.png' cannot contain '/' or '\\'.")]
    [InlineData("a\\b.png", "'a\\b.png' cannot contain '/' or '\\'.")]
    [InlineData("..", "'..' is not a valid name ('.' and '..' refer to folders).")]
    [InlineData("a:b.png", "'a:b.png' contains the invalid character ':'.")]
    [InlineData("what?.png", "'what?.png' contains the invalid character '?'.")]
    [InlineData("CON.png", "'CON.png' is a reserved name on Windows.")]
    [InlineData("name.", "'name.' cannot end with a dot.")]
    [InlineData(" lead.png", "' lead.png' cannot start or end with a space.")]
    [InlineData("x.png.meta", "'x.png.meta': .meta files are managed by the editor.")]
    public void InvalidNamesAreRejectedWithAMessage(string name, string message)
    {
        var result = _ops.Rename(FileSystemProject.WoodPath, name);
        Assert.False(result.Succeeded);
        Assert.Equal(message, result.Error);
        Assert.True(File.Exists(_project.Abs(FileSystemProject.WoodPath)));
    }

    [Fact]
    public void ConflictsAndImpossibleMovesAreRejected()
    {
        File.WriteAllText(_project.Abs("Content/Textures/stone.png"), "x");
        var conflict = _ops.Rename(FileSystemProject.WoodPath, "stone.png");
        Assert.False(conflict.Succeeded);
        Assert.Equal("'stone.png' already exists in 'Content/Textures'.", conflict.Error);

        Succeeded(_ops.CreateFolder("Content/Textures", "Inner"));
        var intoItself = _ops.Move("Content/Textures", "Content/Textures/Inner");
        Assert.False(intoItself.Succeeded);
        Assert.Equal("Cannot move the folder 'Textures' into itself.", intoItself.Error);

        var notAFolder = _ops.Move(FileSystemProject.MainPath, FileSystemProject.PlayerPath);
        Assert.False(notAFolder.Succeeded);
        Assert.Equal("'Content/Scenes/Player.mscene' is not a folder in the project.", notAFolder.Error);

        var gone = _ops.Rename("Content/nothing.png", "x.png");
        Assert.Equal("'Content/nothing.png' no longer exists.", gone.Error);
        Assert.False(_ops.Delete(_project.Root).Succeeded);
        Assert.False(_ops.Move(FileSystemProject.MainPath, Path.GetTempPath()).Succeeded);
    }
}

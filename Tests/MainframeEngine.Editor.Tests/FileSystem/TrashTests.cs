namespace MainframeEngine.Editor.Tests.FileSystem;

public sealed class TrashTests : IDisposable
{
    private readonly string _temp = Directory.CreateTempSubdirectory("mf-trash").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void MacTrashMovesAFileToTheFinderTrash()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "The Finder trash exists on macOS only.");
        // Left in the user's Trash (clearly named); the test never deletes anything permanently.
        var file = Path.Combine(_temp, $"mf-trash-test-{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, "Mainframe editor trash test; safe to delete.");

        Assert.True(SystemTrash.Default.IsSupported);
        Assert.True(SystemTrash.Default.TryMoveToTrash(file, out var error), error);
        Assert.Null(error);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void SystemTrashReportsMissingFiles()
    {
        Assert.False(SystemTrash.Default.TryMoveToTrash(Path.Combine(_temp, "nope.txt"), out var error));
        Assert.Equal("'nope.txt' does not exist.", error);
    }

    [Fact]
    public void FreedesktopTrashMovesFilesAndWritesTrashInfo()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The freedesktop trash is for Unix systems.");
        var trash = new FreedesktopTrash(Path.Combine(_temp, "Trash"));
        Assert.True(trash.IsSupported);

        var folder = Directory.CreateDirectory(Path.Combine(_temp, "my stuff")).FullName;
        var file = Path.Combine(folder, "über file.txt");
        File.WriteAllText(file, "one");
        var before = DateTime.Now.AddSeconds(-2);

        Assert.True(trash.TryMoveToTrash(file, out var error), error);
        Assert.False(File.Exists(file));
        Assert.Equal("one", File.ReadAllText(Path.Combine(_temp, "Trash", "files", "über file.txt")));
        var info = File.ReadAllLines(Path.Combine(_temp, "Trash", "info", "über file.txt.trashinfo"));
        Assert.Equal("[Trash Info]", info[0]);
        Assert.Equal($"Path={FreedesktopTrash.EscapePath(file)}", info[1]);
        Assert.EndsWith("/my%20stuff/%C3%BCber%20file.txt", info[1], StringComparison.Ordinal);
        var deleted = DateTime.ParseExact(info[2]["DeletionDate=".Length..], "yyyy-MM-ddTHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(deleted, before, DateTime.Now.AddSeconds(2));

        // A second item with the same name gets a unique one; folders go too.
        File.WriteAllText(file, "two");
        Assert.True(trash.TryMoveToTrash(file, out error), error);
        Assert.Equal("two", File.ReadAllText(Path.Combine(_temp, "Trash", "files", "über file.2.txt")));
        Assert.True(File.Exists(Path.Combine(_temp, "Trash", "info", "über file.2.txt.trashinfo")));

        Assert.True(trash.TryMoveToTrash(folder, out error), error);
        Assert.False(Directory.Exists(folder));
        Assert.True(Directory.Exists(Path.Combine(_temp, "Trash", "files", "my stuff")));

        Assert.False(trash.TryMoveToTrash(file, out error));
        Assert.Equal("'über file.txt' does not exist.", error);
    }
}

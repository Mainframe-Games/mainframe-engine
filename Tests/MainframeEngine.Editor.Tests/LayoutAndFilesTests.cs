namespace MainframeEngine.Editor.Tests;

public sealed class EditorLayoutTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("mf-layout").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static bool Overlaps(LayoutRect a, LayoutRect b) =>
        a.X < b.Right - 0.01f && b.X < a.Right - 0.01f && a.Y < b.Bottom - 0.01f && b.Y < a.Bottom - 0.01f;

    [Theory]
    [InlineData(1600, 960)]
    [InlineData(1280, 720)]
    [InlineData(800, 600)]
    [InlineData(3840, 2160)]
    public void PanelsTileTheWindowWithoutOverlapping(float width, float height)
    {
        var layout = new EditorLayout();
        layout.Update(width, height);
        LayoutRect[] panels = [layout.MenuBar, layout.Toolbar, layout.SceneTree, layout.FileSystem, layout.Viewport, layout.Output, layout.Inspector];

        for (var i = 0; i < panels.Length; i++)
        {
            Assert.True(panels[i].Width > 0 && panels[i].Height > 0, $"panel {i} is empty");
            Assert.True(panels[i].Right <= width + 0.01f && panels[i].Bottom <= height + 0.01f, $"panel {i} leaves the window");
            for (var j = i + 1; j < panels.Length; j++)
                Assert.False(Overlaps(panels[i], panels[j]), $"panels {i} and {j} overlap");
        }

        Assert.Equal(width, layout.Inspector.Right, 2);
        Assert.Equal(height, layout.Output.Bottom, 2);
        Assert.True(layout.ViewportImage.Height > 0);
    }

    [Fact]
    public void SplittersMovePanelsAndClampToMinimumSizes()
    {
        var layout = new EditorLayout();
        layout.Update(1600, 900);

        Assert.True(layout.DragSplitter(Splitter.Left, 400, 0));
        Assert.Equal(400 - EditorLayout.SplitterSize / 2, layout.SceneTree.Width, 2);
        Assert.Equal(layout.SceneTree.Width, layout.Settings.LeftWidth, 2);

        layout.DragSplitter(Splitter.Left, 5, 0);
        Assert.Equal(EditorLayout.MinDock, layout.SceneTree.Width);
        layout.DragSplitter(Splitter.Right, 0, 0); // the inspector cannot squeeze the viewport away
        Assert.True(layout.Viewport.Width >= EditorLayout.MinViewportWidth - 0.01f);

        layout.DragSplitter(Splitter.Bottom, 0, 600);
        Assert.Equal(900 - 600 - EditorLayout.SplitterSize / 2, layout.Output.Height, 2);
        layout.DragSplitter(Splitter.LeftDock, 0, 300);
        Assert.InRange(layout.Settings.LeftDockSplit, 0.15f, 0.9f);
        Assert.False(layout.DragSplitter(Splitter.LeftDock, 0, 300)); // no change
    }

    [Fact]
    public void SettingsRoundTripThroughTheFileAtomically()
    {
        var path = Path.Combine(_directory, "nested", "editor_layout.json");
        var settings = new EditorLayoutSettings { LeftWidth = 333, RightWidth = 280, BottomHeight = 120, LeftDockSplit = 0.5f, WindowWidth = 1400, WindowHeight = 800, OutputFilter = 0b1010 };

        Assert.True(EditorLayout.Save(path, settings));

        Assert.Equal(settings, EditorLayout.Load(path));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    [Fact]
    public void MissingCorruptOrNewerFilesGiveDefaultsAndBadValuesAreSanitized()
    {
        Assert.Equal(new EditorLayoutSettings(), EditorLayout.Load(Path.Combine(_directory, "missing.json")));

        var corrupt = Path.Combine(_directory, "corrupt.json");
        File.WriteAllText(corrupt, "{ not json");
        Assert.Equal(new EditorLayoutSettings(), EditorLayout.Load(corrupt));

        var newer = Path.Combine(_directory, "newer.json");
        File.WriteAllText(newer, "{ \"Format\": 99, \"LeftWidth\": 500 }");
        Assert.Equal(new EditorLayoutSettings(), EditorLayout.Load(newer));

        var bad = Path.Combine(_directory, "bad.json");
        File.WriteAllText(bad, "{ \"LeftWidth\": -5, \"LeftDockSplit\": 7, \"WindowWidth\": 999999 }");
        var loaded = EditorLayout.Load(bad);
        Assert.Equal(270, loaded.LeftWidth);
        Assert.Equal(0.9f, loaded.LeftDockSplit);
        Assert.Equal(16384, loaded.WindowWidth);
    }

    [Fact]
    public void TheDefaultPathIsInTheUserProfile()
    {
        Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), EditorLayout.DefaultPath, StringComparison.Ordinal);
        Assert.EndsWith(Path.Combine(".mainframe", "editor_layout.json"), EditorLayout.DefaultPath, StringComparison.Ordinal);
    }
}

public sealed class FilePickerModelTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mf-picker").FullName;

    public FilePickerModelTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Scenes"));
        Directory.CreateDirectory(Path.Combine(_root, "assets"));
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        File.WriteAllText(Path.Combine(_root, "b.mscene"), "{}");
        File.WriteAllText(Path.Combine(_root, "A.MSCENE"), "{}");
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "x");
        File.WriteAllText(Path.Combine(_root, ".hidden.mscene"), "{}");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void ListsFoldersFirstThenMatchingFilesSortedAndSkipsHiddenEntries()
    {
        var model = new FilePickerModel(FilePickerMode.Open, _root, ["*.mscene"]);

        Assert.Equal(["assets", "Scenes", "A.MSCENE", "b.mscene"], model.Entries.Select(e => e.Name));
        Assert.True(model.Entries[0].IsDirectory);

        model.ShowHidden = true;
        model.Refresh();
        Assert.Contains(model.Entries, e => e.Name == ".hidden.mscene");

        model.Filter = [];
        Assert.Contains(model.Entries, e => e.Name == "notes.txt");
    }

    [Fact]
    public void NavigatesIntoFoldersAndUpAndRejectsMissingFolders()
    {
        var model = new FilePickerModel(FilePickerMode.Open, _root, ["*.mscene"]);
        Assert.False(model.Activate(model.Entries.First(e => e.Name == "Scenes")));
        Assert.Equal(Path.Combine(_root, "Scenes"), model.CurrentDirectory);
        Assert.True(model.Up());
        Assert.Equal(_root, model.CurrentDirectory);

        Assert.False(model.Navigate(Path.Combine(_root, "nope")));
        Assert.Equal(_root, model.CurrentDirectory);
        Assert.Contains("not found", model.Error, StringComparison.Ordinal);

        var fallback = new FilePickerModel(FilePickerMode.Open, Path.Combine(_root, "nope"));
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), fallback.CurrentDirectory);
    }

    [Fact]
    public void OpenNeedsAnExistingMatchingFile()
    {
        var model = new FilePickerModel(FilePickerMode.Open, _root, ["*.mscene"]);
        Assert.False(model.TryGetResult(out _, out _));
        Assert.Equal("Enter a file name.", model.Error);

        model.Select(model.Entries.First(e => e.Name == "b.mscene"));
        Assert.True(model.TryGetResult(out var path, out var exists));
        Assert.Equal(Path.Combine(_root, "b.mscene"), path);
        Assert.True(exists);

        model.FileName = "notes.txt";
        Assert.False(model.TryGetResult(out _, out _));
        model.FileName = "missing.mscene";
        Assert.False(model.TryGetResult(out _, out _));
        model.FileName = "Scenes";
        Assert.False(model.TryGetResult(out _, out _));
        Assert.Contains("folder", model.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void SaveAddsTheDefaultExtensionAndReportsExistingFiles()
    {
        var model = new FilePickerModel(FilePickerMode.Save, _root, ["*.mscene"], "Level1");
        Assert.Equal(".mscene", model.DefaultExtension);

        Assert.True(model.TryGetResult(out var path, out var exists));
        Assert.Equal(Path.Combine(_root, "Level1.mscene"), path);
        Assert.False(exists);

        model.FileName = "b";
        Assert.True(model.TryGetResult(out _, out exists));
        Assert.True(exists); // the dialog asks before replacing

        model.FileName = Path.Combine(_root, "nowhere", "x.mscene");
        Assert.False(model.TryGetResult(out _, out _));
    }

    [Fact]
    public void FolderModeListsOnlyFoldersAndReturnsTheChosenOne()
    {
        var model = new FilePickerModel(FilePickerMode.Folder, _root);
        Assert.All(model.Entries, e => Assert.True(e.IsDirectory));
        Assert.True(model.TryGetResult(out var current, out _));
        Assert.Equal(_root, current);
        model.Select(model.Entries.First(e => e.Name == "Scenes"));
        Assert.True(model.TryGetResult(out var chosen, out _));
        Assert.Equal(Path.Combine(_root, "Scenes"), chosen);
    }

    [Theory]
    [InlineData("level.mscene", "*.mscene", true)]
    [InlineData("LEVEL.MSCENE", "*.mscene", true)]
    [InlineData("level.mres", "*.mscene", false)]
    [InlineData("a1.png", "a?.png", true)]
    [InlineData("a12.png", "a?.png", false)]
    [InlineData("anything", "*", true)]
    public void GlobsMatchCaseInsensitively(string name, string pattern, bool expected) =>
        Assert.Equal(expected, FilePickerModel.Glob(name, pattern));
}

public sealed class OutputLogTests
{
    [Fact]
    public void MessagesFromAnyThreadArriveOnDrainBoundedByCapacity()
    {
        using var log = new OutputLog(capacity: 100);
        Assert.False(log.Drain());

        Parallel.For(0, 4, t =>
        {
            for (var i = 0; i < 50; i++)
                log.Add(OutputLevel.Info, $"t{t} m{i}");
        });

        Assert.True(log.Drain());
        Assert.Equal(100, log.Messages.Count);
        Assert.Equal(100, log.Dropped);
        log.Clear();
        Assert.Empty(log.Messages);
    }

    [Fact]
    public void EngineLogMessagesAreCollectedOnceAttached()
    {
        using var log = new OutputLog();
        log.Attach();
        Log.Warning("output-log-test-warning");
        log.Drain();
        var message = Assert.Single(log.Messages, m => m.Text == "output-log-test-warning");
        Assert.Equal(OutputLevel.Warning, message.Level);
        Assert.Equal("warn", message.LevelText);
        log.Dispose();
        Log.Warning("output-log-test-after");
        log.Drain();
        Assert.DoesNotContain(log.Messages, m => m.Text == "output-log-test-after");
    }

    [Theory]
    [InlineData(Log.Level.Debug, OutputLevel.Debug)]
    [InlineData(Log.Level.Info, OutputLevel.Info)]
    [InlineData(Log.Level.Warning, OutputLevel.Warning)]
    [InlineData(Log.Level.Error, OutputLevel.Error)]
    [InlineData(Log.Level.Fatal, OutputLevel.Error)]
    public void LevelsMapToTheFilterBuckets(Log.Level level, OutputLevel expected) => Assert.Equal(expected, OutputLog.ToOutputLevel(level));
}

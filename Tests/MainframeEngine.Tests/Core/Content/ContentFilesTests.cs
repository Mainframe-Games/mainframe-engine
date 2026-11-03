namespace MainframeEngine.Tests.Core.Content;

/// <summary>
/// <see cref="ContentFiles"/> (G19 phase 1): with nothing mounted it reads the disk like the <see cref="File"/> calls it
/// replaced; a mount serves the absolute paths under its root. Every mount here sits under its own temporary root, so
/// tests running in parallel never see each other's mounts.
/// </summary>
[Collection(nameof(SerialContentPaths))]
public sealed class ContentFilesTests
{
    private const string EngineShader = "Shaders/Sky/Sky.vk.vert.spv";

    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "mf-content-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void LooseSourceReadsAssetsPathsLikeContentPathsResolve()
    {
        var path = ContentPaths.Resolve(EngineShader);
        var expected = File.ReadAllBytes(path);

        Assert.Equal(expected, ContentFiles.ReadAllBytes(path));
        Assert.Equal(expected, ContentFiles.ReadAllBytes(ContentPaths.Resolve("Assets/" + EngineShader)));
        Assert.True(ContentFiles.Exists(path));
        Assert.Equal(expected.Length, ContentFiles.GetInfo(path).Length);
    }

    [Fact]
    public void LooseSourceFallsBackToTheBaseDirectory()
    {
        var project = Directory.CreateTempSubdirectory("mf-project").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(project, "Assets"));
            ContentPaths.ProjectDirectory = project;

            var path = ContentPaths.Resolve("Assets/" + EngineShader);
            Assert.True(ContentFiles.Exists(path));
            Assert.Equal(File.ReadAllBytes(Path.Combine(ContentPaths.Root, EngineShader)), ContentFiles.ReadAllBytes(path));
        }
        finally
        {
            ContentPaths.ProjectDirectory = null;
            Directory.Delete(project, recursive: true);
        }
    }

    [Fact]
    public void LegacyContentPathsReadTheAssetsFolder() =>
        Assert.Equal(File.ReadAllBytes(ContentPaths.Resolve(EngineShader)), ContentFiles.ReadAllBytes(ContentPaths.Resolve("Content/" + EngineShader)));

    [Fact]
    public void RelativePathsResolveAgainstTheWorkingDirectoryLikeFile()
    {
        // Tools pass repository-relative paths (mf-shaders check): a one-for-one swap must not reinterpret them.
        var relative = Path.GetRelativePath(Environment.CurrentDirectory, ContentPaths.Resolve(EngineShader));

        Assert.Equal(File.Exists(relative), ContentFiles.Exists(relative));
        Assert.True(ContentFiles.Exists(relative));
        Assert.Equal(File.ReadAllBytes(relative), ContentFiles.ReadAllBytes(relative));
        Assert.False(ContentFiles.Exists(EngineShader)); // not under the working directory: no content-path guessing
        Assert.False(ContentFiles.Exists(null));
        Assert.False(ContentFiles.Exists(""));
        Assert.False(ContentFiles.DirectoryExists(" "));
    }

    [Fact]
    public void PathsOutsideEveryMountReadTheDisk()
    {
        var root = NewRoot();
        using var _ = ContentFiles.Mount(new MemoryContentSource(root).Add("Assets/a.txt", "mounted"));
        var outside = Path.GetTempFileName();
        try
        {
            File.WriteAllText(outside, "disk");

            Assert.Equal("disk", ContentFiles.ReadAllText(outside));
            Assert.True(ContentFiles.Exists(outside));
            // A sibling folder whose name starts with the root's is not under it.
            Assert.False(ContentFiles.Exists(root + "x/Assets/a.txt"));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public void AbsolutePathsUnderAMountReadTheMount()
    {
        var root = NewRoot();
        using var _ = ContentFiles.Mount(new MemoryContentSource(root).Add("Assets/UI/hud.rml", "<rml/>"));
        var path = Path.Combine(root, "Assets", "UI", "hud.rml");

        Assert.False(File.Exists(path));
        Assert.True(ContentFiles.Exists(path));
        Assert.True(ContentFiles.DirectoryExists(Path.Combine(root, "Assets", "UI")));
        Assert.True(ContentFiles.DirectoryExists(root));
        Assert.Equal("<rml/>", ContentFiles.ReadAllText(path));
        Assert.Equal("<rml/>"u8.ToArray(), ContentFiles.ReadAllBytes(path));
        Assert.Equal(new ContentFileInfo(true, 6, MemoryContentSource.LastWriteUtc), ContentFiles.GetInfo(path));
        using var stream = ContentFiles.OpenRead(path);
        Assert.True(stream.CanSeek);
        stream.Position = 1;
        Assert.Equal((byte)'r', (byte)stream.ReadByte());
    }

    [Fact]
    public void HigherPriorityMountsWinAndTheDiskIsLast()
    {
        var root = NewRoot();
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "a.txt"), "disk");
            File.WriteAllText(Path.Combine(root, "b.txt"), "disk");
            using var low = ContentFiles.Mount(new MemoryContentSource(root, "low").Add("a.txt", "low").Add("c.txt", "low"), priority: 0);
            using var high = ContentFiles.Mount(new MemoryContentSource(root, "high").Add("a.txt", "high"), priority: 1);

            Assert.Equal("high", ContentFiles.ReadAllText(Path.Combine(root, "a.txt")));
            Assert.Equal("disk", ContentFiles.ReadAllText(Path.Combine(root, "b.txt")));
            Assert.Equal("low", ContentFiles.ReadAllText(Path.Combine(root, "c.txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void EnumerateUnionsMountsAndDisk()
    {
        var root = NewRoot();
        Directory.CreateDirectory(Path.Combine(root, "Assets", "Fonts"));
        try
        {
            File.WriteAllText(Path.Combine(root, "Assets", "Fonts", "disk.ttf"), "");
            File.WriteAllText(Path.Combine(root, "Assets", "Fonts", "both.ttf"), "");
            using var _ = ContentFiles.Mount(new MemoryContentSource(root)
                .Add("Assets/Fonts/both.ttf", "")
                .Add("Assets/Fonts/packed.ttf", "")
                .Add("Assets/Fonts/readme.txt", "")
                .Add("Assets/Fonts/Noto/noto.ttf", ""));
            var fonts = Path.Combine(root, "Assets", "Fonts");

            string[] ttf = ["both.ttf", "disk.ttf", "packed.ttf"];
            string[] noto = [Path.Combine(fonts, "Noto", "noto.ttf")];
            string[] folders = [Path.Combine(fonts, "Noto")];

            Assert.Equal(ttf, ContentFiles.EnumerateFiles(fonts, "*.ttf").Select(Path.GetFileName));
            Assert.Equal(noto, ContentFiles.EnumerateFiles(fonts, "noto*", recursive: true));
            Assert.Equal(folders, ContentFiles.EnumerateDirectories(fonts));
            Assert.Throws<DirectoryNotFoundException>(() => ContentFiles.EnumerateFiles(Path.Combine(root, "Missing")).ToList());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MissingContentNamesThePathAndTheMounts()
    {
        var root = NewRoot();
        using var _ = ContentFiles.Mount(new MemoryContentSource(root, "Assets.mfpak"));
        var path = Path.Combine(root, "Assets", "missing.png");

        var error = Assert.Throws<FileNotFoundException>(() => ContentFiles.OpenRead(path));

        Assert.Contains(path, error.Message);
        Assert.Contains("'Assets.mfpak'", error.Message);
        Assert.Equal(path, error.FileName);
        Assert.False(ContentFiles.Exists(path));
        Assert.False(ContentFiles.GetInfo(path).Exists);
    }

    [Fact]
    public void UnmountingRestoresDiskReads()
    {
        var root = NewRoot();
        var path = Path.Combine(root, "a.txt");
        var mount = ContentFiles.Mount(new MemoryContentSource(root).Add("a.txt", "mounted"));
        Assert.True(ContentFiles.Exists(path));

        mount.Dispose();

        Assert.False(ContentFiles.Exists(path));
    }

    [Fact]
    public void RelativeRootsAreRefused() =>
        Assert.Throws<ArgumentException>(() => ContentFiles.Mount(new RelativeRootSource()));

    private sealed class RelativeRootSource : IContentSource
    {
        public string Root => "Assets";
        public string Name => "relative";
        public bool FileExists(ReadOnlySpan<char> path) => false;
        public bool DirectoryExists(ReadOnlySpan<char> path) => false;
        public ContentFileInfo GetInfo(ReadOnlySpan<char> path) => default;
        public Stream? OpenRead(string path) => null;
        public IEnumerable<string> EnumerateFiles(string directory, bool recursive) => [];
        public IEnumerable<string> EnumerateDirectories(string directory, bool recursive) => [];
    }
}

/// <summary><see cref="ContentFiles.Exists"/> runs while drawing (the canvas renderer), so it must not allocate.</summary>
public sealed class ContentFilesAllocationTests
{
    [Fact]
    public void ExistsOnAbsolutePathsAllocatesNothing()
    {
        var root = Path.Combine(Path.GetTempPath(), "mf-content-" + Guid.NewGuid().ToString("N"));
        var mounted = Path.Combine(root, "Assets", "a.spv");
        var onDisk = ContentPaths.Resolve("Shaders/Sky/Sky.vk.vert.spv");
        var missing = Path.Combine(root, "Assets", "missing.spv");
        using var _ = ContentFiles.Mount(new MemoryContentSource(root).Add("Assets/a.spv", [1, 2, 3]));
        var found = 0;
        void Window()
        {
            for (var i = 0; i < 100; i++)
                found += (ContentFiles.Exists(mounted) ? 1 : 0) + (ContentFiles.Exists(onDisk) ? 1 : 0) + (ContentFiles.Exists(missing) ? 1 : 0);
        }

        Window(); // warm up

        Assert.Equal(0, AllocationGate.SmallestWindow(Window));
        Assert.True(found > 0);
    }
}

[CollectionDefinition(nameof(SerialContentPaths), DisableParallelization = true)]
public sealed class SerialContentPaths;

using System.Text;

namespace MainframeEngine.Tests.Core.Content;

/// <summary>The <c>.mfpak</c> v1 writer and reader (G19 phase 2, ADR 0247): determinism, round trips, layout and reads.</summary>
public sealed class PackTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("mf-pack-tests-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string Folder(string name) => Path.Combine(_directory, name);

    // A staged game: text, JSON, an empty file, incompressible binary, "media" and a project file.
    private string Stage(string name = "stage")
    {
        var root = Folder(name);
        Write(root, "project.mfproj", new ProjectSettings { Name = "Packed" }.ToJson());
        Write(root, "Assets/Scenes/main.mfscene", string.Concat(Enumerable.Repeat("""{ "type": "Node3D", "name": "Root" }""" + "\n", 200)));
        Write(root, "Assets/UI/hud.rml", "<rml><body>hud</body></rml>");
        Write(root, "Assets/UI/fonts/a.ttf", Random(40_000, seed: 1));
        Write(root, "Assets/UI/empty.txt", "");
        Write(root, "Assets/Audio/music.ogg", Random(300_000, seed: 2));
        Write(root, "Assets/Audio/sub/hit.wav", Random(5_000, seed: 3));
        Write(root, "Assets/Textures/checker.png", Random(20_000, seed: 4));
        return root;
    }

    private static void Write(string root, string path, string text) => Write(root, path, Encoding.UTF8.GetBytes(text));

    private static void Write(string root, string path, byte[] bytes)
    {
        var file = Path.Combine(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, bytes);
    }

    private static byte[] Random(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private string Pack(string root, string name = "Assets.mfpak")
    {
        var pack = Folder(name);
        PackWriter.Write(pack, PackWriter.GameFiles(root));
        return pack;
    }

    [Fact]
    public void PackWriterIsDeterministic()
    {
        var root = Stage();

        var first = File.ReadAllBytes(Pack(root, "a.mfpak"));
        File.SetLastWriteTimeUtc(Path.Combine(root, "Assets/UI/hud.rml"), DateTime.UtcNow.AddDays(-3)); // times never matter
        var second = File.ReadAllBytes(Pack(root, "b.mfpak"));

        Assert.Equal(first, second);
    }

    [Fact]
    public void PackRoundTripsAStagedFolder()
    {
        var root = Stage();
        var pack = Pack(root);
        var mountRoot = Folder("mounted"); // not on disk: every read must come from the pack
        using var source = new PackContentSource(PackReader.Open(pack), mountRoot);
        using var mount = ContentFiles.Mount(source);

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file);
            var mounted = Path.Combine(mountRoot, relative);
            Assert.True(ContentFiles.Exists(mounted), relative);
            Assert.Equal(File.ReadAllBytes(file), ContentFiles.ReadAllBytes(mounted));
            Assert.Equal(new FileInfo(file).Length, ContentFiles.GetInfo(mounted).Length);
        }

        Assert.False(ContentFiles.Exists(Path.Combine(mountRoot, "Assets/UI/missing.rml")));
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            Assert.True(ContentFiles.DirectoryExists(Path.Combine(mountRoot, Path.GetRelativePath(root, directory))));
        Assert.False(ContentFiles.DirectoryExists(Path.Combine(mountRoot, "Assets/Nope")));

        string[] Relative(IEnumerable<string> paths, string from) => [.. paths.Select(p => Path.GetRelativePath(from, p)).Order(StringComparer.Ordinal)];
        foreach (var (folder, pattern, recursive) in new[] { ("Assets", "*", true), ("Assets", "*", false), ("Assets/UI", "*.rml", false),
                     ("Assets/Audio", "*.wav", true), ("", "*.mfproj", false) })
        {
            var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var on = Path.Combine(mountRoot, folder);
            Assert.Equal(Relative(Directory.EnumerateFiles(Path.Combine(root, folder), pattern, option), Path.Combine(root, folder)),
                Relative(ContentFiles.EnumerateFiles(on, pattern, recursive), on));
            Assert.Equal(Relative(Directory.EnumerateDirectories(Path.Combine(root, folder), "*", option), Path.Combine(root, folder)),
                Relative(ContentFiles.EnumerateDirectories(on, "*", recursive), on));
        }
    }

    [Fact]
    public void TextIsCompressedAndMediaIsStored()
    {
        using var reader = PackReader.Open(Pack(Stage()));

        Assert.True(reader.TryGetEntry("Assets/Scenes/main.mfscene", out var scene));
        Assert.Equal(PackCodec.Brotli, scene.Codec);
        Assert.True(scene.StoredLength < scene.Length / 4);
        Assert.True(reader.TryGetEntry("Assets/UI/fonts/a.ttf", out var font));
        Assert.Equal(PackCodec.Stored, font.Codec); // random bytes: Brotli cannot shrink them
        Assert.True(reader.TryGetEntry("Assets/Textures/checker.png", out var png));
        Assert.Equal(PackCodec.Stored, png.Codec);
        Assert.Equal([.. reader.Entries.Select(e => e.Path).Order(StringComparer.Ordinal)], reader.Entries.Select(e => e.Path));
    }

    [Fact]
    public void StoredEntriesAreAlignedAndSeekable()
    {
        var root = Stage();
        var expected = File.ReadAllBytes(Path.Combine(root, "Assets/Audio/music.ogg"));
        using var reader = PackReader.Open(Pack(root));
        Assert.True(reader.TryGetEntry("Assets/Audio/music.ogg", out var ogg));
        Assert.Equal(PackCodec.Stored, ogg.Codec);
        Assert.Equal(0, ogg.Offset % PackFormat.StoredAlignment);

        using var stream = reader.OpenEntry(ogg);
        Assert.True(stream.CanSeek);
        Assert.Equal(expected.Length, stream.Length);
        var random = new Random(7);
        var buffer = new byte[4096];
        for (var i = 0; i < 50; i++)
        {
            var at = random.Next(expected.Length);
            stream.Seek(at, SeekOrigin.Begin);
            var read = stream.Read(buffer);
            Assert.Equal(Math.Min(buffer.Length, expected.Length - at), read);
            Assert.Equal(expected.AsSpan(at, read).ToArray(), buffer.AsSpan(0, read).ToArray());
        }

        stream.Seek(-10, SeekOrigin.End);
        Assert.Equal(10, stream.Read(buffer));
        Assert.Equal(0, stream.Read(buffer));
    }

    [Fact]
    public void AnchorsAndLargeEntriesStartOnAPage()
    {
        var root = Folder("pages");
        for (var i = 0; i < 12; i++)
            Write(root, $"Assets/Audio/{i:00}.ogg", Random(300_000, seed: i));
        Write(root, "Assets/Audio/big.ogg", Random(PackFormat.PageSize * 2 + 5, seed: 99));
        using var reader = PackReader.Open(Pack(root));

        Assert.True(PackFormat.IsAnchor("Assets/Audio/big.ogg", PackFormat.PageSize));
        foreach (var entry in reader.Entries)
        {
            if (PackFormat.IsAnchor(entry.Path, entry.StoredLength))
                Assert.Equal(0, entry.Offset % PackFormat.PageSize);
            else
                Assert.Equal(0, entry.Offset % PackFormat.StoredAlignment);
        }
    }

    [Fact]
    public void InsertingAnAssetShiftsEntriesAfterTheNextAnchorByWholePages()
    {
        // ~40 MB of 200 KB "media": a couple of anchors per 16 MB, so the insert's effect ends at one.
        var root = Folder("insert");
        for (var i = 0; i < 200; i++)
            Write(root, $"Assets/Audio/{i:000}.ogg", Random(200_000 + i * 97, seed: i));
        using var before = PackReader.Open(Pack(root, "before.mfpak"));
        const string inserted = "Assets/Audio/050b.ogg";
        Write(root, inserted, Random(123_456, seed: 500));
        using var after = PackReader.Open(Pack(root, "after.mfpak"));

        var anchorSeen = false;
        var wholePages = 0;
        foreach (var moved in after.Entries)
        {
            if (string.CompareOrdinal(moved.Path, inserted) <= 0)
            {
                if (moved.Path != inserted)
                    Assert.Equal(before.Entries.Single(e => e.Path == moved.Path).Offset, moved.Offset);
                continue;
            }

            anchorSeen |= PackFormat.IsAnchor(moved.Path, moved.StoredLength);
            if (!anchorSeen)
                continue; // re-laid out between the insert and the next anchor
            var old = before.Entries.Single(e => e.Path == moved.Path);
            Assert.Equal(0, (moved.Offset - old.Offset) % PackFormat.PageSize); // SteamPipe's ~1 MB chunks still match
            wholePages++;
        }

        Assert.True(wholePages > 50, $"only {wholePages} entries after the first anchor");
    }

    [Fact]
    public void ReadsOfStoredEntriesAllocateNothing()
    {
        var root = Stage();
        using var reader = PackReader.Open(Pack(root));
        Assert.True(reader.TryGetEntry("Assets/Audio/music.ogg", out var ogg));
        using var stream = reader.OpenEntry(ogg);
        var buffer = new byte[8192];
        stream.ReadExactly(buffer); // warm up

        var allocated = AllocationGate.SmallestWindow(() =>
        {
            for (var i = 0; i < 100; i++)
            {
                stream.Seek(i * 1_000, SeekOrigin.Begin);
                _ = stream.Read(buffer);
                _ = reader.FileExists("Assets/Audio/music.ogg");
            }
        });

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void ANonPackOrANewerFormatFailsAtOpen()
    {
        var garbage = Folder("garbage.mfpak");
        File.WriteAllBytes(garbage, Random(500, seed: 1));
        var notPack = Assert.Throws<InvalidDataException>(() => PackReader.Open(garbage));
        Assert.Contains("garbage.mfpak", notPack.Message, StringComparison.Ordinal);

        var newer = File.ReadAllBytes(Pack(Stage()));
        newer[4] = 2; // version 2
        File.WriteAllBytes(Folder("newer.mfpak"), newer);
        var tooNew = Assert.Throws<InvalidDataException>(() => PackReader.Open(Folder("newer.mfpak")));
        Assert.Contains("newer", tooNew.Message, StringComparison.Ordinal);

        var truncated = File.ReadAllBytes(Pack(Stage(), "full.mfpak"));
        File.WriteAllBytes(Folder("truncated.mfpak"), truncated[..(truncated.Length - 20)]);
        Assert.Throws<InvalidDataException>(() => PackReader.Open(Folder("truncated.mfpak")));
    }

    [Fact]
    public void GameFilesKeepLooseOnesOutOfThePack()
    {
        var root = Stage();

        var files = PackWriter.GameFiles(root, path => path.StartsWith("Assets/Audio/", StringComparison.Ordinal));

        Assert.Contains(files, f => f.Path == "project.mfproj");
        Assert.Contains(files, f => f.Path == "Assets/UI/hud.rml");
        Assert.DoesNotContain(files, f => f.Path.StartsWith("Assets/Audio/", StringComparison.Ordinal));
    }

    [Fact]
    public void MountBesideFindsThePackNextToTheApplication()
    {
        var root = Stage();
        var app = Folder("app");
        Directory.CreateDirectory(app);
        Assert.Null(PackContentSource.MountBeside(app));
        PackWriter.Write(Path.Combine(app, PackFormat.FileName), PackWriter.GameFiles(root));

        using (PackContentSource.MountBeside(app))
        {
            var settings = ProjectSettings.Load(app);
            Assert.Equal("Packed", settings.Name);
            Assert.True(ContentFiles.Exists(Path.Combine(app, "Assets/UI/hud.rml")));
        }

        Assert.False(ContentFiles.Exists(Path.Combine(app, "Assets/UI/hud.rml")));
    }
}

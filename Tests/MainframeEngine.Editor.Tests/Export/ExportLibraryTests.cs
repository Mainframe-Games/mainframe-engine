using System.Buffers.Binary;
using System.Formats.Tar;
using System.IO.Compression;

namespace MainframeEngine.Editor.Tests.Export;

/// <summary>The export library's pure parts: globs, the content filter, natives pruning, the exported project file, icons, archives, symbols, the plan.</summary>
public sealed class ExportLibraryTests : IDisposable
{
    private readonly string _dir = GameProjectLayout.RealPath(Directory.CreateTempSubdirectory("mf-export-lib").FullName);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string File_(string relative, string text = "x")
    {
        var path = Path.Combine(_dir, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    // ── Glob ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Assets/**", "Assets/a.png", true)]
    [InlineData("Assets/**", "Assets/x/y/z.png", true)]
    [InlineData("Assets/**", "Other/a.png", false)]
    [InlineData("Assets/*.png", "Assets/a.png", true)]
    [InlineData("Assets/*.png", "Assets/x/a.png", false)]
    [InlineData("Assets/**/*.po", "Assets/locale/fr/LC_MESSAGES/messages.po", true)]
    [InlineData("Assets/**/*.po", "Assets/x.po", true)]
    [InlineData("Assets/**/*.po", "Assets/x.pot", false)]
    [InlineData("Assets/a?.png", "Assets/ab.png", true)]
    [InlineData("Assets/a?.png", "Assets/a/.png", false)]
    [InlineData("Assets/A.png", "Assets/a.png", false)]
    [InlineData("Assets/**/b/*", "Assets/b/c", true)]
    [InlineData("Assets/**/b/*", "Assets/x/y/b/c", true)]
    [InlineData("**", "anything/at/all", true)]
    [InlineData("Assets/*", "Assets", false)]
    public void GlobsMatchProjectPaths(string pattern, string path, bool expected) => Assert.Equal(expected, Glob.IsMatch(pattern, path));

    [Theory]
    [InlineData("", "is empty")]
    [InlineData("/Assets/**", "must be project-relative")]
    [InlineData("Assets\\**", "must use '/'")]
    [InlineData("Assets/../x", "must not contain '..'")]
    [InlineData("Assets/**", null)]
    public void BadGlobsAreReported(string pattern, string? problem)
    {
        var found = Glob.Problem(pattern);
        if (problem is null)
            Assert.Null(found);
        else
            Assert.Contains(problem, found, StringComparison.Ordinal);
    }

    // ── Content filter ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheFilterRemovesOnlyGameFilesThatDoNotShip()
    {
        var project = Path.Combine(_dir, "project");
        var stage = Path.Combine(_dir, "stage");
        foreach (var f in (string[])["Assets/Scenes/Main.mscene", "Assets/Audio/a.ogg", "Assets/Audio/a.ogg.meta", "Assets/locale/fr/messages.po"])
        {
            File_("project/" + f);
            File_("stage/" + f);
        }

        File_("stage/Assets/Shaders/Mesh.spv"); // engine content: not in the project
        File_("stage/Assets/locale/fr/messages.mo"); // compiled by the build

        var removed = ContentFilter.Apply(stage, project, ExportPreset.DefaultInclude, ["Assets/**/*.po", "Assets/Audio/**"]);

        Assert.Equal(["Assets/Audio/a.ogg", "Assets/Audio/a.ogg.meta", "Assets/locale/fr/messages.po"], removed);
        Assert.True(File.Exists(Path.Combine(stage, "Assets", "Shaders", "Mesh.spv")));
        Assert.True(File.Exists(Path.Combine(stage, "Assets", "locale", "fr", "messages.mo")));
        Assert.True(File.Exists(Path.Combine(stage, "Assets", "Scenes", "Main.mscene")));
        Assert.False(Directory.Exists(Path.Combine(stage, "Assets", "Audio"))); // emptied folders go too

        // A narrow include keeps engine content.
        var narrow = ContentFilter.Apply(stage, project, ["Assets/Audio/**"], []);
        Assert.Equal(["Assets/Scenes/Main.mscene"], narrow);
        Assert.True(File.Exists(Path.Combine(stage, "Assets", "Shaders", "Mesh.spv")));
    }

    // ── Natives ──────────────────────────────────────────────────────────────────────────────────────────────────

    private static readonly Dictionary<string, IReadOnlyList<string>> Natives = new()
    {
        ["win-x64"] = ["enet.dll", "mfrmlui.dll", "steam_api64.dll", "newlib.dll"],
        ["linux-x64"] = ["libenet.so", "libmfrmlui.so", "libsteam_api.so", "libnewlib.so"],
        ["osx-arm64"] = ["libenet.dylib", "libmfrmlui.dylib", "libsteam_api.dylib", "libnewlib.dylib"],
        ["osx-x64"] = ["libenet.dylib", "libmfrmlui.dylib", "libsteam_api.dylib", "libnewlib.dylib"],
    };

    private string FakePublish()
    {
        var publish = Path.Combine(_dir, "publish");
        foreach (var files in Natives.Values)
            foreach (var f in files)
                File_("publish/" + f);
        File_("publish/steamworks/win64/MainframeEngine.Steamworks.dll");
        File_("publish/steamworks/posix/MainframeEngine.Steamworks.dll");
        File_("publish/libSDL2-2.0.so"); // a NuGet native of this RID: never pruned
        return publish;
    }

    [Theory]
    [InlineData("linux-x64", "libnewlib.so", "posix")]
    [InlineData("win-x64", "newlib.dll", "win64")]
    [InlineData("osx-x64", "libnewlib.dylib", "posix")]
    public void PruningKeepsOnlyTheRidsNativesAndSteamworks(string rid, string kept, string variant)
    {
        var publish = FakePublish();
        NativesPruner.Prune(publish, rid, Natives);

        var left = Directory.EnumerateFiles(publish, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(publish, f).Replace('\\', '/')).Order().ToArray();
        Assert.Contains(kept, left);
        Assert.Contains("libSDL2-2.0.so", left);
        Assert.Contains(NativesPruner.SteamApiNative(rid), left);
        Assert.Contains($"steamworks/{variant}/MainframeEngine.Steamworks.dll", left);
        Assert.DoesNotContain(left, f => f.StartsWith("steamworks/", StringComparison.Ordinal) && !f.StartsWith($"steamworks/{variant}/", StringComparison.Ordinal));
        Assert.Equal(Natives[rid].Count + 2, left.Length);
    }

    [Fact]
    public void TheEngineNativesTableComesFromTheCheckout()
    {
        Assert.Equal(["linux-x64", "osx-arm64", "osx-x64", "win-x64"], EngineNatives.Rids);
        Assert.Contains("mfrmlui.dll", EngineNatives.FilesOf("win-x64"));
        Assert.Contains("libsteam_api.dylib", EngineNatives.FilesOf("osx-arm64"));
        Assert.Equal(["win-x64"], ExportTargets.RidsOf(ExportPlatform.Windows));
        Assert.Equal(["osx-arm64", "osx-x64"], ExportTargets.RidsOf(ExportPlatform.MacOS));
    }

    // ── Exported project file ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheExportedProjectFileHasNoPresetsAndTheBuildsFlags()
    {
        var settings = new ProjectSettings { Name = "G", IsDemo = false, Version = "1.0.0" };
        settings.Steam.AppId = 480;
        settings.Steam.DemoAppId = 481;
        settings.Steam.RestartThroughSteam = true;
        var steamOff = ExportPreset.Create("Off", ExportPlatform.Linux);
        steamOff.Demo = true;
        settings.ExportPresets.Add(steamOff);

        var off = ExportedProjectFile.Transform(settings, steamOff, "2.0.0");
        Assert.Empty(off.ExportPresets);
        Assert.True(off.IsDemo);
        Assert.Equal("2.0.0", off.Version);
        Assert.Equal((0u, 0u, false), (off.Steam.AppId, off.Steam.DemoAppId, off.Steam.RestartThroughSteam));
        Assert.Single(settings.ExportPresets); // the input is untouched
        Assert.Equal(480u, settings.Steam.AppId);

        var steamOn = ExportPreset.Create("On", ExportPlatform.Windows);
        steamOn.Desktop.Steam = true;
        var on = ExportedProjectFile.Transform(settings, steamOn, "1.0.0");
        Assert.False(on.IsDemo);
        Assert.Equal((480u, 481u, true), (on.Steam.AppId, on.Steam.DemoAppId, on.Steam.RestartThroughSteam));
    }

    // ── References ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheScannerFindsUidAndPathReferences()
    {
        File_("Assets/a.mres", """{ "uid": "res_000000000001" }""");
        var scene = File_("Assets/s.mscene", """
            { "uid": "scn_000000000002", "root": { "props": {
                "a": { "ref": "res_000000000001", "path": "Assets/old.mres" },
                "b": { "instance": "scn_000000000099", "path": "Assets/Sub.mscene" },
                "c": "Assets/Textures/t.png", "d": "not a path" } } }
            """);
        var database = new AssetDatabase(_dir);
        database.Scan();

        var refs = ReferenceScanner.ReferencesOf(scene, database);
        Assert.Equal(["Assets/Sub.mscene", "Assets/Textures/t.png", "Assets/a.mres"], refs.Order(StringComparer.Ordinal));
        Assert.Empty(ReferenceScanner.ReferencesOf(File_("Assets/bad.mscene", "{ nope"), database));
    }

    // ── Icons ────────────────────────────────────────────────────────────────────────────────────────────────────

    private static byte[] Solid(int size)
    {
        var rgba = new byte[size * size * 4];
        for (var i = 0; i < rgba.Length; i += 4)
            (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = (10, 20, 30, 255);
        return rgba;
    }

    [Fact]
    public void TheIcoHasPngEntriesUpToTheSourceSize()
    {
        var ico = IconWriter.Ico(256, Solid(256));

        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(ico));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(2)));
        var count = BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(4));
        Assert.Equal(6, count);
        var sizes = new List<int>();
        for (var i = 0; i < count; i++)
        {
            var entry = ico.AsSpan(6 + i * 16, 16);
            var declared = entry[0] == 0 ? 256 : entry[0];
            var length = BinaryPrimitives.ReadInt32LittleEndian(entry[8..]);
            var offset = BinaryPrimitives.ReadInt32LittleEndian(entry[12..]);
            var png = Png.ReadRgba8(new MemoryStream(ico, offset, length));
            Assert.Equal((declared, declared), (png.Width, png.Height));
            Assert.Equal((10, 255), (png.Pixels[0], png.Pixels[3]));
            sizes.Add(declared);
        }

        Assert.Equal([16, 32, 48, 64, 128, 256], sizes);
        Assert.Equal([16, 32, 48], ReadIcoSizes(IconWriter.Ico(48, Solid(48)))); // never enlarged
    }

    private static List<int> ReadIcoSizes(byte[] ico)
    {
        var sizes = new List<int>();
        for (var i = 0; i < BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(4)); i++)
            sizes.Add(ico[6 + i * 16] == 0 ? 256 : ico[6 + i * 16]);
        return sizes;
    }

    [Fact]
    public void TheIcnsHasPngChunks()
    {
        var icns = IconWriter.Icns(1024, Solid(1024));

        Assert.Equal("icns"u8.ToArray(), icns[..4]);
        Assert.Equal(icns.Length, BinaryPrimitives.ReadInt32BigEndian(icns.AsSpan(4)));
        var chunks = new List<(string, int)>();
        for (var at = 8; at < icns.Length;)
        {
            var type = System.Text.Encoding.ASCII.GetString(icns, at, 4);
            var length = BinaryPrimitives.ReadInt32BigEndian(icns.AsSpan(at + 4));
            var png = Png.ReadRgba8(new MemoryStream(icns, at + 8, length - 8));
            Assert.Equal(png.Width, png.Height);
            chunks.Add((type, png.Width));
            at += length;
        }

        Assert.Equal([("icp4", 16), ("icp5", 32), ("icp6", 64), ("ic07", 128), ("ic08", 256), ("ic09", 512), ("ic10", 1024)], chunks);
    }

    [Fact]
    public void NonSquareIconsAreCentredOnATransparentSquare()
    {
        var (size, rgba) = IconWriter.Square(4, 2, Solid(4)[..(4 * 2 * 4)]);
        Assert.Equal(4, size);
        Assert.Equal(0, rgba[3]); // top row: transparent
        Assert.Equal(255, rgba[(1 * 4 + 0) * 4 + 3]); // second row: the image
        Assert.Throws<InvalidDataException>(() => IconWriter.LoadSquare(File_("not.png", "text")));
    }

    // ── Archives ─────────────────────────────────────────────────────────────────────────────────────────────────

    private string Folder()
    {
        File_("in/Game", "elf");
        File_("in/Game.dll", "dll");
        File_("in/Assets/a.txt", "a");
        return Path.Combine(_dir, "in");
    }

    [Fact]
    public void TarArchivesMarkTheExecutableWhateverTheHost()
    {
        var archive = Path.Combine(_dir, "out.tar.gz");
        Archiver.TarGz(Folder(), "Game-1.0.0-linux-x64", archive, new HashSet<string> { "Game" }, cancellationToken: TestContext.Current.CancellationToken);

        using var gzip = new GZipStream(File.OpenRead(archive), CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        var entries = new Dictionary<string, UnixFileMode>();
        while (tar.GetNextEntry() is { } entry)
            entries[entry.Name] = entry.Mode;
        Assert.Equal(Archiver.ExecutableMode, entries["Game-1.0.0-linux-x64/Game"]);
        Assert.Equal(Archiver.FileMode, entries["Game-1.0.0-linux-x64/Game.dll"]);
        Assert.Equal(Archiver.ExecutableMode, entries["Game-1.0.0-linux-x64/Assets/"]);
        Assert.Contains("Game-1.0.0-linux-x64/Assets/a.txt", entries.Keys);
        Assert.False(File.Exists(archive + Archiver.PartialSuffix));
    }

    [Fact]
    public void ZipArchivesCarryUnixModesUnderTheirRootFolder()
    {
        var archive = Path.Combine(_dir, "out.zip");
        Archiver.Zip(Folder(), "Game-1.0.0-win-x64", archive, new HashSet<string> { "Game" }, cancellationToken: TestContext.Current.CancellationToken);

        using var zip = ZipFile.OpenRead(archive);
        Assert.Equal(["Game-1.0.0-win-x64/Assets/a.txt", "Game-1.0.0-win-x64/Game", "Game-1.0.0-win-x64/Game.dll"], zip.Entries.Select(e => e.FullName).Order());
        Assert.Equal((int)Archiver.ExecutableMode, (zip.GetEntry("Game-1.0.0-win-x64/Game")!.ExternalAttributes >> 16) & 0x1FF);
    }

    [Fact]
    public void ACancelledArchiveLeavesThePreviousOne()
    {
        var archive = Path.Combine(_dir, "out.zip");
        File.WriteAllText(archive, "previous");
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => Archiver.Zip(Folder(), "G", archive, new HashSet<string>(), cancellationToken: cancel.Token));
        Assert.Equal("previous", File.ReadAllText(archive));
        Assert.False(File.Exists(archive + Archiver.PartialSuffix));
    }

    [Fact]
    public void MovingAFolderReplacesTheOldOne()
    {
        var target = Path.Combine(_dir, "out", "Game-1.0.0");
        File_("out/Game-1.0.0/stale.txt");
        Archiver.MoveFolder(Folder(), target);

        Assert.True(File.Exists(Path.Combine(target, "Game")));
        Assert.False(File.Exists(Path.Combine(target, "stale.txt")));
        Assert.False(Directory.Exists(target + Archiver.PartialSuffix));
    }

    // ── Symbols and signing ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SymbolsAreCollectedWithTheirAssemblies()
    {
        File_("p/Game.dll");
        File_("p/Game.pdb");
        File_("p/Orphan.pdb");
        File_("p/steamworks/posix/Shim.dll");
        File_("p/steamworks/posix/Shim.pdb");
        var count = SymbolCollector.Collect(Path.Combine(_dir, "p"), Path.Combine(_dir, "s"), move: true);

        Assert.Equal(2, count);
        Assert.True(File.Exists(Path.Combine(_dir, "s", "Game.pdb")));
        Assert.True(File.Exists(Path.Combine(_dir, "s", "Game.dll")));
        Assert.True(File.Exists(Path.Combine(_dir, "s", "steamworks", "posix", "Shim.dll")));
        Assert.False(File.Exists(Path.Combine(_dir, "p", "Game.pdb")));
        Assert.True(File.Exists(Path.Combine(_dir, "p", "Game.dll")));
        Assert.True(File.Exists(Path.Combine(_dir, "p", "Orphan.pdb")));
    }

    [Fact]
    public void SigningIsInsideOutAndOnlyMachO()
    {
        var app = Path.Combine(_dir, "G.app");
        File.WriteAllBytes(File_("G.app/Contents/MacOS/G"), [0xCF, 0xFA, 0xED, 0xFE, 0]);
        File.WriteAllBytes(File_("G.app/Contents/MonoBundle/libcoreclr.dylib"), [0xCF, 0xFA, 0xED, 0xFE, 0]);
        File.WriteAllBytes(File_("G.app/Contents/MonoBundle/steamworks/posix/lib.dylib"), [0xCA, 0xFE, 0xBA, 0xBE, 0]);
        File_("G.app/Contents/MonoBundle/G.dll", "MZ managed");

        var nested = MacSigner.NestedCode(app, "G").Select(f => Path.GetRelativePath(app, f).Replace('\\', '/')).ToArray();
        Assert.Equal(["Contents/MonoBundle/steamworks/posix/lib.dylib", "Contents/MonoBundle/libcoreclr.dylib"], nested);
        Assert.Equal(["--force", "--sign", "-", "x"], MacSigner.SignArguments("-", "x", null));
        Assert.Equal(["--force", "--sign", "Dev", "--timestamp", "--options", "runtime", "--entitlements", "e", "x"], MacSigner.SignArguments("Dev", "x", "e"));
    }

    [Fact]
    public void TheInfoPlistEscapesAndListsTheIconOnlyWhenThereIsOne()
    {
        var plist = AppBundleWriter.InfoPlist(new AppBundleInfo("Tom & Jerry", "TomJerry", "com.x", "1.0.0", "© <2026>", HasIcon: false));
        Assert.Contains("<key>CFBundleName</key><string>Tom &amp; Jerry</string>", plist, StringComparison.Ordinal);
        Assert.Contains("&lt;2026&gt;", plist, StringComparison.Ordinal);
        Assert.DoesNotContain("CFBundleIconFile", plist, StringComparison.Ordinal);
        Assert.Contains("<key>LSMinimumSystemVersion</key><string>11.0</string>", plist, StringComparison.Ordinal);
        Assert.Contains("CFBundleIconFile", AppBundleWriter.InfoPlist(new AppBundleInfo("G", "G", "c", "1", "", HasIcon: true)), StringComparison.Ordinal);
    }

    // ── Names ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Windows (Steam demo)", "windows-steam-demo")]
    [InlineData("  macOS  ", "macos")]
    [InlineData("Linux server (headless)", "linux-server-headless")]
    [InlineData("???", "preset")]
    public void SlugsAreFolderSafe(string name, string slug) => Assert.Equal(slug, ExportTargets.Slug(name));

    [Theory]
    [InlineData("1.2.3", true)]
    [InlineData("1.2.3-beta.1", true)]
    [InlineData("1.2", false)]
    [InlineData("1.2.3-", false)]
    [InlineData("v1.2.3", false)]
    public void VersionsAreChecked(string version, bool ok) => Assert.Equal(ok, ExportTargets.IsSemanticVersion(version));
}

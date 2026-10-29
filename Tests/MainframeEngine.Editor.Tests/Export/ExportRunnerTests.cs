using System.Formats.Tar;
using System.IO.Compression;

namespace MainframeEngine.Editor.Tests.Export;

/// <summary><see cref="ExportRunner"/> end to end over a fake <c>dotnet publish</c> (no SDK): layouts, project file, index, cancel, failures.</summary>
public sealed class ExportRunnerTests : IDisposable
{
    private readonly ExportFixture _game = new();

    public void Dispose() => _game.Dispose();

    private Task<ExportResult> Run(ExportPreset preset, ExportHost host = ExportHost.Linux, CancellationToken ct = default) =>
        new ExportRunner(_game.Environment(host)).RunAsync(new ExportRequest(_game.Root, preset), null, null, ct);

    private static Dictionary<string, UnixFileMode> TarEntries(string archive)
    {
        using var gzip = new GZipStream(File.OpenRead(archive), CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        var entries = new Dictionary<string, UnixFileMode>(StringComparer.Ordinal);
        while (tar.GetNextEntry() is { } entry)
            entries[entry.Name] = entry.Mode;
        return entries;
    }

    private static string TarText(string archive, string name)
    {
        using var gzip = new GZipStream(File.OpenRead(archive), CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        while (tar.GetNextEntry() is { } entry)
            if (entry.Name == name)
                return new StreamReader(entry.DataStream!).ReadToEnd();
        throw new FileNotFoundException(name);
    }

    [Fact]
    public async Task ALinuxExportIsATarOfTheStagedPublish()
    {
        _game.Settings.Steam.AppId = 480;
        _game.Settings.ExportPresets.Add(ExportPreset.Create("Linux", ExportPlatform.Linux));
        _game.Save();
        var progress = new List<ExportPhase>();
        var lines = new List<string>();
        var result = await new ExportRunner(_game.Environment()).RunAsync(new ExportRequest(_game.Root, _game.Settings.ExportPresets[0]),
            new SyncProgress(p => progress.Add(p.Phase)), lines.Add, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Summary);
        Assert.Equal(Path.Combine(_game.Root, "export", "linux", "TinyGame-1.2.0-linux-x64.tar.gz"), result.ArtifactPath);
        var entries = TarEntries(result.ArtifactPath!);
        const string root = "TinyGame-1.2.0-linux-x64/";
        Assert.Equal(Archiver.ExecutableMode, entries[root + "TinyGame"]);
        Assert.DoesNotContain(root + "TinyGame.Desktop", entries.Keys);
        Assert.Contains(root + "libmfrmlui.so", entries.Keys);
        Assert.Contains(root + "libsteam_api.so", entries.Keys);
        Assert.Contains(root + "steamworks/posix/MainframeEngine.Steamworks.dll", entries.Keys);
        Assert.DoesNotContain(entries.Keys, e => e.EndsWith(".dll", StringComparison.Ordinal) && e.Contains("mfrmlui", StringComparison.Ordinal));
        Assert.DoesNotContain(entries.Keys, e => e.Contains("steamworks/win64", StringComparison.Ordinal) || e.EndsWith(".dylib", StringComparison.Ordinal));
        Assert.DoesNotContain(entries.Keys, e => e.EndsWith(".po", StringComparison.Ordinal));
        Assert.Contains(root + "Assets/locale/fr/LC_MESSAGES/messages.mo", entries.Keys);
        Assert.Contains(root + "Assets/Shaders/Mesh.vk.vert.spv", entries.Keys);

        // The project file: no presets, Steam off zeroes the ids; the asset index lists the game's UIDs.
        var project = ProjectSettings.Parse(System.Text.Encoding.UTF8.GetBytes(TarText(result.ArtifactPath!, root + "project.mfproj")));
        Assert.Empty(project.ExportPresets);
        Assert.Equal(0u, project.Steam.AppId);
        Assert.Equal("1.2.0", project.Version);
        var index = TarText(result.ArtifactPath!, root + "Assets/assets.index.json");
        Assert.Contains("scn_aaaaaaaaaaaa", index, StringComparison.Ordinal);
        Assert.Contains("Assets/Scenes/Main.mscene", index, StringComparison.Ordinal);

        Assert.Equal([ExportPhase.Validate, ExportPhase.Publish, ExportPhase.Stage], progress.Distinct().Take(3));
        Assert.Contains(ExportPhase.Archive, progress);
        Assert.Contains(lines, l => l.StartsWith("dotnet publish ", StringComparison.Ordinal));
        Assert.False(Directory.Exists(Path.Combine(_game.Root, ".mainframe", "export", "stage", "linux")));
    }

    [Fact]
    public async Task AWindowsExportIsAZipWithTheIconAndOnlyWindowsFiles()
    {
        var result = await Run(ExportPreset.Create("Windows", ExportPlatform.Windows), ct: TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Summary);
        using var zip = ZipFile.OpenRead(result.ArtifactPath!);
        var names = zip.Entries.Select(e => e.FullName).ToArray();
        const string root = "TinyGame-1.2.0-win-x64/";
        Assert.Contains(root + "TinyGame.exe", names);
        Assert.Contains(root + "mfrmlui.dll", names);
        Assert.Contains(root + "steam_api64.dll", names);
        Assert.DoesNotContain(names, n => n.EndsWith(".so", StringComparison.Ordinal) || n.Contains("steamworks/posix", StringComparison.Ordinal));
        var publish = _game.Calls.Single(c => c.File == "/fake/dotnet").Arguments;
        Assert.Contains(publish, a => a.StartsWith("-p:ApplicationIcon=", StringComparison.Ordinal) && a.EndsWith("icon.ico", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFolderExportWithSeparateSymbolsReplacesTheLastOne()
    {
        var preset = ExportPreset.Create("Server", ExportPlatform.Linux);
        preset.Archive = false;
        preset.Desktop.Symbols = ExportSymbols.Separate;
        var stale = Path.Combine(_game.Root, "export", "server", "TinyGame-1.2.0-linux-x64", "stale.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(stale)!);
        File.WriteAllText(stale, "old");

        var result = await Run(preset, ct: TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Summary);
        Assert.True(File.Exists(Path.Combine(result.ArtifactPath!, "TinyGame")));
        Assert.False(File.Exists(stale));
        Assert.False(File.Exists(Path.Combine(result.ArtifactPath!, "TinyGame.pdb")));
        Assert.Equal(Path.Combine(_game.Root, "export", "server", "TinyGame-1.2.0-linux-x64-symbols.zip"), result.SymbolsPath);
        using var symbols = ZipFile.OpenRead(result.SymbolsPath!);
        Assert.Equal(["TinyGame.Desktop.dll", "TinyGame.Desktop.pdb", "TinyGame.dll", "TinyGame.pdb"], symbols.Entries.Select(e => e.FullName).Order());
    }

    [Fact]
    public async Task AMacOsExportIsASignedBundleZippedWithDitto()
    {
        var preset = ExportPreset.Create("macOS", ExportPlatform.MacOS);
        preset.Desktop.MacOs!.Copyright = "© Me";
        var result = await Run(preset, ExportHost.MacOS, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Summary);
        Assert.EndsWith("TinyGame-1.2.0-osx-arm64.zip", result.ArtifactPath, StringComparison.Ordinal);
        using var zip = ZipFile.OpenRead(result.ArtifactPath!);
        var names = zip.Entries.Select(e => e.FullName.Replace('\\', '/')).ToArray();
        Assert.Contains("Tiny Game.app/Contents/MacOS/TinyGame", names);
        Assert.Contains("Tiny Game.app/Contents/MonoBundle/TinyGame.Desktop.dll", names);
        Assert.Contains("Tiny Game.app/Contents/MonoBundle/libmfrmlui.dylib", names);
        Assert.Contains("Tiny Game.app/Contents/Resources/icon.icns", names);
        Assert.DoesNotContain(names, n => n.StartsWith("Tiny Game.app/Contents/MacOS/", StringComparison.Ordinal) && !n.EndsWith("/TinyGame", StringComparison.Ordinal));
        Assert.DoesNotContain(names, n => n.EndsWith(".so", StringComparison.Ordinal) || n.EndsWith(".dll", StringComparison.Ordinal) && n.Contains("mfrmlui", StringComparison.Ordinal));
        using var plist = new StreamReader(zip.GetEntry("Tiny Game.app/Contents/Info.plist")!.Open());
        var text = await plist.ReadToEndAsync(TestContext.Current.CancellationToken);
        Assert.Contains("<string>com.mainframegames.tinygame</string>", text, StringComparison.Ordinal);
        Assert.Contains("<string>© Me</string>", text, StringComparison.Ordinal);

        // Ad hoc: the bundle signed after its nested code, then verified; no notarization.
        var tools = _game.Calls.Where(c => c.File != "/fake/dotnet").Select(c => c.File + " " + c.Arguments[0]).ToArray();
        Assert.Equal(["codesign --verify", "ditto -c"], tools.Where(t => t.StartsWith("codesign --verify", StringComparison.Ordinal) || t.StartsWith("ditto", StringComparison.Ordinal)));
        var bundleSign = _game.Calls.Last(c => c.File == "codesign" && c.Arguments[0] == "--force");
        Assert.EndsWith("Tiny Game.app", bundleSign.Arguments[^1], StringComparison.Ordinal);
        Assert.DoesNotContain(_game.Calls, c => c.File == "xcrun");
    }

    [Fact]
    public async Task NotarizationSubmitsStaplesAndRezips()
    {
        var preset = ExportPreset.Create("macOS", ExportPlatform.MacOS);
        preset.Desktop.MacOs!.Sign = "Developer ID Application: Me";
        preset.Desktop.MacOs.NotaryProfile = "me-notary";
        var result = await Run(preset, ExportHost.MacOS, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Summary);
        var xcrun = _game.Calls.Where(c => c.File == "xcrun").Select(c => c.Arguments[0]).ToArray();
        Assert.Equal(["notarytool", "stapler"], xcrun);
        Assert.Equal(2, _game.Calls.Count(c => c.File == "ditto"));
        Assert.Contains(_game.Calls, c => c.File == "codesign" && c.Arguments.Contains("--entitlements") && c.Arguments.Contains("runtime"));
    }

    [Fact]
    public async Task InvalidPresetsDoNotRun()
    {
        var preset = ExportPreset.Create("Steam", ExportPlatform.Windows);
        preset.Desktop.Steam = true; // no app id

        var result = await Run(preset, ct: TestContext.Current.CancellationToken);

        Assert.True(result.Invalid);
        Assert.Null(result.ArtifactPath);
        Assert.Empty(_game.Calls);
        Assert.Contains("steam-app-id", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APublishMissingSteamFilesFails()
    {
        _game.Settings.Steam.AppId = 480;
        _game.Save();
        _game.OmitSteam = true;
        var preset = ExportPreset.Create("Steam", ExportPlatform.Linux);
        preset.Desktop.Steam = true;

        var result = await Run(preset, ct: TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains("libsteam_api.so", result.Error, StringComparison.Ordinal);
        Assert.Contains("NativeLibraryMissing", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildErrorsAreDiagnosticsAndThePreviousArtifactStays()
    {
        var artifact = Path.Combine(_game.Root, "export", "linux", "TinyGame-1.2.0-linux-x64.tar.gz");
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
        File.WriteAllText(artifact, "previous");
        _game.FailPublish = true;

        var result = await Run(ExportPreset.Create("Linux", ExportPlatform.Linux), ct: TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        var error = Assert.Single(result.Diagnostics);
        Assert.Equal(("CS1002", 3), (error.Code, error.Line));
        Assert.Equal("previous", File.ReadAllText(artifact));
    }

    [Fact]
    public async Task CancellingLeavesThePreviousArtifactAndNoPartialFiles()
    {
        var artifact = Path.Combine(_game.Root, "export", "linux", "TinyGame-1.2.0-linux-x64.tar.gz");
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
        File.WriteAllText(artifact, "previous");
        using var cancel = new CancellationTokenSource();
        _game.DuringPublish = cancel.Cancel;

        var result = await Run(ExportPreset.Create("Linux", ExportPlatform.Linux), ct: cancel.Token);

        Assert.True(result.Cancelled);
        Assert.False(result.Succeeded);
        Assert.Equal("previous", File.ReadAllText(artifact));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(artifact)!, "*" + Archiver.PartialSuffix));
        Assert.False(Directory.Exists(Path.Combine(_game.Root, ".mainframe", "export", "stage", "linux")));
    }

    [Fact]
    public async Task RunAllKeepsGoingAfterAnInvalidPreset()
    {
        var invalid = ExportPreset.Create("Mac on Linux", ExportPlatform.MacOS);
        List<ExportPreset> presets = [ExportPreset.Create("Linux", ExportPlatform.Linux), invalid, ExportPreset.Create("Windows", ExportPlatform.Windows)];
        var seen = new List<(int, int)>();

        var results = await new ExportRunner(_game.Environment()).RunAllAsync(_game.Root, presets,
            new SyncProgress(p => seen.Add((p.PresetIndex, p.PresetCount))), null, TestContext.Current.CancellationToken);

        Assert.Equal([true, false, true], results.Select(r => r.Succeeded));
        Assert.True(results[1].Invalid);
        Assert.Equal("2 exported, 1 skipped (invalid)", ExportResult.Tally(results));
        Assert.Equal([(0, 3), (1, 3), (2, 3)], seen.Distinct());
    }

    [Fact]
    public async Task TheSameExportTwiceGivesTheSameEntries()
    {
        var preset = ExportPreset.Create("Linux", ExportPlatform.Linux);
        var first = await Run(preset, ct: TestContext.Current.CancellationToken);
        var a = TarEntries(first.ArtifactPath!);
        var second = await Run(preset, ct: TestContext.Current.CancellationToken);
        Assert.Equal(a.OrderBy(p => p.Key), TarEntries(second.ArtifactPath!).OrderBy(p => p.Key));
    }

    /// <summary>An <see cref="IProgress{T}"/> that reports on the calling thread (Progress&lt;T&gt; posts to the pool).</summary>
    private sealed class SyncProgress(Action<ExportProgress> report) : IProgress<ExportProgress>
    {
        public void Report(ExportProgress value) => report(value);
    }
}

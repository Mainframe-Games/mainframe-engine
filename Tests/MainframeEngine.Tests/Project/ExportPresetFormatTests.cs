using System.Text;
using System.Text.Json.Nodes;

namespace MainframeEngine.Tests.Project;

/// <summary>The <c>export</c> section of <c>project.mfproj</c> (G5, docs/design/game-export.md#preset-schema-projectmfproj--export).</summary>
public sealed class ExportPresetFormatTests
{
    private static ProjectSettings Parse(string json) => ProjectSettings.Parse(Encoding.UTF8.GetBytes(json), "test.mfproj");

    private static string Text(ProjectSettings settings) => Encoding.UTF8.GetString(settings.ToJson());

    private const string Sample = """
        {
          "format": 5,
          "name": "My Game",
          "export": [
            { "name": "Windows (Steam)", "platform": "windows", "rid": "win-x64", "output": "export/windows", "steam": true },
            { "name": "Windows (Steam demo)", "platform": "windows", "rid": "win-x64", "output": "export/windows-demo", "demo": true, "steam": true },
            { "name": "macOS", "platform": "macos", "rid": "osx-arm64", "output": "export/macos",
              "bundleId": "com.example.mygame", "copyright": "© 2026 Example",
              "sign": "Developer ID Application: Example (ABCDE12345)", "notaryProfile": "example-notary" },
            { "name": "Linux", "platform": "linux", "rid": "linux-x64", "output": "export/linux" },
            { "name": "Linux server (headless)", "platform": "linux", "rid": "linux-x64",
              "output": "export/server", "archive": false, "exclude": ["Assets/Audio/**"], "selfContained": false,
              "readyToRun": false, "singleFile": true, "trim": true, "symbols": "separate", "profiler": false, "configuration": "Debug",
              "version": "1.2.3-beta", "icon": "Assets/server.png", "exe": "MyServer", "include": ["Assets/Scenes/**"] }
          ]
        }
        """;

    [Fact]
    public void TheSpecExampleParses()
    {
        var s = Parse(Sample);

        Assert.Equal(5, s.ExportPresets.Count);
        var windows = s.ExportPresets[0];
        Assert.Equal(("Windows (Steam)", ExportPlatform.Windows, "export/windows"), (windows.Name, windows.Platform, windows.Output));
        Assert.Equal(("win-x64", true, false), (windows.Desktop.Rid, windows.Desktop.Steam, windows.Demo));
        Assert.Null(windows.Desktop.MacOs);
        Assert.True(s.ExportPresets[1].Demo);

        var mac = s.ExportPresets[2].Desktop.MacOs!;
        Assert.Equal(("com.example.mygame", "© 2026 Example", "Developer ID Application: Example (ABCDE12345)", "example-notary"),
            (mac.BundleId, mac.Copyright, mac.Sign, mac.NotaryProfile));

        var server = s.ExportPresets[4];
        Assert.False(server.Archive);
        Assert.Equal(["Assets/Scenes/**"], server.Include);
        Assert.Equal(["Assets/Audio/**"], server.Exclude);
        Assert.Equal(("Debug", "1.2.3-beta", "Assets/server.png"), (server.Configuration, server.Version, server.Icon));
        var d = server.Desktop;
        Assert.Equal((false, false, true, true, ExportSymbols.Separate, "MyServer"), (d.SelfContained, d.ReadyToRun, d.SingleFile, d.Trim, d.Symbols, d.Exe));
        Assert.False(d.Profiler);
        Assert.True(windows.Desktop.Profiler);
    }

    [Fact]
    public void DefaultsAreNotWrittenAndEverythingRoundTrips()
    {
        var s = Parse(Sample);
        var text = Text(s);
        var json = JsonNode.Parse(text)!;
        var linux = json["export"]![3]!.AsObject();
        Assert.Equal(["name", "platform", "rid", "output"], linux.Select(p => p.Key));
        Assert.DoesNotContain("\"archive\": true", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"configuration\": \"Release\"", text, StringComparison.Ordinal);

        var again = Parse(text);
        Assert.Equal(text, Text(again));
        Assert.Equal(s.ExportPresets.Select(p => p.Name), again.ExportPresets.Select(p => p.Name));
    }

    [Fact]
    public void ANewPresetIsOnlyItsNameAndPlatform()
    {
        var s = new ProjectSettings { Name = "G" };
        s.ExportPresets.Add(ExportPreset.Create("Linux", ExportPlatform.Linux));
        var preset = JsonNode.Parse(Text(s))!["export"]![0]!.AsObject();

        Assert.Equal(["name", "platform"], preset.Select(p => p.Key));
        var back = Parse(Text(s)).ExportPresets[0];
        Assert.Equal(ExportPreset.DefaultInclude, back.Include);
        Assert.Equal(ExportPreset.DefaultExclude, back.Exclude);
        Assert.True(back.Desktop.SelfContained);
        Assert.True(back.Desktop.ReadyToRun);
        Assert.Equal("Release", back.Configuration);
    }

    [Fact]
    public void NoPresetsWritesNoSection()
    {
        Assert.Null(JsonNode.Parse(Text(new ProjectSettings { Name = "G" }))!["export"]);
    }

    [Fact]
    public void KeysOfAnotherPlatformWarn()
    {
        var sink = new MemoryLogSink { Levels = Log.Level.Warning };
        Log.AddSink(sink);
        try
        {
            var s = Parse("""
                { "format": 5, "export": [
                  { "name": "W", "platform": "windows", "bundleId": "com.x", "applicationId": "com.y" },
                  { "name": "M", "platform": "macos", "bundleId": "com.x" } ] }
                """);
            Assert.Null(s.ExportPresets[0].Desktop.MacOs);
            Assert.Equal("com.x", s.ExportPresets[1].Desktop.MacOs!.BundleId);
        }
        finally
        {
            Log.RemoveSink(sink);
        }

        var warnings = sink.Snapshot().Select(e => e.Message).Where(m => m.Contains("test.mfproj", StringComparison.Ordinal)).ToArray();
        Assert.Contains(warnings, m => m.Contains("'export[0].bundleId'", StringComparison.Ordinal));
        Assert.Contains(warnings, m => m.Contains("'export[0].applicationId'", StringComparison.Ordinal));
        Assert.DoesNotContain(warnings, m => m.Contains("export[1]", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""{ "format": 5, "export": [ { "name": "A", "platform": "linux" }, { "name": "A", "platform": "windows" } ] }""", "export[1].name 'A' is used by another preset")]
    [InlineData("""{ "format": 5, "export": [ { "platform": "linux" } ] }""", "export[0].name must not be empty")]
    [InlineData("""{ "format": 5, "export": [ { "name": "A" } ] }""", "export[0].platform is required")]
    [InlineData("""{ "format": 5, "export": [ { "name": "A", "platform": "android" } ] }""", "export[0].platform 'android' is not")]
    [InlineData("""{ "format": 5, "export": [ { "name": "A", "platform": "linux", "symbols": "both" } ] }""", "export[0].symbols 'both' is not")]
    [InlineData("""{ "format": 5, "export": [ { "name": "A", "platform": "linux", "demo": "yes" } ] }""", "export[0].demo must be true or false")]
    [InlineData("""{ "format": 5, "export": [ { "name": "A", "platform": "linux", "configuration": " " } ] }""", "export[0].configuration is invalid")]
    [InlineData("""{ "format": 5, "export": [ { "name": "A", "platform": "linux", "include": [1] } ] }""", "export[0].include must contain only strings")]
    [InlineData("""{ "format": 5, "export": { "name": "A" } }""", "export must be an array")]
    [InlineData("""{ "format": 5, "export": [ 3 ] }""", "export[0] must be an object")]
    public void BadPresetsThrowWithTheKeyPath(string json, string message)
    {
        var e = Assert.Throws<InvalidDataException>(() => Parse(json));
        Assert.Contains(message, e.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void OlderFormatsUpgradeToTheCurrentOneWithNothingElseChanged(int format)
    {
        var older = Parse($$"""{ "format": {{format}}, "name": "Old", "mainScene": "Assets/Scenes/Main.mscene", "window": { "width": 640 } }""");
        var json = JsonNode.Parse(Text(older))!;

        Assert.Equal(ProjectSettingsFormat.Current, (int)json["format"]!);
        Assert.Equal(5, ProjectSettingsFormat.Current);
        Assert.Null(json["export"]);
        Assert.Equal(("Old", "Assets/Scenes/Main.mscene", 640), (older.Name, older.MainScene, older.Window.Width));
    }

    [Fact]
    public void TheFourToFiveStepLeavesTheTreeAlone()
    {
        var root = JsonNode.Parse("""{ "format": 4, "name": "N", "audio": { "busLayout": "Assets/b.mres" } }""")!.AsObject();
        var before = root.DeepClone().AsObject();
        before.Remove("format");
        ProjectSettingsFormat.Upgrade(root, 4, ProjectSettingsFormat.Current, ProjectSettingsFormat.Migrations, "t");
        Assert.Equal(5, (int)root["format"]!);
        root.Remove("format");
        Assert.True(JsonNode.DeepEquals(before, root));
    }

    [Fact]
    public void AnOlderEditorRejectsAFormat5ProjectInsteadOfDroppingItsPresets()
    {
        // The format-4 reader (the engine before G5) refuses the file: it would otherwise drop the section on save.
        var e = Assert.Throws<InvalidDataException>(() =>
            ProjectSettingsFormat.Parse(Encoding.UTF8.GetBytes(Sample), "new.mfproj", 4, [.. ProjectSettingsFormat.Migrations.Take(3)]));
        Assert.Contains("has project format 5; this engine reads up to 4. Update the engine.", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDemoProjectFileLoadsWithItsPresets()
    {
        var file = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Examples", "Demo", ProjectSettings.FileName);
        var demo = ProjectSettings.Load(Path.GetFullPath(file));

        Assert.Equal(["Windows", "macOS", "Linux", "Linux server"], demo.ExportPresets.Select(p => p.Name));
        Assert.False(demo.ExportPresets[3].Archive);
    }

    [Fact]
    public void ClonesAreDeep()
    {
        var preset = Parse(Sample).ExportPresets[2];
        var copy = preset.Clone();
        copy.Desktop.MacOs!.BundleId = "other";
        copy.Exclude.Add("x");
        copy.Desktop.Rid = "osx-x64";

        Assert.Equal("com.example.mygame", preset.Desktop.MacOs!.BundleId);
        Assert.Equal(ExportPreset.DefaultExclude, preset.Exclude);
        Assert.Equal("osx-arm64", preset.Desktop.Rid);
    }
}

using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace MainframeEngine.Tests.Saves;

public sealed record ConfigTestResolution(int Width, int Height);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ConfigTestResolution))]
internal sealed partial class ConfigTestJson : JsonSerializerContext;

public sealed class ConfigFileTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mf-config-tests", Guid.NewGuid().ToString("N"));

    public ConfigFileTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a temp folder left behind is harmless.
        }
    }

    private string File(string name) => Path.Combine(_directory, name);

    private static ConfigFile Parse(string json, string source = "test", int currentVersion = 0, IReadOnlyList<JsonMigration>? migrations = null) =>
        ConfigFile.Parse(Encoding.UTF8.GetBytes(json), source, currentVersion, migrations);

    [Fact]
    public void SectionsAndKeys()
    {
        var config = new ConfigFile();
        Assert.False(config.HasSection("game"));
        Assert.Empty(config.GetSections());

        config.SetValue("game", "subtitles", true);
        config.SetValue("game", "fov", 80);
        config.SetValue("audio", "device", "Speakers");

        Assert.True(config.HasSection("game"));
        Assert.True(config.HasSectionKey("game", "fov"));
        Assert.False(config.HasSectionKey("game", "nope"));
        Assert.False(config.HasSectionKey("nope", "fov"));
        Assert.Equal(["game", "audio"], config.GetSections());
        Assert.Equal(["subtitles", "fov"], config.GetSectionKeys("game"));
        Assert.Empty(config.GetSectionKeys("nope"));

        Assert.True(config.EraseSectionKey("game", "fov"));
        Assert.False(config.EraseSectionKey("game", "fov"));
        Assert.True(config.EraseSectionKey("game", "subtitles"));
        Assert.False(config.HasSection("game")); // an emptied section goes too (Godot)

        Assert.True(config.EraseSection("audio"));
        Assert.False(config.EraseSection("audio"));
        config.SetValue("a", "b", 1);
        config.Clear();
        Assert.Empty(config.GetSections());

        // The file's own header keys are not sections.
        Assert.Throws<ArgumentException>(() => config.SetValue("format", "x", 1));
        Assert.Throws<ArgumentException>(() => config.SetValue("version", "x", 1));
        Assert.Throws<ArgumentException>(() => config.SetValue("", "x", 1));
    }

    [Fact]
    public void TypedValuesRoundTripThroughTheFile()
    {
        var config = new ConfigFile { Version = 2 };
        config.SetValue("game", "subtitles", true);
        config.SetValue("game", "fov", 80);
        config.SetValue("game", "sensitivity", 0.35);
        config.SetValue("game", "name", "Brogan");
        config.SetValue("game", "recent", ["slot1", "autosave"]);
        config.SetValue("game", "name2", "x");
        config.SetValue("game", "name2", (string?)null); // null erases

        var json = Encoding.UTF8.GetString(config.ToJson());
        Assert.StartsWith("{\n  \"format\": 1,\n  \"version\": 2,\n  \"game\": {", json, StringComparison.Ordinal);
        Assert.EndsWith("}\n", json, StringComparison.Ordinal);

        var read = Parse(json);
        Assert.Equal(2, read.Version);
        Assert.True(read.GetBool("game", "subtitles"));
        Assert.Equal(80, read.GetInt("game", "fov"));
        Assert.Equal(80.0, read.GetDouble("game", "fov")); // an integer is a number too
        Assert.Equal(0.35, read.GetDouble("game", "sensitivity"));
        Assert.Equal("Brogan", read.GetString("game", "name"));
        var recent = new List<string> { "stale" };
        Assert.True(read.GetStrings("game", "recent", recent));
        Assert.Equal(["slot1", "autosave"], recent);
        Assert.False(read.HasSectionKey("game", "name2"));

        // Missing keys give the defaults, silently.
        Assert.Equal(7, read.GetInt("game", "missing", 7));
        Assert.Equal("d", read.GetString("nope", "missing", "d"));
        Assert.False(read.GetStrings("game", "missing", recent));
        Assert.Empty(recent);

        Assert.Throws<ArgumentOutOfRangeException>(() => config.SetValue("game", "bad", double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => config.SetValue("game", "bad", double.PositiveInfinity));
    }

    [Fact]
    public void WrongTypesReturnTheDefaultAndWarnOncePerKey()
    {
        var source = "wrong-types-" + Guid.NewGuid().ToString("N");
        var sink = new MemoryLogSink { Levels = Log.Level.Warning };
        Log.AddSink(sink);
        try
        {
            var config = Parse("""
                {
                  "format": 1,
                  "game": { "fov": "eighty", "subtitles": 1, "scale": 1.5, "name": 3, "recent": ["a", 2], "flag": true }
                }
                """, source);

            Assert.Equal(60, config.GetInt("game", "fov", 60));
            Assert.Equal(60, config.GetInt("game", "fov", 60)); // a second read does not warn again
            Assert.True(config.GetBool("game", "subtitles", true));
            Assert.Equal(2, config.GetInt("game", "scale", 2)); // 1.5 is not an integer…
            Assert.Equal(1.5, config.GetDouble("game", "scale")); // …but is a number
            Assert.Null(config.GetString("game", "name"));
            var recent = new List<string>();
            Assert.False(config.GetStrings("game", "recent", recent));
            Assert.Empty(recent);
            Assert.Equal(0.0, config.GetDouble("game", "flag"));

            // Writing a key forgets its warning: a later bad value warns again.
            config.SetValue("game", "fov", "still wrong");
            Assert.Equal(1, config.GetInt("game", "fov", 1));
        }
        finally
        {
            Log.RemoveSink(sink);
        }

        var warnings = sink.Snapshot().Where(e => e.Message.Contains(source, StringComparison.Ordinal)).ToList();
        Assert.Equal(7, warnings.Count); // fov ×2 (before and after the write), subtitles, scale, name, recent, flag
        Assert.Equal(2, warnings.Count(e => e.Message.Contains("game.fov is not an integer", StringComparison.Ordinal)));
        Assert.All(warnings, e => Assert.Equal("Config", e.Category));
    }

    [Fact]
    public void IntegersAcceptWholeNumbersOnly()
    {
        var config = Parse("""{ "game": { "a": 3.0, "b": 3.5, "c": 1e12 } }""");
        Assert.Equal(3, config.GetInt("game", "a"));
        Assert.Equal(-1, config.GetInt("game", "b", -1));
        Assert.Equal(-1, config.GetInt("game", "c", -1)); // out of int range
    }

    [Fact]
    public void StructuredValuesUseTheGamesTypeInfo()
    {
        var config = new ConfigFile();
        config.Set("display", "custom", new ConfigTestResolution(1600, 900), ConfigTestJson.Default.ConfigTestResolution);
        Assert.Contains("\"width\": 1600", Encoding.UTF8.GetString(config.ToJson()), StringComparison.Ordinal);

        var read = Parse(Encoding.UTF8.GetString(config.ToJson()));
        Assert.Equal(new ConfigTestResolution(1600, 900), read.Get("display", "custom", ConfigTestJson.Default.ConfigTestResolution));
        Assert.Null(read.Get("display", "missing", ConfigTestJson.Default.ConfigTestResolution));

        // Not readable as the type: the default.
        var bad = Parse("""{ "display": { "custom": [1, 2] } }""");
        Assert.Null(bad.Get("display", "custom", ConfigTestJson.Default.ConfigTestResolution));

        // A null value erases the key.
        config.Set("display", "custom", (ConfigTestResolution)null!, ConfigTestJson.Default.ConfigTestResolution);
        Assert.False(config.HasSection("display"));
    }

    [Fact]
    public void OlderGameVersionsRunTheMigrations()
    {
        JsonMigration[] migrations =
        [
            new(1, static root => root["game"]!["fov"] = (int)root["game"]!["fovDegrees"]!), // v1 named it fovDegrees
            new(2, static root => root["game"]!.AsObject().Remove("fovDegrees")),
        ];

        var unversioned = Parse("""{ "format": 1, "game": { "fovDegrees": 75 } }""", currentVersion: 3, migrations: migrations);
        Assert.Equal(3, unversioned.Version); // a missing version means 1
        Assert.Equal(75, unversioned.GetInt("game", "fov"));
        Assert.False(unversioned.HasSectionKey("game", "fovDegrees"));
        Assert.Contains("\"version\": 3", Encoding.UTF8.GetString(unversioned.ToJson()), StringComparison.Ordinal);

        var v2 = Parse("""{ "version": 2, "game": { "fov": 90, "fovDegrees": 75 } }""", currentVersion: 3, migrations: migrations);
        Assert.Equal(90, v2.GetInt("game", "fov")); // only the 2 → 3 step ran

        var missingStep = Assert.Throws<InvalidDataException>(() => Parse("""{ "game": {} }""", "s.json", 3, [migrations[1]]));
        Assert.Contains("no migration from settings version 1 to 2", missingStep.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnversionedConfigKeepsTheFilesVersion()
    {
        var config = Parse("""{ "version": 4, "game": { "x": 1 } }""");
        Assert.Equal(4, config.Version);
        Assert.Contains("\"version\": 4", Encoding.UTF8.GetString(config.ToJson()), StringComparison.Ordinal);
        Assert.DoesNotContain("version", Encoding.UTF8.GetString(new ConfigFile().ToJson()), StringComparison.Ordinal);
    }

    [Fact]
    public void ParseToleratesCommentsAndDropsNonSections()
    {
        var config = Parse("""
            {
              // the player edited this
              "format": 1,
              "game": { "fov": 80, },
              "stray": 3,
            }
            """);
        Assert.Equal(80, config.GetInt("game", "fov"));
        Assert.Equal(["game"], config.GetSections());

        Assert.Throws<InvalidDataException>(() => Parse("{ not json"));
        Assert.Throws<InvalidDataException>(() => Parse("[1, 2]"));
        Assert.Throws<InvalidDataException>(() => Parse("""{ "game": {}, "game": {} }"""));
        Assert.Throws<InvalidDataException>(() => Parse("""{ "format": "one" }"""));
    }

    [Fact]
    public void NewerFilesAreRejectedAndLeftUntouched()
    {
        Assert.Throws<InvalidDataException>(() => Parse("""{ "format": 2 }"""));
        Assert.Throws<InvalidDataException>(() => Parse("""{ "version": 5 }""", currentVersion: 4));

        var path = File("settings.json");
        var newer = """{ "format": 99, "game": { "fov": 80 } }"""u8.ToArray();
        System.IO.File.WriteAllBytes(path, newer);
        var config = ConfigFile.Load(path);
        Assert.Empty(config.GetSections()); // defaults…
        Assert.Equal(newer, System.IO.File.ReadAllBytes(path)); // …and the newer file is never moved or rewritten
        Assert.Empty(Directory.GetFiles(_directory, "*.corrupt-*"));

        System.IO.File.WriteAllBytes(path, """{ "version": 9 }"""u8.ToArray());
        Assert.Equal(3, ConfigFile.Load(path, currentVersion: 3).Version);
        Assert.Empty(Directory.GetFiles(_directory, "*.corrupt-*"));
    }

    [Fact]
    public void AMissingFileIsAnEmptyConfig()
    {
        var config = ConfigFile.Load(File("none.json"), currentVersion: 2);
        Assert.Empty(config.GetSections());
        Assert.Equal(2, config.Version);
        Assert.Equal(File("none.json"), config.Source);
        Assert.False(System.IO.File.Exists(File("none.json"))); // loading never writes
    }

    [Fact]
    public void SaveIsDurableWithOneBackup()
    {
        var path = File("settings.json");
        var config = new ConfigFile();
        for (var i = 1; i <= 3; i++)
        {
            config.SetValue("game", "saves", i);
            config.Save(path);
        }

        Assert.Equal(3, ConfigFile.Load(path).GetInt("game", "saves"));
        Assert.Equal(2, ConfigFile.Parse(System.IO.File.ReadAllBytes(DurableFile.BackupPath(path, 1)), "bak").GetInt("game", "saves"));
        Assert.False(System.IO.File.Exists(DurableFile.BackupPath(path, 2)));
        Assert.False(System.IO.File.Exists(DurableFile.TempPath(path)));
    }

    [Fact]
    public void ACorruptFileIsQuarantinedAndTheBackupRestored()
    {
        var path = File("settings.json");
        var config = new ConfigFile();
        config.SetValue("game", "fov", 70);
        config.Save(path);
        config.SetValue("game", "fov", 80);
        config.Save(path); // .bak1 holds fov 70
        System.IO.File.WriteAllText(path, "{ \"game\": { \"fov\": 8"); // truncated by a crash or a bad edit

        var loaded = ConfigFile.Load(path);

        Assert.Equal(70, loaded.GetInt("game", "fov"));
        var corrupt = Assert.Single(Directory.GetFiles(_directory, "settings.json.corrupt-*"));
        Assert.Equal("{ \"game\": { \"fov\": 8", System.IO.File.ReadAllText(corrupt)); // kept for support
        Assert.Equal(70, ConfigFile.Parse(System.IO.File.ReadAllBytes(path), "restored").GetInt("game", "fov")); // restored as the main file
    }

    [Fact]
    public void WithNoValidCopyTheConfigStartsEmpty()
    {
        var path = File("settings.json");
        System.IO.File.WriteAllText(path, "garbage");
        System.IO.File.WriteAllText(DurableFile.BackupPath(path, 1), "also garbage");

        var loaded = ConfigFile.Load(path);

        Assert.Empty(loaded.GetSections());
        Assert.Single(Directory.GetFiles(_directory, "settings.json.corrupt-*"));
        loaded.SetValue("game", "fov", 90);
        loaded.Save(path); // the game keeps working
        Assert.Equal(90, ConfigFile.Load(path).GetInt("game", "fov"));
    }

    [Fact]
    public void AFlushedTempFileIsRecoveredWhenTheTargetIsMissing()
    {
        // A crash between the flushed .tmp and the rename (DurableFile's recovery order).
        var path = File("settings.json");
        System.IO.File.WriteAllText(DurableFile.TempPath(path), """{ "format": 1, "game": { "fov": 85 } }""");

        Assert.Equal(85, ConfigFile.Load(path).GetInt("game", "fov"));
        Assert.True(System.IO.File.Exists(path));
    }

    [Fact]
    public void RawNodesAreSharedWithUserSettings()
    {
        var config = new ConfigFile();
        config.SetNode("audio", "volume", new JsonObject { ["Music"] = 0.5 });
        Assert.Equal(0.5, (double)config.GetNode("audio", "volume")!["Music"]!);
        Assert.True(ConfigFile.TryGetNumber(JsonValue.Create(3), out var three) && three == 3);
        Assert.True(ConfigFile.TryGetNumber(JsonNode.Parse("2.5")!, out var parsed) && parsed == 2.5);
        Assert.False(ConfigFile.TryGetNumber(JsonValue.Create("3"), out _));
    }
}

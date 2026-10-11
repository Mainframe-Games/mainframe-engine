using System.Text.Json.Nodes;

namespace MainframeEngine.Tests.Saves;

public sealed class JsonMigrationsTests
{
    [Fact]
    public void AMissingVersionMeansOne()
    {
        Assert.Equal(1, JsonMigrations.ReadVersion(new JsonObject(), "version", 3, "x", "save version"));
        Assert.Equal(2, JsonMigrations.ReadVersion(new JsonObject { ["version"] = 2 }, "version", 3, "x", "save version"));
        Assert.Equal(2, JsonMigrations.ReadVersion(new JsonObject { ["version"] = 2.0 }, "version", 3, "x", "save version"));
    }

    [Fact]
    public void StepsRunInOrderFromTheDocumentVersion()
    {
        var root = new JsonObject { ["log"] = "" };
        JsonMigration[] steps =
        [
            new(3, static r => r["log"] = (string)r["log"]! + "3"), // registration order does not matter
            new(1, static r => r["log"] = (string)r["log"]! + "1"),
            new(2, static r => r["log"] = (string)r["log"]! + "2"),
        ];

        JsonMigrations.Upgrade(root, 2, 4, steps, "x", "save version");

        Assert.Equal("23", (string)root["log"]!);
        Assert.Null(root["version"]); // the caller owns the version key
    }

    [Fact]
    public void AMissingStepNamesTheFileAndTheKind()
    {
        var e = Assert.Throws<InvalidDataException>(() =>
            JsonMigrations.Upgrade(new JsonObject(), 1, 3, [new JsonMigration(1, static _ => { })], "slot1.msave", "save version"));

        Assert.Equal("'slot1.msave': no migration from save version 2 to 3.", e.Message);
    }

    [Theory]
    [InlineData(4, "has settings format 4; this build reads up to 3")]
    [InlineData(0, "invalid settings format 0")]
    [InlineData(-2, "invalid settings format -2")]
    public void NewerAndInvalidVersionsAreRejected(int version, string expected)
    {
        var e = Assert.Throws<InvalidDataException>(() =>
            JsonMigrations.ReadVersion(new JsonObject { ["format"] = version }, "format", 3, "settings.json", "settings format"));

        Assert.Contains(expected, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANonIntegerVersionIsRejected()
    {
        Assert.Throws<InvalidDataException>(() => JsonMigrations.ReadVersion(new JsonObject { ["version"] = "2" }, "version", 3, "x", "v"));
        Assert.Throws<InvalidDataException>(() => JsonMigrations.ReadVersion(new JsonObject { ["version"] = 1.5 }, "version", 3, "x", "v"));
    }

    [Fact]
    public void ProjectFilesKeepTheirMessagesThroughTheSharedLoop()
    {
        var e = Assert.Throws<InvalidDataException>(() =>
            ProjectSettingsFormat.Upgrade(new JsonObject(), 1, 3, [new ProjectMigration(1, static _ => { })], "gap.mfproj"));

        Assert.Equal("'gap.mfproj': no migration from project format 2 to 3.", e.Message);
    }
}

using System.Text;
using System.Text.Json.Nodes;

namespace MainframeEngine.Tests.Project;

/// <summary>project.mfproj's "splash" section (BRUTAL B1, ADR 0192): defaults, parsing, clamping, the @brutal lock, the migration.</summary>
public sealed class SplashSettingsTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("mf-splash-settings").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static ProjectSettings Parse(string json) => ProjectSettings.Parse(Encoding.UTF8.GetBytes(json), "test.mfproj");

    private static string Text(ProjectSettings settings) => Encoding.UTF8.GetString(settings.ToJson());

    private static string Current(string splash) => $$"""{ "format": {{ProjectSettingsFormat.Current}}, "name": "G", "splash": {{splash}} }""";

    private static List<string> Warnings(Action run)
    {
        var sink = new MemoryLogSink { Levels = Log.Level.Warning };
        Log.AddSink(sink);
        try
        {
            run();
        }
        finally
        {
            Log.RemoveSink(sink);
        }

        return [.. sink.Snapshot().Select(e => e.Message)];
    }

    [Fact]
    public void TheDefaultsShowMainframeThenBrutalForTwoSecondsOnBlackAndAreNotWritten()
    {
        var s = new ProjectSettings { Name = "G" }.Splash;
        Assert.Equal([SplashProjectSettings.MainframeCard, SplashProjectSettings.BrutalCard], s.Cards);
        Assert.Equal(s.Cards, s.ResolvedCards);
        Assert.Equal(("#000000", 2f, true, false), (s.Background, s.HoldSeconds, s.Skippable, s.NonProfit));
        Assert.False(s.LockApplies);
        Assert.Empty(s.Warnings);
        Assert.True(s.UsesWhiteLogo);
        Assert.Null(JsonNode.Parse(Text(new ProjectSettings { Name = "G" }))!["splash"]);
    }

    [Fact]
    public void EveryKeyParsesAndRoundTrips()
    {
        var s = Parse(Current("""
            { "cards": ["@brutal", "@mainframe", "Content/Brand/studio.png"], "background": "#ffffff", "holdSeconds": 3.5,
              "skippable": false, "nonProfit": true }
            """));
        Assert.Equal(["@brutal", "@mainframe", "Content/Brand/studio.png"], s.Splash.Cards);
        Assert.Equal(("#ffffff", 3.5f, false, true), (s.Splash.Background, s.Splash.HoldSeconds, s.Splash.Skippable, s.Splash.NonProfit));
        Assert.False(s.Splash.UsesWhiteLogo); // black BRUTAL logo on a light background

        var json = JsonNode.Parse(Text(s))!["splash"]!;
        Assert.Equal("Content/Brand/studio.png", (string?)json["cards"]![2]);
        Assert.Equal(3.5f, (float)json["holdSeconds"]!);
        var again = Parse(Text(s));
        Assert.Equal(s.Splash.Cards, again.Splash.Cards);
        Assert.Equal(Text(s), Text(again));
    }

    [Theory]
    [InlineData(0.5f, 1.5f)]
    [InlineData(1.5f, 1.5f)]
    [InlineData(10f, 10f)]
    [InlineData(45f, 10f)]
    public void TheHoldIsClampedForLegibilityAndAWarningNamesIt(float written, float used)
    {
        ProjectSettings? s = null;
        var warnings = Warnings(() => s = Parse(Current($$"""{ "holdSeconds": {{written.ToString(System.Globalization.CultureInfo.InvariantCulture)}} }""")));
        Assert.Equal(used, s!.Splash.HoldSeconds);
        Assert.Equal(written != used, warnings.Any(w => w.Contains("splash.holdSeconds", StringComparison.Ordinal)));
        Assert.Equal(1.5f, new SplashProjectSettings { HoldSeconds = 0f }.HoldSeconds);
        Assert.Throws<ArgumentOutOfRangeException>(() => new SplashProjectSettings { HoldSeconds = float.NaN });
    }

    [Theory]
    [InlineData("""["@mainframe", "Content/Brand/studio.png"]""", false, "@mainframe @brutal Content/Brand/studio.png")]
    [InlineData("""["Content/Brand/studio.png"]""", false, "@brutal Content/Brand/studio.png")]
    [InlineData("""["Content/Brand/studio.png", "@mainframe"]""", false, "Content/Brand/studio.png @mainframe @brutal")]
    [InlineData("""[]""", false, "@brutal")]
    [InlineData("""[]""", true, "")]
    [InlineData("""["@mainframe", "Content/Brand/studio.png"]""", true, "@mainframe Content/Brand/studio.png")]
    public void TheBrutalLockPutsTheCardBackUnlessNonProfit(string cards, bool nonProfit, string resolved)
    {
        var settings = Parse(Current($$"""{ "cards": {{cards}}, "nonProfit": {{(nonProfit ? "true" : "false")}} }"""));
        var written = JsonNode.Parse(cards)!.AsArray().Select(n => (string)n!).ToList();
        Assert.Equal(resolved, string.Join(' ', settings.Splash.ResolvedCards));
        Assert.Equal(written, settings.Splash.Cards);
        Assert.Equal(written, Parse(Text(settings)).Splash.Cards); // the saver writes what the user set, never the lock's card
    }

    [Fact]
    public void TheBrutalLockInsertsTheCardAndLoadWarnsOncePerLoad()
    {
        var file = Path.Combine(_directory, ProjectSettings.FileName);
        File.WriteAllText(file, Current("""{ "cards": ["@mainframe"] }"""));

        var loaded = Warnings(() => ProjectSettings.Load(file));
        Assert.Single(loaded, w => w.Contains("BRUTAL licence (condition 2.2)", StringComparison.Ordinal));
        Assert.Contains(loaded, w => w.Contains("brutal@ahwoo.com", StringComparison.Ordinal));
        Assert.DoesNotContain(Warnings(() => ProjectSettings.Parse(File.ReadAllBytes(file), file)), w => w.Contains("BRUTAL licence", StringComparison.Ordinal));

        // Saving keeps the file as the user wrote it, so the next load warns again until it is fixed.
        ProjectSettings.Load(file).Save(file);
        Assert.Single(Warnings(() => ProjectSettings.Load(file)), w => w.Contains("BRUTAL licence (condition 2.2)", StringComparison.Ordinal));
    }

    [Fact]
    public void DuplicateCardsShowOnceAndWarn()
    {
        var s = Parse(Current("""{ "cards": ["@mainframe", "@brutal", "@brutal", "Content/a.png", "Content/a.png"] }""")).Splash;
        Assert.Equal(["@mainframe", "@brutal", "Content/a.png"], s.ResolvedCards);
        Assert.Equal(2, s.Warnings.Count(w => w.Contains("more than once", StringComparison.Ordinal)));
        Assert.Equal(5, s.Cards.Count); // the file is not rewritten behind the user's back
    }

    [Theory]
    [InlineData("""{ "cards": ["@studio"] }""", "splash.cards[0]")]
    [InlineData("""{ "cards": ["@mainframe", "Content/Brand/studio.jpg"] }""", "splash.cards[1]")]
    [InlineData("""{ "cards": ["Brand/studio.png"] }""", "splash.cards[0]")]
    [InlineData("""{ "cards": ["Content/../secret.png"] }""", "splash.cards[0]")]
    [InlineData("""{ "cards": [""] }""", "splash.cards[0]")]
    [InlineData("""{ "cards": [1] }""", "splash.cards")]
    [InlineData("""{ "background": "black" }""", "splash.background is invalid")]
    [InlineData("""{ "background": "#00000080" }""", "splash.background is invalid")]
    [InlineData("""{ "holdSeconds": "long" }""", "splash.holdSeconds")]
    [InlineData("""{ "skippable": 1 }""", "splash.skippable")]
    [InlineData("""1""", "splash must be an object")]
    public void InvalidEntriesNameTheirIndex(string splash, string message) =>
        Assert.Contains(message, Assert.Throws<InvalidDataException>(() => Parse(Current(splash))).Message, StringComparison.Ordinal);

    [Fact]
    public void Format3ProjectsGetTheDefaultSplashThroughTheMigration()
    {
        Assert.Equal(4, ProjectSettingsFormat.Current);
        var s = Parse("""{ "format": 3, "name": "Old" }""");
        Assert.Equal([SplashProjectSettings.MainframeCard, SplashProjectSettings.BrutalCard], s.Splash.ResolvedCards);
        Assert.Equal(4, (int)JsonNode.Parse(Text(s))!["format"]!);

        var root = new JsonObject { ["format"] = 3, ["name"] = "Old" };
        ProjectSettingsFormat.Upgrade(root, 3, ProjectSettingsFormat.Current, ProjectSettingsFormat.Migrations, "old.mfproj");
        Assert.IsType<JsonObject>(root["splash"]);
    }

    [Theory]
    [InlineData("#000000", true)]
    [InlineData("#000", true)]
    [InlineData("#808080", true)]   // sRGB 0.5 is linear 0.216: dark
    [InlineData("#bcbcbc", false)]  // linear 0.503: light
    [InlineData("#ffffff", false)]
    [InlineData("#ff0000", true)]   // relative luminance 0.2126
    public void TheBrutalLogoVariantFollowsTheBackgroundsRelativeLuminance(string background, bool white) =>
        Assert.Equal(white, new SplashProjectSettings { Background = background }.UsesWhiteLogo);
}

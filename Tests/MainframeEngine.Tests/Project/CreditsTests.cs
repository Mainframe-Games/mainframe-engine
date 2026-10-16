using MainframeEngine.Tests.TestAssets;

namespace MainframeEngine.Tests.Project;

/// <summary>
/// The credits (BRUTAL B1, ADR 0192): the engine's entries come first and are there before any game code runs, the BRUTAL
/// licence's copyright line verbatim; the third-party entries are THIRD_PARTY_NOTICES.md's game-shipped rows, read from the
/// copy embedded at build, so they cannot drift; games append their own.
/// </summary>
public sealed class CreditsTests
{
    [Fact]
    public void TheEngineEntriesComeFirstWithBrutalsCopyrightLine()
    {
        var entries = Credits.Entries;
        Assert.Equal(new CreditEntry(Credits.EngineSection, "Mainframe Engine", Credits.MainframeNotice), entries[0]);
        Assert.Equal(new CreditEntry(Credits.EngineSection, "BRUTAL", "BRUTAL game development framework is Copyright (c) 2024 Ahwoo Limited."), entries[1]);
        Assert.Equal(Credits.ThirdPartySection, entries[2].Section);
    }

    [Fact]
    public void TheEmbeddedNoticesAreTheRepositorysFile()
    {
        var file = File.ReadAllText(Path.Combine(TestPaths.RepositoryRoot(), "THIRD_PARTY_NOTICES.md"));
        Assert.Equal(file.ReplaceLineEndings("\n"), ThirdPartyNotices.ReadEmbedded().ReplaceLineEndings("\n"));
    }

    [Fact]
    public void TheThirdPartyEntriesAreTheNoticesRowsThatShipInGames()
    {
        var rows = ThirdPartyNotices.Parse(File.ReadAllText(Path.Combine(TestPaths.RepositoryRoot(), "THIRD_PARTY_NOTICES.md")));
        var expected = rows.Where(r => r.ShipsInGames && r.Name != "BRUTAL")
            .Select(r => (r.Version is { } v ? $"{r.Name} {v}" : r.Name, r.Credit ?? r.Licence)).ToArray();
        var actual = Credits.CreateEngineEntries().Where(e => e.Section == Credits.ThirdPartySection).Select(e => (e.Name, e.Notice ?? "")).ToArray();
        Assert.Equal(expected, actual);
        Assert.Contains(actual, e => e.Name.StartsWith("Jolt Physics", StringComparison.Ordinal));
        Assert.Contains(actual, e => e.Name.StartsWith("CommunityToolkit.HighPerformance", StringComparison.Ordinal));
        Assert.DoesNotContain(actual, e => e.Name.StartsWith("Tabler Icons", StringComparison.Ordinal)); // editor only
        Assert.DoesNotContain(actual, e => e.Name.StartsWith("Whisper.net", StringComparison.Ordinal)); // editor only
        Assert.DoesNotContain(actual, e => e.Name.StartsWith("GetText.NET.Extractor", StringComparison.Ordinal)); // a tool
        Assert.DoesNotContain(actual, e => e.Name.StartsWith("Poly Haven", StringComparison.Ordinal)); // Demo and tests
    }

    [Fact]
    public void FreeTypesRequiredCreditIsItsNotice()
    {
        var freeType = Credits.Entries.Single(e => e.Name.StartsWith("FreeType ", StringComparison.Ordinal));
        Assert.Equal("Portions of this software are copyright © 2026 The FreeType Project (https://freetype.org). All rights reserved.", freeType.Notice);
    }

    [Fact]
    public void TheParserReadsLinksCodeAndVersions()
    {
        const string markdown = """
            | Component | Version | Licence | Where | Shipped in |
            |---|---|---|---|---|
            | [Thing](https://x.example) (a `detail`) | 1.2 | **MIT** | `a/b` | engine |
            | Tool | — | MIT | — | nothing: a tool |
            | Panel kit | 3 | MIT | — | editor only: panels |

            ## Thing

            > Thing must be credited.
            """;
        var rows = ThirdPartyNotices.Parse(markdown);
        Assert.Equal(new ThirdPartyNotice("Thing", "1.2", "MIT", "engine", "Thing must be credited."), rows[0]);
        Assert.True(rows[0].ShipsInGames);
        Assert.Null(rows[1].Version);
        Assert.False(rows[1].ShipsInGames);
        Assert.False(rows[2].ShipsInGames);
    }

    [Fact]
    public void GamesAppendTheirOwnEntriesAfterTheEngines()
    {
        var name = "Credits test " + Guid.NewGuid().ToString("N");
        Credits.Add("Studio", name, "  ");
        var entries = Credits.Entries;
        Assert.Equal(new CreditEntry("Studio", name, null), entries[^1]); // a blank notice is none
        Assert.Equal(Credits.EngineSection, entries[0].Section);
        Assert.Throws<ArgumentException>(() => Credits.Add(" ", "x"));
        Assert.Throws<ArgumentException>(() => Credits.Add("Studio", ""));
        Assert.Equal([Credits.EngineSection, Credits.ThirdPartySection], Credits.BySection().Select(g => g.Key).Take(2));
    }
}

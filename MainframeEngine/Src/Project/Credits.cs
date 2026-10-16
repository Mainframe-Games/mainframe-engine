namespace MainframeEngine;

/// <summary>One line of the credits: a section ("Engine", "Third-party", a game's own), a name and an optional notice.</summary>
/// <param name="Notice">Legal text (a copyright line, a licence name), shown as is and never translated; null for none.</param>
public readonly record struct CreditEntry(string Section, string Name, string? Notice);

/// <summary>
/// The credits every game shows (BRUTAL B1, ADR 0192). The engine's own entries are registered before any game code runs:
/// <see cref="EngineSection"/> holds Mainframe Engine and BRUTAL (the BRUTAL licence's condition 2.1 copyright line,
/// <see cref="BrutalNotice"/>), <see cref="ThirdPartySection"/> every component of <c>THIRD_PARTY_NOTICES.md</c> that ships
/// in games, with its licence or required credit. Games add their own with <see cref="Add"/>, for example from an
/// autoload's <see cref="Node.OnReady"/>. <see cref="SettingsMenu"/>'s Credits tab lists them; a game with its own screen
/// lists <see cref="Entries"/>. Section and component names are not translated; notices are legal text and stay in English.
/// </summary>
public static class Credits
{
    public const string EngineSection = "Engine";
    public const string ThirdPartySection = "Third-party";

    /// <summary>Mainframe Engine's MIT line (its <c>LICENSE</c>).</summary>
    public const string MainframeNotice = "Copyright (c) 2024 Mainframe Games. MIT License.";

    /// <summary>The BRUTAL licence's condition 2.1: this line on the credits screen or equivalent, verbatim.</summary>
    public const string BrutalNotice = "BRUTAL game development framework is Copyright (c) 2024 Ahwoo Limited.";

    private static readonly Lock Gate = new();
    private static CreditEntry[] _entries = CreateEngineEntries();

    /// <summary>Every entry in registration order: the engine's first, then the games'.</summary>
    public static IReadOnlyList<CreditEntry> Entries => Volatile.Read(ref _entries);

    /// <summary>Appends an entry (thread-safe). A blank <paramref name="notice"/> is none.</summary>
    public static void Add(string section, string name, string? notice = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(section);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var entry = new CreditEntry(section, name, string.IsNullOrWhiteSpace(notice) ? null : notice);
        lock (Gate)
            Volatile.Write(ref _entries, [.. _entries, entry]);
    }

    /// <summary><see cref="Entries"/> grouped by section, sections in the order they first appear.</summary>
    public static IEnumerable<IGrouping<string, CreditEntry>> BySection() => Entries.GroupBy(static e => e.Section, StringComparer.Ordinal);

    /// <summary>The engine's own entries (the start of <see cref="Entries"/>).</summary>
    internal static CreditEntry[] CreateEngineEntries()
    {
        List<CreditEntry> entries =
        [
            new(EngineSection, "Mainframe Engine", MainframeNotice),
            new(EngineSection, "BRUTAL", BrutalNotice),
        ];
        foreach (var notice in ThirdPartyNotices.Parse(ThirdPartyNotices.ReadEmbedded()))
        {
            if (!notice.ShipsInGames || entries.Exists(e => string.Equals(e.Name, notice.Name, StringComparison.Ordinal)))
                continue;
            entries.Add(new CreditEntry(ThirdPartySection, notice.Version is { } version ? $"{notice.Name} {version}" : notice.Name,
                notice.Credit ?? notice.Licence));
        }

        return [.. entries];
    }
}

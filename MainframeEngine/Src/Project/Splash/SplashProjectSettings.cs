namespace MainframeEngine;

/// <summary>
/// The <c>splash</c> section of <c>project.mfproj</c> (BRUTAL B1, ADR 0192): the cards <c>SplashSequence</c> shows
/// before gameplay, their background, hold time and skipping.
/// </summary>
/// <remarks>
/// <see cref="BrutalCard"/> is locked: the BRUTAL licence (condition 2.2) requires the BRUTAL logo before gameplay, so
/// <see cref="ResolvedCards"/> puts it back after <see cref="MainframeCard"/> (or first) when <see cref="Cards"/> lacks it,
/// unless <see cref="NonProfit"/> (the README's waiver; confirm with Ahwoo). <see cref="MainframeCard"/> is not locked
/// (Mainframe is MIT). <see cref="Cards"/> keeps what the file says, and the saver writes it unchanged, so
/// <see cref="ProjectSettings.Load"/> warns (<see cref="Warnings"/>) on every load until the file is fixed.
/// </remarks>
public sealed class SplashProjectSettings
{
    /// <summary>The Mainframe Engine logo card.</summary>
    public const string MainframeCard = "@mainframe";

    /// <summary>The BRUTAL logo card (locked: the BRUTAL licence, 2.2).</summary>
    public const string BrutalCard = "@brutal";

    /// <summary>The shortest hold: a logo shown for less is not legible.</summary>
    public const float MinHoldSeconds = 1.5f;

    public const float MaxHoldSeconds = 10f;
    public const float DefaultHoldSeconds = 2f;
    public const string DefaultBackground = "#000000";

    /// <summary>
    /// The cards in order, as the file lists them: <see cref="MainframeCard"/>, <see cref="BrutalCard"/>, or a PNG in the
    /// game's <c>Content/</c> (<c>"Content/Brand/studio.png"</c>). Empty only makes sense with <see cref="NonProfit"/>.
    /// </summary>
    public List<string> Cards { get; } = [MainframeCard, BrutalCard];

    /// <summary>The clear colour behind every card: sRGB hex, <c>#RRGGBB</c> or <c>#RGB</c>.</summary>
    public string Background
    {
        get;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!(value.Length is 4 or 7 && value[0] == '#' && Color.HtmlIsValid(value)))
                throw new ArgumentException($"'{value}' is not an sRGB hex colour (#RRGGBB or #RGB).", nameof(value));
            field = value;
        }
    } = DefaultBackground;

    /// <summary>Seconds each card is fully visible, clamped to <see cref="MinHoldSeconds"/>–<see cref="MaxHoldSeconds"/>.</summary>
    public float HoldSeconds
    {
        get;
        set
        {
            if (float.IsNaN(value))
                throw new ArgumentOutOfRangeException(nameof(value), value, "The hold must be a number of seconds.");
            field = Math.Clamp(value, MinHoldSeconds, MaxHoldSeconds);
        }
    } = DefaultHoldSeconds;

    /// <summary>A key, click or gamepad button ends the current card's hold once it has run <see cref="MinHoldSeconds"/>.</summary>
    public bool Skippable { get; set; } = true;

    /// <summary>The game is non-profit: <see cref="BrutalCard"/> may be left out (BRUTAL's README waiver; confirm with Ahwoo).</summary>
    public bool NonProfit { get; set; }

    /// <summary>The lock adds <see cref="BrutalCard"/>, which <see cref="Cards"/> lacks.</summary>
    public bool LockApplies => !NonProfit && !Cards.Contains(BrutalCard);

    /// <summary>
    /// The cards shown: <see cref="Cards"/> without repeats (the first of each stays), with <see cref="BrutalCard"/> inserted
    /// after <see cref="MainframeCard"/> (or first) when the lock applies.
    /// </summary>
    public IReadOnlyList<string> ResolvedCards
    {
        get
        {
            var cards = new List<string>(Cards.Count + 1);
            foreach (var card in Cards)
                if (!cards.Contains(card))
                    cards.Add(card);
            if (!NonProfit && !cards.Contains(BrutalCard))
                cards.Insert(cards.IndexOf(MainframeCard) + 1, BrutalCard);
            return cards;
        }
    }

    /// <summary>BRUTAL's white logo goes on a background whose relative luminance (linear) is below 0.5, its black one otherwise.</summary>
    public bool UsesWhiteLogo => Color.FromHtml(Background).SrgbToLinear().Luminance < 0.5f;

    /// <summary>What <see cref="ProjectSettings.Load"/> warns about: the lock applying, repeated cards.</summary>
    public IReadOnlyList<string> Warnings
    {
        get
        {
            var warnings = new List<string>();
            if (LockApplies)
                warnings.Add($"splash.cards has no \"{BrutalCard}\": the BRUTAL licence (condition 2.2) requires the BRUTAL logo before gameplay, so " +
                             $"it is shown {(Cards.Contains(MainframeCard) ? $"after \"{MainframeCard}\"" : "first")}. Add \"{BrutalCard}\" to splash.cards, " +
                             "or set splash.nonProfit if the game is non-profit (confirm with Ahwoo: brutal@ahwoo.com).");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var reported = new HashSet<string>(StringComparer.Ordinal);
            foreach (var card in Cards)
                if (!seen.Add(card) && reported.Add(card))
                    warnings.Add($"splash.cards lists \"{card}\" more than once; it is shown once.");
            return warnings;
        }
    }

    /// <summary>Why <paramref name="entry"/> is not a card (phrased to follow its setting's name), or null when it is one.</summary>
    public static string? CardError(string entry)
    {
        if (string.IsNullOrWhiteSpace(entry))
            return "must not be empty";
        if (entry.StartsWith('@'))
            return entry is MainframeCard or BrutalCard ? null : $"'{entry}' is not a built-in card (\"{MainframeCard}\" or \"{BrutalCard}\")";
        if (!entry.StartsWith(ContentPaths.FolderName + "/", StringComparison.Ordinal) || !entry.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            || entry.Contains("..", StringComparison.Ordinal))
            return $"'{entry}' is not a PNG in the game's Content/ folder (\"Content/…/name.png\")";
        return null;
    }
}

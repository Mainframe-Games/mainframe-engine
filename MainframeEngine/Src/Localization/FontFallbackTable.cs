namespace MainframeEngine.Localization;

/// <summary>The fonts one locale needs beyond the defaults (for example Noto Sans JP for <c>ja</c>).</summary>
public sealed class LocaleFontSet : Resource
{
    /// <summary>The locale the fonts serve (<c>ja</c>, <c>zh_Hans</c>, <c>pt_BR</c>); matched against the lookup chain.</summary>
    [Export]
    public string Locale { get; set; } = string.Empty;

    /// <summary>Font files (content paths), in fallback order.</summary>
    [Export(File = "*.ttf,*.otf")]
    public string[] Fonts { get; set; } = [];
}

/// <summary>
/// Per-locale font fallback for the game UI: which font faces to load for the current locale. Stored as a
/// <c>.mres</c> resource (by convention <see cref="DefaultPath"/>), edited in the inspector like any resource.
/// </summary>
/// <remarks>
/// The UI server resolves it on start-up and on every <see cref="Tr.LocaleChanged"/>
/// (<see cref="Resolve(IReadOnlyList{string})"/> with <see cref="Tr.LocaleChain"/>) and loads the returned faces as
/// fallback faces. RmlUi cannot unload a single face, so faces accumulate over a session; fallback faces only
/// supply glyphs the primary font lacks, so earlier locales' faces never change how later text looks.
/// </remarks>
public sealed class FontFallbackTable : Resource
{
    /// <summary>Conventional location of the project's table.</summary>
    public const string DefaultPath = "Content/locale/fonts.mres";

    /// <summary>Faces loaded for every locale, after the locale-specific ones.</summary>
    [Export(File = "*.ttf,*.otf")]
    public string[] DefaultFonts { get; set; } = [];

    /// <summary>Locale-specific faces.</summary>
    [Export]
    public LocaleFontSet[] Locales { get; set; } = [];

    /// <summary>
    /// The faces for a lookup chain (most specific locale first, as <see cref="Tr.LocaleChain"/>): each matching set's
    /// fonts in chain order, then <see cref="DefaultFonts"/>, without duplicates.
    /// </summary>
    public IReadOnlyList<string> Resolve(IReadOnlyList<string> localeChain)
    {
        ArgumentNullException.ThrowIfNull(localeChain);
        var fonts = new List<string>();
        foreach (var locale in localeChain)
        {
            var normalized = LocaleId.Normalize(locale);
            foreach (var set in Locales)
            {
                if (set is null || LocaleId.Normalize(set.Locale) != normalized)
                    continue;
                foreach (var font in set.Fonts)
                {
                    if (!string.IsNullOrWhiteSpace(font) && !fonts.Contains(font, StringComparer.Ordinal))
                        fonts.Add(font);
                }
            }
        }

        foreach (var font in DefaultFonts)
        {
            if (!string.IsNullOrWhiteSpace(font) && !fonts.Contains(font, StringComparer.Ordinal))
                fonts.Add(font);
        }

        return fonts;
    }

    /// <summary>The faces for the current locale (<see cref="Tr.LocaleChain"/>).</summary>
    public IReadOnlyList<string> ResolveCurrent() => Resolve(Tr.LocaleChain);

    /// <summary>
    /// Loads the table at <paramref name="path"/> (default <see cref="DefaultPath"/>) through <see cref="ResourceLoader"/>,
    /// or returns null when the project has none.
    /// </summary>
    public static FontFallbackTable? TryLoad(string path = DefaultPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return File.Exists(ContentPaths.Resolve(path)) ? ResourceLoader.Load<FontFallbackTable>(path) : null;
    }
}

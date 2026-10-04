namespace MainframeEngine.Localization;

/// <summary>
/// Where <see cref="Tr"/> finds catalogs and how it falls back. Catalogs are GNU <c>.mo</c> files at
/// <c>{LocaleDirectory}/{locale}/LC_MESSAGES/{Domain}.mo</c>; compile them from <c>.po</c> with <c>mf-l10n compile</c>
/// (the build does this, see docs/design/localization.md).
/// </summary>
public sealed record LocalizationOptions
{
    /// <summary>Default catalog domain (file name without <c>.mo</c>).</summary>
    public const string DefaultDomain = "messages";

    /// <summary>Default locale folder, relative to the application (resolved with <see cref="ContentPaths"/>).</summary>
    public const string DefaultLocaleDirectory = "Content/locale";

    /// <summary>Missing-translation logging default for this engine build: on in Debug, off in Release.</summary>
    public const bool DefaultLogMissingTranslations =
#if DEBUG
        true;
#else
        false;
#endif

    /// <summary>The catalog domain: <c>{Domain}.mo</c>.</summary>
    public string Domain { get; init; } = DefaultDomain;

    /// <summary>The folder holding one sub-folder per locale. Relative paths go through <see cref="ContentPaths.Resolve(string)"/>.</summary>
    public string LocaleDirectory { get; init; } = DefaultLocaleDirectory;

    /// <summary>
    /// The language the msgids are written in. It ends every fallback chain and needs no catalog: untranslated
    /// strings show the msgid, and no missing-translation warnings are logged while it is the current language.
    /// </summary>
    public string SourceLocale { get; init; } = "en";

    /// <summary>
    /// Locales tried after the requested locale and its parents, before <see cref="SourceLocale"/>
    /// (for example <c>["es"]</c> so <c>ca</c> players see Spanish rather than English for untranslated strings).
    /// </summary>
    public IReadOnlyList<string> FallbackLocales { get; init; } = [];

    /// <summary>
    /// Logs each msgid that has no translation in the current locale, once per locale (and invalid translated
    /// format strings). Defaults to <see cref="DefaultLogMissingTranslations"/>.
    /// </summary>
    public bool LogMissingTranslations { get; init; } = DefaultLogMissingTranslations;

    /// <summary>Absolute path of the locale folder.</summary>
    public string ResolveLocaleDirectory() => ContentPaths.Resolve(LocaleDirectory);

    /// <summary>Absolute path of the catalog for <paramref name="locale"/> (which need not exist).</summary>
    public string CatalogPath(string locale) =>
        Path.Combine(ResolveLocaleDirectory(), LocaleId.Normalize(locale), "LC_MESSAGES", Domain + ".mo");
}

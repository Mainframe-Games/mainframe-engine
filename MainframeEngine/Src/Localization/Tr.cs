using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace MainframeEngine.Localization;

/// <summary>Arguments of <see cref="Tr.LocaleChanged"/>.</summary>
public sealed class LocaleChangedEventArgs(string previousLocale, string locale) : EventArgs
{
    /// <summary>The locale before the change (equal to <see cref="Locale"/> after a reload).</summary>
    public string PreviousLocale { get; } = previousLocale;

    /// <summary>The new current locale.</summary>
    public string Locale { get; } = locale;
}

/// <summary>
/// Translations (gettext): <c>Tr._("Settings")</c>, <c>Tr.P("menu", "Open")</c>, <c>Tr.N("{0} enemy left", "{0} enemies
/// left", n)</c>. Catalogs are <c>.mo</c> files under <see cref="LocalizationOptions.LocaleDirectory"/>; the current
/// locale switches at run time with <see cref="SetLocale"/>, which raises <see cref="LocaleChanged"/> and notifies every
/// scene tree (<see cref="Node.OnLocaleChanged"/>). See docs/design/localization.md.
/// </summary>
/// <remarks>
/// <para>
/// Lookups never allocate: the result is the translation stored in the catalog, or the msgid itself when there is
/// none (untranslated strings show the source text). Context and span lookups compose their keys on the stack.
/// </para>
/// <para>
/// <b>Format safety.</b> The overloads with arguments treat the message as a .NET composite format
/// (<c>"Score: {0}"</c>) and format with <see cref="Culture"/>. A translation that is not a valid format, or that uses
/// more arguments than the call passes, never throws: the source format is used instead (logged once). The
/// overloads without arguments never format, so literal braces need no escaping there. In plural messages
/// <c>n</c> is always <c>{0}</c> and further arguments start at <c>{1}</c>.
/// </para>
/// <para>
/// Strings are extracted for translators with the GetText.NET extractor (aliases <c>_</c>, <c>P</c>, <c>N</c>,
/// <c>NP</c>; <c>just l10n-extract</c>), so the msgid must be a string literal (or an interpolated string, see
/// <see cref="TrInterpolatedStringHandler"/>) at the call site.
/// </para>
/// <para>Lookups are thread-safe. Switch locales on the main thread: scene trees are notified synchronously.</para>
/// </remarks>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores",
    Justification = "Tr._ is the conventional gettext name and the extractor alias.")]
public static class Tr
{
    private const int MaxCachedSourceFormats = 4096;

    private static readonly Lock StateLock = new();
    private static readonly List<WeakReference<SceneTree>> Trees = [];
    private static readonly ConcurrentDictionary<string, CompositeFormat?> SourceFormats = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, CompositeFormat?>.AlternateLookup<ReadOnlySpan<char>> SourceFormatLookup =
        SourceFormats.GetAlternateLookup<ReadOnlySpan<char>>();

    private static LocalizationOptions _options = new();
    private static int _sourceFormatCount;
    private static volatile TranslationSet _set = TranslationSet.Empty(_options.SourceLocale);

    /// <summary>
    /// Raised after the current locale changed (or was reloaded), after the new catalogs are in place. Scene trees
    /// re-translate their nodes on the same call (<see cref="Node.OnLocaleChanged"/>, <see cref="SceneTree.LocaleChanged"/>).
    /// </summary>
    public static event EventHandler<LocaleChangedEventArgs>? LocaleChanged;

    /// <summary>The active options (<see cref="Configure"/>).</summary>
    public static LocalizationOptions Options => _options;

    /// <summary>The current locale in gettext form (<c>pt_BR</c>); the source locale until one is set.</summary>
    public static string CurrentLocale => _set.Locale;

    /// <summary>The formatting culture of <see cref="CurrentLocale"/> (used by the format overloads).</summary>
    public static CultureInfo Culture => _set.Culture;

    /// <summary>The lookup chain of the current locale (<c>pt_BR, pt, en</c>).</summary>
    public static IReadOnlyList<string> LocaleChain => _set.Chain;

    /// <summary>Locales of <see cref="LocaleChain"/> whose catalogs are loaded.</summary>
    public static IReadOnlyList<string> LoadedLocales => _set.LoadedLocales;

    /// <summary>Number of translated messages available in the current chain.</summary>
    public static int MessageCount => _set.Count;

    /// <summary>
    /// Sets the options and loads <paramref name="locale"/>, or the startup locale when null
    /// (<see cref="ResolveStartupLocale"/>: the OS UI language when catalogs exist for it, else the source locale).
    /// The engine calls this from its constructor with <see cref="EngineOptions.Localization"/> and <see cref="EngineOptions.Locale"/>.
    /// </summary>
    public static void Configure(LocalizationOptions options, string? locale = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Domain);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.LocaleDirectory);
        if (LocaleId.Normalize(options.SourceLocale).Length == 0)
            throw new ArgumentException($"Invalid source locale '{options.SourceLocale}'.", nameof(options));

        lock (StateLock)
            _options = options;
        SetLocale(ResolveStartupLocale(locale));
    }

    /// <summary>
    /// Loads the catalogs of <paramref name="locale"/> (<c>es</c>, <c>pt_BR</c> or <c>pt-BR</c>) and its fallback chain,
    /// makes it current and raises <see cref="LocaleChanged"/>. A locale without catalogs is allowed (it shows the
    /// source text, with a warning). Setting the current locale again reloads its catalogs.
    /// </summary>
    public static void SetLocale(string locale)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(locale);
        var normalized = LocaleId.Normalize(locale);
        if (normalized.Length == 0)
            throw new ArgumentException($"Invalid locale name '{locale}'.", nameof(locale));

        string previous;
        TranslationSet next;
        lock (StateLock)
        {
            previous = _set.Locale;
            next = TranslationSet.Load(_options, normalized);
            _set = next;
        }

        if (next.LoadedLocales.Count == 0 && LocaleId.Language(normalized) != LocaleId.Language(_options.SourceLocale))
            Log.Warning($"[L10n] No catalogs for '{normalized}' under {_options.ResolveLocaleDirectory()}; showing source text.");
        else
            Log.Info($"[L10n] Locale '{normalized}' ({next.Count} messages from [{string.Join(", ", next.LoadedLocales)}])");

        RaiseLocaleChanged(previous, normalized);
    }

    /// <summary>Reloads the current locale's catalogs (after recompiling them) and notifies listeners.</summary>
    public static void Reload() => SetLocale(CurrentLocale);

    /// <summary>
    /// The locale to start with: <paramref name="preferred"/> (user settings) if catalogs exist for its chain (itself,
    /// its parents or <see cref="LocalizationOptions.FallbackLocales"/>), else the OS UI language under the same rule,
    /// else the source locale. Invalid names are ignored.
    /// </summary>
    public static string ResolveStartupLocale(string? preferred)
    {
        var options = _options;
        var source = LocaleId.Normalize(options.SourceLocale);
        foreach (var candidate in (ReadOnlySpan<string?>)[preferred, LocaleId.SystemLocale])
        {
            var normalized = LocaleId.Normalize(candidate);
            if (normalized.Length == 0)
                continue;
            if (LocaleId.Language(normalized) == LocaleId.Language(source))
                return normalized;
            foreach (var link in LocaleId.Chain(normalized, source, options.FallbackLocales))
            {
                if (link != source && File.Exists(options.CatalogPath(link)))
                    return normalized;
            }
        }

        return source;
    }

    /// <summary>The locales with a catalog in <see cref="LocalizationOptions.LocaleDirectory"/>, plus the source locale (first).</summary>
    public static IReadOnlyList<string> GetAvailableLocales()
    {
        var options = _options;
        var source = LocaleId.Normalize(options.SourceLocale);
        var locales = new List<string>();
        var directory = options.ResolveLocaleDirectory();
        if (Directory.Exists(directory))
        {
            foreach (var folder in Directory.EnumerateDirectories(directory))
            {
                var locale = LocaleId.Normalize(Path.GetFileName(folder));
                if (locale.Length > 0 && locale != source && File.Exists(options.CatalogPath(locale)))
                    locales.Add(locale);
            }
        }

        locales.Sort(StringComparer.Ordinal);
        locales.Insert(0, source);
        return locales;
    }

    /// <summary>True when the current chain has a translation for the message.</summary>
    public static bool HasTranslation(string text, string? context = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var set = _set;
        return context is null ? set.TryGet(text, out var found) : set.TryGet(context, text, out found);
    }

    // ------------------------------------------------------------------------------------------------
    // _ : plain messages
    // ------------------------------------------------------------------------------------------------

    /// <summary>The translation of <paramref name="text"/>, or <paramref name="text"/> itself. Never formats; never allocates.</summary>
    public static string _(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
            return text;
        var set = _set;
        if (set.TryGet(text, out var entry))
            return entry.Forms[0];
        set.OnMissing(default, text);
        return text;
    }

    /// <summary>Translates the composite format <paramref name="format"/> and formats it with <paramref name="arg0"/> (<c>{0}</c>).</summary>
    public static string _<T0>(string format, T0 arg0) => Message(null, format, new Args1<T0>(arg0));

    /// <summary>Translates <paramref name="format"/> and formats it with two arguments.</summary>
    public static string _<T0, T1>(string format, T0 arg0, T1 arg1) => Message(null, format, new Args2<T0, T1>(arg0, arg1));

    /// <summary>Translates <paramref name="format"/> and formats it with three arguments.</summary>
    public static string _<T0, T1, T2>(string format, T0 arg0, T1 arg1, T2 arg2) =>
        Message(null, format, new Args3<T0, T1, T2>(arg0, arg1, arg2));

    /// <summary>Translates <paramref name="format"/> and formats it with any number of arguments.</summary>
    public static string _(string format, params ReadOnlySpan<object?> args) => Message(null, format, new ArgsSpan(args));

    /// <summary>Translates an interpolated string: <c>Tr._($"Score: {score}")</c> looks up <c>"Score: {0}"</c>.</summary>
    public static string _(ref TrInterpolatedStringHandler text)
    {
        try
        {
            return Message(default, text.Format, null, new ArgsSpan(text.Arguments));
        }
        finally
        {
            text.Release();
        }
    }

    // ------------------------------------------------------------------------------------------------
    // P : messages with a context (msgctxt)
    // ------------------------------------------------------------------------------------------------

    /// <summary>The translation of <paramref name="text"/> in <paramref name="context"/> (gettext <c>pgettext</c>). Never formats.</summary>
    public static string P(string context, string text)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
            return text;
        var set = _set;
        if (set.TryGet(context, text, out var entry))
            return entry.Forms[0];
        set.OnMissing(context, text);
        return text;
    }

    /// <summary>Translates <paramref name="format"/> in <paramref name="context"/> and formats it with one argument.</summary>
    public static string P<T0>(string context, string format, T0 arg0) => Message(context, format, new Args1<T0>(arg0));

    /// <summary>Translates <paramref name="format"/> in <paramref name="context"/> and formats it with two arguments.</summary>
    public static string P<T0, T1>(string context, string format, T0 arg0, T1 arg1) =>
        Message(context, format, new Args2<T0, T1>(arg0, arg1));

    /// <summary>Translates <paramref name="format"/> in <paramref name="context"/> and formats it with any number of arguments.</summary>
    public static string P(string context, string format, params ReadOnlySpan<object?> args) =>
        Message(context, format, new ArgsSpan(args));

    /// <summary>Translates an interpolated string in <paramref name="context"/>.</summary>
    public static string P(string context, ref TrInterpolatedStringHandler text)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            return Message(context, text.Format, null, new ArgsSpan(text.Arguments));
        }
        finally
        {
            text.Release();
        }
    }

    // ------------------------------------------------------------------------------------------------
    // N / NP : plurals
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// The plural form of a message for <paramref name="n"/> (gettext <c>ngettext</c>), formatted with <paramref name="n"/>
    /// as <c>{0}</c>: <c>Tr.N("{0} enemy left", "{0} enemies left", n)</c>. Untranslated: <paramref name="singular"/>
    /// when n is 1, otherwise <paramref name="plural"/>.
    /// </summary>
    public static string N(string singular, string plural, long n) => Plural(null, singular, plural, n, new Args1<long>(n));

    /// <summary><see cref="N(string, string, long)"/> with one more argument (<c>{1}</c>).</summary>
    public static string N<T1>(string singular, string plural, long n, T1 arg1) =>
        Plural(null, singular, plural, n, new Args2<long, T1>(n, arg1));

    /// <summary><see cref="N(string, string, long)"/> with two more arguments (<c>{1}</c>, <c>{2}</c>).</summary>
    public static string N<T1, T2>(string singular, string plural, long n, T1 arg1, T2 arg2) =>
        Plural(null, singular, plural, n, new Args3<long, T1, T2>(n, arg1, arg2));

    /// <summary><see cref="N(string, string, long)"/> with any number of further arguments (from <c>{1}</c>).</summary>
    public static string N(string singular, string plural, long n, params ReadOnlySpan<object?> args) =>
        Plural(null, singular, plural, n, new PluralArgsSpan(n, args));

    /// <summary>Plural of interpolated strings: <c>Tr.N($"{n} file", $"{n} files", n)</c> (the holes are the arguments).</summary>
    public static string N(ref TrInterpolatedStringHandler singular, ref TrInterpolatedStringHandler plural, long n)
    {
        try
        {
            return PluralSpan(default, ref singular, ref plural, n);
        }
        finally
        {
            singular.Release();
            plural.Release();
        }
    }

    /// <summary>A plural message in <paramref name="context"/> (gettext <c>npgettext</c>); <paramref name="n"/> is <c>{0}</c>.</summary>
    public static string NP(string context, string singular, string plural, long n)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Plural(context, singular, plural, n, new Args1<long>(n));
    }

    /// <summary><see cref="NP(string, string, string, long)"/> with one more argument (<c>{1}</c>).</summary>
    public static string NP<T1>(string context, string singular, string plural, long n, T1 arg1)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Plural(context, singular, plural, n, new Args2<long, T1>(n, arg1));
    }

    /// <summary><see cref="NP(string, string, string, long)"/> with any number of further arguments (from <c>{1}</c>).</summary>
    public static string NP(string context, string singular, string plural, long n, params ReadOnlySpan<object?> args)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Plural(context, singular, plural, n, new PluralArgsSpan(n, args));
    }

    /// <summary>Plural of interpolated strings in <paramref name="context"/>.</summary>
    public static string NP(string context, ref TrInterpolatedStringHandler singular, ref TrInterpolatedStringHandler plural, long n)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            return PluralSpan(context, ref singular, ref plural, n);
        }
        finally
        {
            singular.Release();
            plural.Release();
        }
    }

    // ------------------------------------------------------------------------------------------------
    // UI markup (RmlUi TranslateString)
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// The game UI's text hook: translates one RML text run as RmlUi passes it to <c>TranslateString</c>. Returns
    /// false (leave the text unchanged) when the run has no translation. See <see cref="RmlLocalization"/> for the
    /// matching rules (whitespace-collapsed, entity-decoded, exact match) and the <c>no-tr</c> opt-out marker.
    /// </summary>
    public static bool TranslateMarkup(string input, [NotNullWhen(true)] out string? translated)
    {
        ArgumentNullException.ThrowIfNull(input);
        return RmlLocalization.TryTranslate(_set, input.AsSpan(), out translated);
    }

    /// <summary><see cref="TranslateMarkup(string, out string)"/> over UTF-8 text (RmlUi's native callback form).</summary>
    public static bool TranslateMarkup(ReadOnlySpan<byte> utf8, [NotNullWhen(true)] out string? translated) =>
        RmlLocalization.TryTranslateUtf8(_set, utf8, out translated);

    // ------------------------------------------------------------------------------------------------
    // Internals
    // ------------------------------------------------------------------------------------------------

    internal static TranslationSet CurrentSet => _set;

    /// <summary>Formats source text with <paramref name="n"/> as <c>{0}</c> without looking it up (format-safe).</summary>
    internal static string FormatUntranslated(string text, long n) => FormatSource(_set, text, text, new Args1<long>(n));

    /// <summary>Restores the initial state (source locale, default options, no catalogs). Tests only.</summary>
    internal static void ResetForTests()
    {
        lock (StateLock)
        {
            _options = new LocalizationOptions();
            _set = TranslationSet.Empty(_options.SourceLocale);
        }
    }

    /// <summary>Scene trees re-translate their nodes on a locale change; held weakly so dropped trees are collected.</summary>
    internal static void Track(SceneTree tree)
    {
        lock (Trees)
        {
            Trees.RemoveAll(static w => !w.TryGetTarget(out var target));
            Trees.Add(new WeakReference<SceneTree>(tree));
        }
    }

    internal static void Untrack(SceneTree tree)
    {
        lock (Trees)
            Trees.RemoveAll(w => !w.TryGetTarget(out var t) || ReferenceEquals(t, tree));
    }

    /// <summary>
    /// Code reload (<see cref="GameAssemblyLoader.Unload"/>): drops every <see cref="LocaleChanged"/> handler, and every
    /// tracked tree's <see cref="SceneTree.LocaleChanged"/> handler, whose code lives in <paramref name="assembly"/>, so a
    /// forgotten subscription cannot keep an unloaded game assembly alive. Catalogs and cached formats hold only strings.
    /// </summary>
    internal static void ReleaseCodeOf(System.Reflection.Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        lock (StateLock)
            LocaleChanged = CodeReload.Without(LocaleChanged, assembly, "Tr.LocaleChanged");

        SceneTree[] trees;
        lock (Trees)
        {
            var live = new List<SceneTree>(Trees.Count);
            foreach (var weak in Trees)
            {
                if (weak.TryGetTarget(out var tree))
                    live.Add(tree);
            }

            trees = [.. live];
        }

        foreach (var tree in trees)
            tree.ReleaseCodeOf(assembly);
    }

    // A failing listener or node must not leave the others in the old language: each is isolated and logged.
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Listener failures are logged; the remaining listeners and trees must still be notified.")]
    private static void RaiseLocaleChanged(string previous, string locale)
    {
        if (LocaleChanged is { } handlers)
        {
            var args = new LocaleChangedEventArgs(previous, locale);
            foreach (var handler in handlers.GetInvocationList())
            {
                try
                {
                    ((EventHandler<LocaleChangedEventArgs>)handler)(null, args);
                }
                catch (Exception e)
                {
                    Log.Error($"[L10n] LocaleChanged handler {handler.Method.DeclaringType?.Name}.{handler.Method.Name} failed: {e}");
                }
            }
        }

        SceneTree[] trees;
        lock (Trees)
        {
            var live = new List<SceneTree>(Trees.Count);
            foreach (var weak in Trees)
            {
                if (weak.TryGetTarget(out var tree))
                    live.Add(tree);
            }

            trees = [.. live];
        }

        foreach (var tree in trees)
        {
            try
            {
                tree.OnLocaleChanged();
            }
            catch (Exception e)
            {
                Log.Error($"[L10n] Re-translating a scene tree failed: {e}");
            }
        }
    }

    private static string Message<TArgs>(string? context, string format, in TArgs args)
        where TArgs : IFormatArgs, allows ref struct
    {
        ArgumentNullException.ThrowIfNull(format);
        return Message(context, format.AsSpan(), format, args);
    }

    private static string Message<TArgs>(string? context, ReadOnlySpan<char> format, string? formatString, in TArgs args)
        where TArgs : IFormatArgs, allows ref struct
    {
        var set = _set;
        TranslationEntry? entry;
        if (context is null ? set.TryGet(format, out entry) : set.TryGet(context, format, out entry))
            return FormatTranslated(set, context, format, entry, 0, format, formatString, args);

        set.OnMissing(context, format);
        return FormatSource(set, format, formatString, args);
    }

    private static string Plural<TArgs>(string? context, string singular, string plural, long n, in TArgs args)
        where TArgs : IFormatArgs, allows ref struct
    {
        ArgumentNullException.ThrowIfNull(singular);
        ArgumentNullException.ThrowIfNull(plural);
        var set = _set;
        TranslationEntry? entry;
        if (context is null ? set.TryGet(singular, out entry) : set.TryGet(context, singular, out entry))
        {
            var form = entry.FormIndex(n);
            if (entry.Forms[form].Length > 0)
            {
                var source = n == 1 ? singular : plural;
                return FormatTranslated(set, context, singular, entry, form, source, source, args);
            }
        }
        else
        {
            set.OnMissing(context, singular);
        }

        var untranslated = n == 1 ? singular : plural;
        return FormatSource(set, untranslated, untranslated, args);
    }

    private static string PluralSpan(string? context, ref TrInterpolatedStringHandler singular, ref TrInterpolatedStringHandler plural, long n)
    {
        var set = _set;
        TranslationEntry? entry;
        var key = singular.Format;
        if (context is null ? set.TryGet(key, out entry) : set.TryGet(context, key, out entry))
        {
            var form = entry.FormIndex(n);
            if (entry.Forms[form].Length > 0)
            {
                // Form 0 is not "n == 1" in every language (Russian uses it for 21), so translated forms get the
                // larger argument set; the fallback uses the source text matching n with its own arguments.
                var arguments = plural.Arguments.Length >= singular.Arguments.Length ? plural.Arguments : singular.Arguments;
                return n == 1
                    ? FormatTranslated(set, context, key, entry, form, singular.Format, null, new ArgsSpan(arguments))
                    : FormatTranslated(set, context, key, entry, form, plural.Format, null, new ArgsSpan(arguments));
            }
        }
        else
        {
            set.OnMissing(context, key);
        }

        return n == 1
            ? FormatSource(set, singular.Format, null, new ArgsSpan(singular.Arguments))
            : FormatSource(set, plural.Format, null, new ArgsSpan(plural.Arguments));
    }

    /// <summary>
    /// Formats translated form <paramref name="form"/>; on a bad translation logs it under <paramref name="key"/> and
    /// formats <paramref name="source"/> (for plurals: the source text matching n) instead.
    /// </summary>
    private static string FormatTranslated<TArgs>(
        TranslationSet set, string? context, ReadOnlySpan<char> key, TranslationEntry entry, int form,
        ReadOnlySpan<char> source, string? sourceString, in TArgs args)
        where TArgs : IFormatArgs, allows ref struct
    {
        var text = entry.Forms[form];
        var format = entry.GetFormat(form);
        if (format is not null && format.MinimumArgumentCount <= args.Count)
        {
            if (format.MinimumArgumentCount == 0 && text.AsSpan().IndexOfAny('{', '}') < 0)
                return text;
            try
            {
                return args.Format(set.Culture, format);
            }
            catch (FormatException)
            {
                // An argument rejected the translation's format specifier: fall through to the source format.
            }
        }

        set.OnBadFormat(context, key, entry, form);
        return FormatSource(set, source, sourceString, args);
    }

    private static string FormatSource<TArgs>(TranslationSet set, ReadOnlySpan<char> source, string? sourceString, in TArgs args)
        where TArgs : IFormatArgs, allows ref struct
    {
        var format = SourceFormat(source, sourceString);
        if (format is not null && format.MinimumArgumentCount <= args.Count)
        {
            if (format.MinimumArgumentCount == 0 && source.IndexOfAny('{', '}') < 0)
                return sourceString ?? source.ToString();
            try
            {
                return args.Format(set.Culture, format);
            }
            catch (FormatException)
            {
                // The caller's own format is broken; show it unformatted rather than throwing from UI code.
            }
        }

        return sourceString ?? source.ToString();
    }

    private static CompositeFormat? SourceFormat(ReadOnlySpan<char> source, string? sourceString)
    {
        if (SourceFormatLookup.TryGetValue(source, out var cached))
            return cached;

        var text = sourceString ?? source.ToString();
        CompositeFormat? parsed;
        try
        {
            parsed = CompositeFormat.Parse(text);
        }
        catch (FormatException)
        {
            parsed = null;
        }

        if (Volatile.Read(ref _sourceFormatCount) < MaxCachedSourceFormats && SourceFormats.TryAdd(text, parsed))
            Interlocked.Increment(ref _sourceFormatCount);
        return parsed;
    }
}

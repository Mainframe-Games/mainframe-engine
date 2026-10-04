using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using GetText;
using GetText.Loaders;
using GetText.Plural;

namespace MainframeEngine.Localization;

/// <summary>One translated message: its forms (one, or one per plural form) and the plural rule of its catalog.</summary>
internal sealed class TranslationEntry
{
    private readonly CompositeFormat?[] _formats;
    private readonly byte[] _formatState; // 0 = not parsed, 1 = valid, 2 = invalid

    public TranslationEntry(string[] forms, IPluralRule rule, string locale)
    {
        Forms = forms;
        Rule = rule;
        Locale = locale;
        _formats = new CompositeFormat?[forms.Length];
        _formatState = new byte[forms.Length];
    }

    public string[] Forms { get; }

    public IPluralRule Rule { get; }

    /// <summary>The catalog locale the entry came from (a fallback locale when the requested one lacks it).</summary>
    public string Locale { get; }

    /// <summary>The plural form index for <paramref name="n"/>, clamped to the forms present.</summary>
    public int FormIndex(long n)
    {
        if (Forms.Length == 1)
            return 0;
        var index = Rule.Evaluate(n);
        return (uint)index < (uint)Forms.Length ? index : Forms.Length - 1;
    }

    /// <summary>The form as a composite format, or null when it is not a valid format string.</summary>
    public CompositeFormat? GetFormat(int index)
    {
        switch (Volatile.Read(ref _formatState[index]))
        {
            case 1:
                return _formats[index];
            case 2:
                return null;
        }

        CompositeFormat? parsed;
        try
        {
            parsed = CompositeFormat.Parse(Forms[index]);
        }
        catch (FormatException)
        {
            parsed = null;
        }

        // Benign race: two threads parse the same immutable text and store equal results.
        _formats[index] = parsed;
        Volatile.Write(ref _formatState[index], parsed is null ? (byte)2 : (byte)1);
        return parsed;
    }
}

/// <summary>
/// The merged catalogs of one locale chain (<c>pt_BR → pt → …</c>): the first catalog that has a message wins.
/// Immutable once built, so lookups need no lock; <see cref="Tr"/> swaps whole sets on a locale change.
/// Lookups by string or span never allocate.
/// </summary>
internal sealed class TranslationSet
{
    /// <summary>The separator gettext puts between a context and its msgid (<c>"ctx\u0004id"</c>).</summary>
    public const char ContextGlue = Catalog.CONTEXTGLUE;

    private const int StackKeyLimit = 256;

    private readonly Dictionary<string, TranslationEntry> _entries;
    private readonly Dictionary<string, TranslationEntry>.AlternateLookup<ReadOnlySpan<char>> _spanLookup;
    private readonly ConcurrentDictionary<string, byte> _reported = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte>.AlternateLookup<ReadOnlySpan<char>> _reportedLookup;

    private TranslationSet(
        string locale,
        IReadOnlyList<string> chain,
        IReadOnlyList<string> loaded,
        Dictionary<string, TranslationEntry> entries,
        bool reportMissing)
    {
        Locale = locale;
        Culture = LocaleId.GetCulture(locale);
        Chain = chain;
        LoadedLocales = loaded;
        _entries = entries;
        _spanLookup = entries.GetAlternateLookup<ReadOnlySpan<char>>();
        _reportedLookup = _reported.GetAlternateLookup<ReadOnlySpan<char>>();
        ReportMissing = reportMissing;
    }

    /// <summary>The requested locale (normalised).</summary>
    public string Locale { get; }

    /// <summary>Formatting culture for the locale.</summary>
    public CultureInfo Culture { get; }

    /// <summary>The lookup chain, ending with the source locale.</summary>
    public IReadOnlyList<string> Chain { get; }

    /// <summary>Locales of the chain whose catalogs were found and loaded, in chain order.</summary>
    public IReadOnlyList<string> LoadedLocales { get; }

    /// <summary>Whether misses are logged (enabled, and the locale is not the source language).</summary>
    public bool ReportMissing { get; }

    public int Count => _entries.Count;

    /// <summary>An empty set: every lookup returns the msgid.</summary>
    public static TranslationSet Empty(string locale) =>
        new(LocaleId.Normalize(locale), [LocaleId.Normalize(locale)], [], new Dictionary<string, TranslationEntry>(StringComparer.Ordinal), false);

    /// <summary>Loads every catalog of <paramref name="locale"/>'s chain that exists.</summary>
    public static TranslationSet Load(LocalizationOptions options, string locale)
    {
        locale = LocaleId.Normalize(locale);
        var chain = LocaleId.Chain(locale, options.SourceLocale, options.FallbackLocales);
        var entries = new Dictionary<string, TranslationEntry>(StringComparer.Ordinal);
        var loaded = new List<string>(chain.Count);
        foreach (var candidate in chain)
        {
            var path = options.CatalogPath(candidate);
            if (!File.Exists(path))
                continue;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                Merge(entries, stream, candidate);
                loaded.Add(candidate);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or CatalogLoadingException
                                          or FormatException or InvalidDataException or ArgumentException
                                          or OverflowException or GetText.Plural.Ast.ParserException)
            {
                Log.Error($"[L10n] Could not load catalog '{path}': {e.Message}");
            }
        }

        var sourceLanguage = LocaleId.Language(options.SourceLocale);
        var report = options.LogMissingTranslations && LocaleId.Language(locale) != sourceLanguage;
        return new TranslationSet(locale, chain, loaded, entries, report);
    }

    /// <summary>Builds a set from catalogs in memory (tests, tools): <paramref name="catalogs"/> in chain order.</summary>
    public static TranslationSet FromStreams(string locale, IReadOnlyList<(string Locale, Stream Mo)> catalogs, bool reportMissing = false)
    {
        locale = LocaleId.Normalize(locale);
        var entries = new Dictionary<string, TranslationEntry>(StringComparer.Ordinal);
        var loaded = new List<string>();
        foreach (var (catalogLocale, stream) in catalogs)
        {
            Merge(entries, stream, LocaleId.Normalize(catalogLocale));
            loaded.Add(LocaleId.Normalize(catalogLocale));
        }

        return new TranslationSet(locale, [.. loaded], loaded, entries, reportMissing);
    }

    private static void Merge(Dictionary<string, TranslationEntry> entries, Stream stream, string locale)
    {
        // GetText.NET parses the .mo and builds the Plural-Forms rule (falling back to its per-culture rules).
        var catalog = new Catalog(new MoAstPluralLoader(stream), LocaleId.GetCulture(locale));
        foreach (var (key, forms) in catalog.Translations)
        {
            if (key.Length == 0 || forms is null || forms.Length == 0 || forms[0].Length == 0)
                continue; // header entry, or an untranslated message
            entries.TryAdd(key, new TranslationEntry(forms, catalog.PluralRule, locale));
        }
    }

    public bool TryGet(string id, [NotNullWhen(true)] out TranslationEntry? entry) => _entries.TryGetValue(id, out entry);

    public bool TryGet(ReadOnlySpan<char> id, [NotNullWhen(true)] out TranslationEntry? entry) => _spanLookup.TryGetValue(id, out entry);

    /// <summary>Looks up <c>context\u0004id</c> without allocating (the key is composed on the stack or in a pooled buffer).</summary>
    public bool TryGet(ReadOnlySpan<char> context, ReadOnlySpan<char> id, [NotNullWhen(true)] out TranslationEntry? entry)
    {
        if (_entries.Count == 0)
        {
            entry = null;
            return false;
        }

        var length = context.Length + 1 + id.Length;
        char[]? rented = null;
        Span<char> key = length <= StackKeyLimit
            ? stackalloc char[StackKeyLimit]
            : (rented = ArrayPool<char>.Shared.Rent(length));
        try
        {
            context.CopyTo(key);
            key[context.Length] = ContextGlue;
            id.CopyTo(key[(context.Length + 1)..]);
            return _spanLookup.TryGetValue(key[..length], out entry);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<char>.Shared.Return(rented);
        }
    }

    /// <summary>Logs a missing translation once per message (when <see cref="ReportMissing"/>).</summary>
    public void OnMissing(ReadOnlySpan<char> context, ReadOnlySpan<char> id)
    {
        if (!ReportMissing)
            return;
        if (FirstReport(context, id, prefix: '\0'))
        {
            Log.Debug(context.IsEmpty
                ? $"[L10n] Missing translation ({Locale}): \"{id}\""
                : $"[L10n] Missing translation ({Locale}): \"{id}\" (context \"{context}\")");
        }
    }

    /// <summary>Logs a translation whose format string is invalid or needs more arguments than given, once.</summary>
    public void OnBadFormat(ReadOnlySpan<char> context, ReadOnlySpan<char> id, TranslationEntry entry, int form)
    {
        if (FirstReport(context, id, prefix: '\u0001'))
        {
            Log.Warning($"[L10n] Translation ({entry.Locale}) of \"{id}\" is not a valid format for its arguments; " +
                        $"showing the source text. Translation: \"{entry.Forms[form]}\"");
        }
    }

    private bool FirstReport(ReadOnlySpan<char> context, ReadOnlySpan<char> id, char prefix)
    {
        var length = 1 + context.Length + 1 + id.Length;
        char[]? rented = null;
        Span<char> key = length <= StackKeyLimit
            ? stackalloc char[StackKeyLimit]
            : (rented = ArrayPool<char>.Shared.Rent(length));
        try
        {
            key[0] = prefix;
            context.CopyTo(key[1..]);
            key[1 + context.Length] = ContextGlue;
            id.CopyTo(key[(2 + context.Length)..]);
            return _reportedLookup.TryAdd(key[..length], 0);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<char>.Shared.Return(rented);
        }
    }
}

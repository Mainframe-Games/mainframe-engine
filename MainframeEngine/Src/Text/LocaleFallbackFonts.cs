using MainframeEngine.Localization;

namespace MainframeEngine;

/// <summary>
/// The fallback fonts of the current locale for 3D text (G11.2): the faces the project's
/// <see cref="FontFallbackTable"/> (<see cref="FontFallbackTable.DefaultPath"/>) names for <see cref="Tr.LocaleChain"/>,
/// the same table the game UI loads into RmlUi. Resolved again when the locale changes; fonts are loaded once each.
/// </summary>
internal static class LocaleFallbackFonts
{
    private static readonly Dictionary<string, Font> Loaded = new(StringComparer.Ordinal);
    private static readonly List<Font> CurrentFonts = [];
    private static string? _locale;
    private static FontFallbackTable? _table;
    private static bool _tableChecked;

    /// <summary>The current locale's fallback fonts (empty when the project has no table).</summary>
    public static IReadOnlyList<Font> Current
    {
        get
        {
            var locale = Tr.CurrentLocale;
            if (_tableChecked && string.Equals(locale, _locale, StringComparison.Ordinal))
                return CurrentFonts;
            _locale = locale;
            if (!_tableChecked)
            {
                _tableChecked = true;
                try
                {
                    _table = FontFallbackTable.TryLoad();
                }
                catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException or System.Text.Json.JsonException)
                {
                    Log.Warning($"[Font] cannot read {FontFallbackTable.DefaultPath}: {e.Message}");
                }
            }

            CurrentFonts.Clear();
            if (_table is null)
                return CurrentFonts;
            foreach (var path in _table.ResolveCurrent())
            {
                if (!Loaded.TryGetValue(path, out var font))
                {
                    var resolved = ContentPaths.Resolve(path);
                    if (!ContentFiles.Exists(resolved))
                    {
                        Log.Warning($"[Font] fallback font '{path}' not found.");
                        continue;
                    }

                    Loaded[path] = font = Font.FromData(ContentFiles.ReadAllBytes(resolved));
                }

                CurrentFonts.Add(font);
            }

            return CurrentFonts;
        }
    }

    /// <summary>Forgets the table and fonts (tests, project switches).</summary>
    internal static void Reset()
    {
        Loaded.Clear();
        CurrentFonts.Clear();
        _locale = null;
        _table = null;
        _tableChecked = false;
    }
}

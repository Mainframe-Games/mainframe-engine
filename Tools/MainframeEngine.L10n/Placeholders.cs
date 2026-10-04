using System.Globalization;
using System.Text;
using MainframeEngine.L10n.Gettext;

namespace MainframeEngine.L10n;

/// <summary>A problem found in a translation (<c>mf-l10n compile</c>/<c>check</c>).</summary>
internal sealed record Issue(bool IsError, string Message);

/// <summary>
/// Placeholder safety for translations. C# messages (<c>#, csharp-format</c>) must be valid composite formats that use
/// no argument index the source does not have — the runtime would fall back to the source text otherwise. RML text with
/// <c>{{ data expressions }}</c> must keep exactly the source's expressions, or the UI would bind different data.
/// </summary>
internal static class Placeholders
{
    public static List<Issue> Check(PoEntry entry, int? pluralCount)
    {
        var issues = new List<Issue>();
        if (entry.IsHeader || entry.Obsolete || !entry.HasTranslation)
            return issues;
        var where = $"line {entry.Line}: \"{Shorten(entry.Id)}\"";

        if (entry.IsPlural && pluralCount is { } expected && entry.Translations.Count != expected)
            issues.Add(new Issue(true, $"{where}: {entry.Translations.Count} plural forms, the Plural-Forms header says {expected}"));

        var isCSharp = entry.Flags.Contains("csharp-format");
        var csharp = isCSharp;
        var sourceIndices = new HashSet<int>();
        if (csharp)
        {
            if (!TryGetIndices(entry.Id, sourceIndices, out var error)
                || (entry.IdPlural is not null && !TryGetIndices(entry.IdPlural, sourceIndices, out error)))
            {
                issues.Add(new Issue(false, $"{where}: the source is not a valid format string ({error})"));
                csharp = false;
            }
        }

        var sourceExpressions = DataExpressions(entry.Id);
        for (var form = 0; form < entry.Translations.Count; form++)
        {
            var translation = entry.Translations[form];
            if (translation.Length == 0)
                continue;
            var label = entry.IsPlural ? $"msgstr[{form}]" : "msgstr";
            if (csharp)
            {
                var used = new HashSet<int>();
                if (!TryGetIndices(translation, used, out var error))
                {
                    issues.Add(new Issue(true, $"{where}: {label} is not a valid format string ({error})"));
                    continue;
                }

                foreach (var index in used)
                {
                    if (!sourceIndices.Contains(index))
                        issues.Add(new Issue(true, $"{where}: {label} uses {{{index}}}, which the source does not have"));
                }

                // A plural form may legitimately drop {0} ("one" → "a single file"), so missing indices only warn.
                if (!entry.IsPlural)
                {
                    foreach (var index in sourceIndices)
                    {
                        if (!used.Contains(index))
                            issues.Add(new Issue(false, $"{where}: {label} does not use {{{index}}}"));
                    }
                }
            }

            // In C# formats "{{" is an escaped brace, not a data expression.
            if (!isCSharp && (sourceExpressions.Count > 0 || translation.Contains("{{", StringComparison.Ordinal)))
            {
                var translated = DataExpressions(translation);
                if (!translated.SetEquals(sourceExpressions))
                    issues.Add(new Issue(true, $"{where}: {label} must keep the data expressions {string.Join(", ", sourceExpressions)}"));
            }
        }

        return issues;
    }

    /// <summary>Collects the argument indices of a .NET composite format; false (with a reason) when it is malformed.</summary>
    public static bool TryGetIndices(string format, HashSet<int> indices, out string error)
    {
        error = string.Empty;
        var i = 0;
        while (i < format.Length)
        {
            var c = format[i];
            if (c == '}')
            {
                if (i + 1 < format.Length && format[i + 1] == '}')
                {
                    i += 2;
                    continue;
                }

                error = $"unmatched '}}' at {i}";
                return false;
            }

            if (c != '{')
            {
                i++;
                continue;
            }

            if (i + 1 < format.Length && format[i + 1] == '{')
            {
                i += 2;
                continue;
            }

            var close = format.IndexOf('}', i + 1);
            if (close < 0)
            {
                error = $"unclosed '{{' at {i}";
                return false;
            }

            var item = format.AsSpan(i + 1, close - i - 1);
            var cut = item.IndexOfAny(',', ':');
            var number = (cut < 0 ? item : item[..cut]).Trim();
            if (!int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
            {
                error = $"'{{{item}}}' is not a format item";
                return false;
            }

            indices.Add(index);
            i = close + 1;
        }

        return true;
    }

    /// <summary>The RmlUi data expressions (<c>{{ … }}</c>, whitespace-insensitive) in a string.</summary>
    public static HashSet<string> DataExpressions(string text)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var i = 0;
        while (i < text.Length)
        {
            var open = text.IndexOf("{{", i, StringComparison.Ordinal);
            if (open < 0)
                break;
            var close = text.IndexOf("}}", open + 2, StringComparison.Ordinal);
            if (close < 0)
                break;
            var sb = new StringBuilder();
            foreach (var c in text.AsSpan(open + 2, close - open - 2))
            {
                if (!char.IsWhiteSpace(c))
                    sb.Append(c);
            }

            set.Add(sb.ToString());
            i = close + 2;
        }

        return set;
    }

    private static string Shorten(string text) => text.Length <= 50 ? text : text[..50] + "…";
}

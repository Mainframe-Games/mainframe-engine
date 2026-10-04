using System.Globalization;
using System.Text;

namespace MainframeEngine.L10n.Gettext;

/// <summary>A syntax error in a <c>.po</c> file.</summary>
internal sealed class PoFormatException(string file, int line, string message)
    : FormatException($"{file}:{line}: {message}")
{
    public string File { get; } = file;

    public int LineNumber { get; } = line;
}

/// <summary>
/// Reads GNU <c>.po</c>/<c>.pot</c> files: comments (<c>#</c>, <c>#.</c>, <c>#:</c>, <c>#,</c>, <c>#|</c>), obsolete
/// entries (<c>#~</c>), <c>msgctxt</c>, <c>msgid</c>, <c>msgid_plural</c>, <c>msgstr</c>/<c>msgstr[n]</c>, multi-line
/// strings and C escapes. UTF-8 only (the header's charset must be UTF-8 or ASCII).
/// </summary>
internal static class PoParser
{
    private enum Target
    {
        None,
        Context,
        Id,
        IdPlural,
        Translation,
    }

    public static PoCatalog ParseFile(string path) => Parse(File.ReadAllText(path, Encoding.UTF8), path);

    public static PoCatalog Parse(string text, string fileName = "<po>")
    {
        ArgumentNullException.ThrowIfNull(text);
        var catalog = new PoCatalog();
        var lines = text.Split('\n');
        PoEntry? entry = null;
        var target = Target.None;
        var translationIndex = 0;
        var hasKeywords = false; // the current entry has msgid/msgstr lines (not only comments)
        var sawTranslation = false;
        var lineNumber = 0;

        void Finish()
        {
            if (entry is not null && hasKeywords)
            {
                if (!sawTranslation)
                    throw new PoFormatException(fileName, lineNumber, $"message \"{Shorten(entry.Id)}\" has no msgstr");
                if (entry.IsHeader && !entry.Obsolete && catalog.Entries.Count == 0)
                {
                    // "# Spanish translation of …" above the header describes the file.
                    catalog.FileComments.AddRange(entry.TranslatorComments);
                    entry.TranslatorComments.Clear();
                }

                catalog.Entries.Add(entry);
            }
            else if (entry is not null && catalog.Entries.Count == 0)
            {
                // Comments before the first message (and not attached to one) are the file's leading comments.
                foreach (var c in entry.TranslatorComments)
                    catalog.FileComments.Add(c);
            }

            entry = null;
            target = Target.None;
            hasKeywords = false;
            sawTranslation = false;
        }

        foreach (var rawLine in lines)
        {
            lineNumber++;
            var line = rawLine.TrimEnd('\r');
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                Finish();
                continue;
            }

            var obsolete = false;
            if (trimmed.StartsWith("#~", StringComparison.Ordinal))
            {
                obsolete = true;
                trimmed = trimmed[2..].TrimStart();
                if (trimmed.StartsWith('|'))
                    continue; // previous-msgid of an obsolete entry: dropped
                if (trimmed.Length == 0)
                    continue;
            }

            if (!obsolete && trimmed[0] == '#')
            {
                if (hasKeywords && sawTranslation)
                    Finish();
                entry ??= new PoEntry();
                var kind = trimmed.Length > 1 ? trimmed[1] : ' ';
                var body = trimmed.Length > 2 ? trimmed[2..].Trim() : string.Empty;
                switch (kind)
                {
                    case '.':
                        entry.ExtractedComments.Add(body);
                        break;
                    case ':':
                        entry.References.AddRange(body.Split(' ', StringSplitOptions.RemoveEmptyEntries));
                        break;
                    case ',':
                        foreach (var flag in body.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            if (!entry.Flags.Contains(flag))
                                entry.Flags.Add(flag);
                        }

                        break;
                    case '|':
                        entry.PreviousLines.Add(body);
                        break;
                    default:
                        // "# comment" or "#comment"
                        entry.TranslatorComments.Add(trimmed.Length > 1 && trimmed[1] == ' ' ? trimmed[2..] : trimmed[1..]);
                        break;
                }

                continue;
            }

            if (trimmed[0] == '"')
            {
                if (target == Target.None || entry is null)
                    throw new PoFormatException(fileName, lineNumber, "string continuation without a keyword");
                Append(entry, target, translationIndex, ParseString(trimmed, fileName, lineNumber));
                continue;
            }

            var keywordEnd = trimmed.IndexOfAny([' ', '\t', '"']);
            var keyword = keywordEnd < 0 ? trimmed : trimmed[..keywordEnd];
            var rest = keywordEnd < 0 ? string.Empty : trimmed[keywordEnd..].Trim();
            if (rest.Length == 0 || rest[0] != '"')
                throw new PoFormatException(fileName, lineNumber, $"expected a string after '{keyword}'");
            var value = ParseString(rest, fileName, lineNumber);

            if (keyword is "msgctxt" or "msgid" && sawTranslation)
                Finish(); // a new message without a blank line in between
            if (keyword is "msgctxt" && entry is not null && hasKeywords && !sawTranslation)
                throw new PoFormatException(fileName, lineNumber, "msgctxt after msgid");

            entry ??= new PoEntry();
            entry.Obsolete |= obsolete;
            hasKeywords = true;
            switch (keyword)
            {
                case "msgctxt":
                    entry.Context = value;
                    target = Target.Context;
                    break;
                case "msgid":
                    entry.Id = value;
                    entry.Line = lineNumber;
                    target = Target.Id;
                    break;
                case "msgid_plural":
                    entry.IdPlural = value;
                    target = Target.IdPlural;
                    break;
                case "msgstr":
                    if (entry.IsPlural)
                        throw new PoFormatException(fileName, lineNumber, "plural message needs msgstr[n]");
                    entry.Translations.Add(value);
                    translationIndex = entry.Translations.Count - 1;
                    target = Target.Translation;
                    sawTranslation = true;
                    break;
                default:
                    if (keyword.StartsWith("msgstr[", StringComparison.Ordinal) && keyword.EndsWith(']')
                        && int.TryParse(keyword.AsSpan(7, keyword.Length - 8), NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                    {
                        if (!entry.IsPlural)
                            throw new PoFormatException(fileName, lineNumber, "msgstr[n] without msgid_plural");
                        if (index != entry.Translations.Count)
                            throw new PoFormatException(fileName, lineNumber, $"expected msgstr[{entry.Translations.Count}]");
                        entry.Translations.Add(value);
                        translationIndex = index;
                        target = Target.Translation;
                        sawTranslation = true;
                        break;
                    }

                    throw new PoFormatException(fileName, lineNumber, $"unknown keyword '{keyword}'");
            }
        }

        Finish();
        CheckCharset(catalog, fileName);
        return catalog;
    }

    private static void Append(PoEntry entry, Target target, int translationIndex, string value)
    {
        switch (target)
        {
            case Target.Context:
                entry.Context += value;
                break;
            case Target.Id:
                entry.Id += value;
                break;
            case Target.IdPlural:
                entry.IdPlural += value;
                break;
            case Target.Translation:
                entry.Translations[translationIndex] += value;
                break;
        }
    }

    private static void CheckCharset(PoCatalog catalog, string fileName)
    {
        var contentType = catalog.GetHeader("Content-Type");
        if (contentType is null)
            return;
        var at = contentType.IndexOf("charset=", StringComparison.OrdinalIgnoreCase);
        if (at < 0)
            return;
        var charset = contentType[(at + 8)..].Trim().TrimEnd(';').Trim();
        if (charset.Length == 0 || charset.Equals("CHARSET", StringComparison.Ordinal))
            return; // template placeholder
        if (!charset.Equals("UTF-8", StringComparison.OrdinalIgnoreCase) && !charset.Equals("ASCII", StringComparison.OrdinalIgnoreCase)
            && !charset.Equals("US-ASCII", StringComparison.OrdinalIgnoreCase))
        {
            throw new PoFormatException(fileName, catalog.Header?.Line ?? 1,
                $"charset '{charset}' is not supported; save the file as UTF-8 (charset=UTF-8)");
        }
    }

    /// <summary>Parses one C-style quoted string (<c>"…"</c>), resolving escapes.</summary>
    public static string ParseString(string text, string fileName = "<po>", int lineNumber = 0)
    {
        if (text.Length < 2 || text[0] != '"')
            throw new PoFormatException(fileName, lineNumber, "expected a quoted string");
        var sb = new StringBuilder(text.Length);
        var i = 1;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '"')
            {
                if (text.AsSpan(i + 1).Trim().Length != 0)
                    throw new PoFormatException(fileName, lineNumber, "unexpected text after the closing quote");
                return sb.ToString();
            }

            if (c != '\\')
            {
                sb.Append(c);
                i++;
                continue;
            }

            if (i + 1 >= text.Length)
                break;
            var e = text[i + 1];
            i += 2;
            switch (e)
            {
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case 'r': sb.Append('\r'); break;
                case 'a': sb.Append('\a'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'v': sb.Append('\v'); break;
                case '\\': sb.Append('\\'); break;
                case '"': sb.Append('"'); break;
                case '\'': sb.Append('\''); break;
                case '?': sb.Append('?'); break;
                case 'x':
                    {
                        var start = i;
                        while (i < text.Length && i - start < 2 && char.IsAsciiHexDigit(text[i]))
                            i++;
                        if (i == start)
                            throw new PoFormatException(fileName, lineNumber, "\\x needs hex digits");
                        sb.Append((char)int.Parse(text.AsSpan(start, i - start), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture));
                        break;
                    }

                case >= '0' and <= '7':
                    {
                        var start = i - 1;
                        while (i < text.Length && i - start < 3 && text[i] is >= '0' and <= '7')
                            i++;
                        var value = 0;
                        foreach (var d in text.AsSpan(start, i - start))
                            value = value * 8 + (d - '0');
                        sb.Append((char)value);
                        break;
                    }

                default:
                    throw new PoFormatException(fileName, lineNumber, $"unknown escape '\\{e}'");
            }
        }

        throw new PoFormatException(fileName, lineNumber, "unterminated string");
    }

    private static string Shorten(string text) => text.Length <= 40 ? text : text[..40] + "…";
}

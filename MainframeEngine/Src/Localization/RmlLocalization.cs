using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace MainframeEngine.Localization;

/// <summary>Where a translatable string sits in an RML document.</summary>
public enum RmlTextKind
{
    /// <summary>A text node (the text between two tags).</summary>
    Text,

    /// <summary>An attribute value (<c>title</c>, <c>placeholder</c>, or <c>value</c> of a submit/button input).</summary>
    Attribute,
}

/// <summary>
/// One translatable string found by <see cref="RmlLocalization.Scan"/>.
/// </summary>
/// <param name="Kind">Text node or attribute.</param>
/// <param name="Text">The msgid: entity-decoded, whitespace collapsed to single spaces and trimmed.</param>
/// <param name="Line">1-based line of the first non-blank character (for <c>#:</c> references).</param>
/// <param name="Start">Start of the raw text (or raw attribute value, inside the quotes) in the source.</param>
/// <param name="Length">Length of the raw text or value in the source.</param>
/// <param name="Element">The element the text belongs to (lowercase).</param>
/// <param name="Attribute">The attribute name for <see cref="RmlTextKind.Attribute"/>.</param>
/// <param name="OptedOut">Inside an element with <c>class="no-tr"</c> (or a descendant of one): never translated.</param>
public readonly record struct RmlTextRun(
    RmlTextKind Kind,
    string Text,
    int Line,
    int Start,
    int Length,
    string Element,
    string? Attribute,
    bool OptedOut);

/// <summary>
/// Localization of RmlUi documents: one parser shared by the extractor (<c>mf-l10n</c>) and the runtime, so the msgids
/// translators see are exactly the keys the game looks up.
/// </summary>
/// <remarks>
/// <para><b>What is translated.</b> Text nodes and the <c>title</c> and <c>placeholder</c> attributes of any element,
/// and <c>value</c> of <c>&lt;input type="submit|button"&gt;</c>. Content of <c>head</c>, <c>style</c>, <c>script</c>
/// and <c>textarea</c>, comments and CDATA are not. A run is a candidate only when it has a letter outside
/// <c>{{ data expressions }}</c> (so <c>{{score}}</c> or <c>100%</c> are not extracted or looked up).</para>
/// <para><b>Matching</b> is exact on the normalised text: entities decoded (<c>&amp;amp; &amp;lt; &amp;gt; &amp;quot;
/// &amp;apos; &amp;nbsp;</c>, numeric), runs of spaces/tabs/newlines collapsed to one space, trimmed. Inline markup
/// splits a paragraph into separate runs (<c>Press &lt;b&gt;Start&lt;/b&gt; now</c> → <c>"Press"</c>, <c>"Start"</c>,
/// <c>"now"</c>), as RmlUi translates each text node on its own. Translations are plain text: they are entity-encoded
/// on the way out, so a translation can never inject markup.</para>
/// <para><b>Opt-out.</b> Elements with the class <c>no-tr</c> (and everything inside them) are neither extracted nor
/// translated. RmlUi's <c>TranslateString</c> callback only sees the text, so <see cref="PrepareDocument(string)"/>
/// (run on every document source before RmlUi parses it) prefixes opted-out text nodes with
/// <see cref="OptOutMarker"/>, which <see cref="Tr.TranslateMarkup(string, out string)"/> strips without
/// translating. The same pass translates the attributes, which RmlUi does not send through <c>TranslateString</c>.</para>
/// </remarks>
public static class RmlLocalization
{
    /// <summary>The class that opts an element and its descendants out of translation.</summary>
    public const string OptOutClass = "no-tr";

    /// <summary>
    /// Prefix that <see cref="PrepareDocument(string)"/> puts on opted-out text nodes (U+FDD0, a Unicode noncharacter
    /// reserved for internal use, so it never occurs in real text). <see cref="Tr.TranslateMarkup(string, out string)"/>
    /// removes it and reports the text as handled, so it is never rendered.
    /// </summary>
    public const char OptOutMarker = '﷐';

    private const int StackLimit = 512;

    private static readonly string[] SkippedElements = ["head", "style", "script", "textarea"];

    /// <summary>
    /// Finds every translatable run in an RML document, in source order, including opted-out ones
    /// (<see cref="RmlTextRun.OptedOut"/>; the extractor skips them).
    /// </summary>
    public static List<RmlTextRun> Scan(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var runs = new List<RmlTextRun>();
        var stack = new List<(string Name, bool OptedOut)>();
        var skipDepth = 0; // > 0 inside head/style/script/textarea
        var lineCounter = new LineCounter(source);
        var i = 0;
        while (i < source.Length)
        {
            if (source[i] != '<')
            {
                var end = source.IndexOf('<', i);
                if (end < 0)
                    end = source.Length;
                if (skipDepth == 0 && stack.Count > 0)
                    AddTextRun(runs, source, i, end - i, stack[^1], ref lineCounter);
                i = end;
                continue;
            }

            if (At(source, i, "<!--"))
            {
                i = SkipPast(source, i + 4, "-->");
                continue;
            }

            if (At(source, i, "<![CDATA["))
            {
                i = SkipPast(source, i + 9, "]]>");
                continue;
            }

            if (At(source, i, "<!") || At(source, i, "<?"))
            {
                i = SkipPast(source, i + 2, ">");
                continue;
            }

            if (At(source, i, "</"))
            {
                var nameStart = i + 2;
                var nameEnd = NameEnd(source, nameStart);
                var name = source[nameStart..nameEnd].ToLowerInvariant();
                i = SkipPast(source, nameEnd, ">");
                for (var s = stack.Count - 1; s >= 0; s--)
                {
                    if (stack[s].Name != name)
                        continue;
                    for (var p = stack.Count - 1; p >= s; p--)
                    {
                        if (IsSkipped(stack[p].Name))
                            skipDepth--;
                        stack.RemoveAt(p);
                    }

                    break;
                }

                continue;
            }

            i = ReadStartTag(source, i, runs, stack, ref skipDepth, ref lineCounter);
        }

        return runs;
    }

    /// <summary>
    /// Prepares a document source for RmlUi in the current locale (<see cref="Tr"/>): translates the translatable
    /// attributes and marks opted-out text nodes with <see cref="OptOutMarker"/>. Text nodes are left to RmlUi's
    /// <c>TranslateString</c> → <see cref="Tr.TranslateMarkup(string, out string)"/>. Returns <paramref name="source"/>
    /// itself when nothing changes. Run it again after <see cref="Tr.LocaleChanged"/> (documents are reloaded).
    /// </summary>
    public static string PrepareDocument(string source) => PrepareDocument(Tr.CurrentSet, source);

    internal static string PrepareDocument(TranslationSet set, string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        StringBuilder? sb = null;
        var copied = 0;
        foreach (var run in Scan(source))
        {
            if (run.Kind == RmlTextKind.Text)
            {
                if (!run.OptedOut)
                    continue;
                sb ??= new StringBuilder(source.Length + 16);
                sb.Append(source, copied, run.Start - copied).Append(OptOutMarker);
                copied = run.Start;
                continue;
            }

            if (run.OptedOut)
                continue;
            if (!set.TryGet(run.Text, out var entry))
            {
                set.OnMissing(default, run.Text);
                continue;
            }

            var quote = run.Start > 0 ? source[run.Start - 1] : '"';
            sb ??= new StringBuilder(source.Length + 16);
            sb.Append(source, copied, run.Start - copied);
            AppendEncoded(sb, entry.Forms[0], quote is '\'' ? '\'' : '"');
            copied = run.Start + run.Length;
        }

        if (sb is null)
            return source;
        sb.Append(source, copied, source.Length - copied);
        return sb.ToString();
    }

    /// <summary>The msgid form of raw RML text: entities decoded, whitespace collapsed, trimmed.</summary>
    public static string Normalize(ReadOnlySpan<char> raw)
    {
        char[]? rented = null;
        Span<char> buffer = raw.Length <= StackLimit ? stackalloc char[StackLimit] : (rented = ArrayPool<char>.Shared.Rent(raw.Length));
        try
        {
            var length = Normalize(raw, buffer);
            return new string(buffer[..length]);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<char>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Writes the normalised form of <paramref name="raw"/> to <paramref name="destination"/> (at least as long as
    /// <paramref name="raw"/>; the result is never longer) and returns its length.
    /// </summary>
    public static int Normalize(ReadOnlySpan<char> raw, Span<char> destination)
    {
        var length = 0;
        var pendingSpace = false;
        var i = 0;
        Span<char> decoded = stackalloc char[2];
        while (i < raw.Length)
        {
            var c = raw[i];
            if (c is ' ' or '\t' or '\n' or '\r' or '\f')
            {
                pendingSpace = length > 0;
                i++;
                continue;
            }

            int decodedLength;
            if (c == '&' && TryDecodeEntity(raw[i..], decoded, out var consumed, out decodedLength))
            {
                i += consumed;
            }
            else
            {
                decoded[0] = c;
                decodedLength = 1;
                i++;
            }

            if (pendingSpace)
            {
                destination[length++] = ' ';
                pendingSpace = false;
            }

            for (var d = 0; d < decodedLength; d++)
                destination[length++] = decoded[d];
        }

        return length;
    }

    /// <summary>
    /// True when normalised text is worth translating: it has a letter outside <c>{{ … }}</c> data expressions.
    /// </summary>
    public static bool IsTranslatable(ReadOnlySpan<char> text)
    {
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == '{' && i + 1 < text.Length && text[i + 1] == '{')
            {
                var close = text[(i + 2)..].IndexOf("}}");
                if (close < 0)
                    return false;
                i += close + 4;
                continue;
            }

            if (char.IsLetter(text[i]))
                return true;
            i++;
        }

        return false;
    }

    /// <summary>Entity-encodes plain text for RML text content (<c>&amp; &lt; &gt;</c>).</summary>
    public static string EncodeText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.AsSpan().IndexOfAny('&', '<', '>') < 0)
            return text;
        var sb = new StringBuilder(text.Length + 8);
        AppendEncoded(sb, text, '\0');
        return sb.ToString();
    }

    internal static bool TryTranslate(TranslationSet set, ReadOnlySpan<char> input, [NotNullWhen(true)] out string? translated)
    {
        if (input.Length > 0 && input[0] == OptOutMarker)
        {
            translated = input[1..].ToString();
            return true;
        }

        translated = null;
        if (input.IsEmpty || (set.Count == 0 && !set.ReportMissing))
            return false;

        char[]? rented = null;
        Span<char> buffer = input.Length <= StackLimit ? stackalloc char[StackLimit] : (rented = ArrayPool<char>.Shared.Rent(input.Length));
        try
        {
            var key = buffer[..Normalize(input, buffer)];
            if (!IsTranslatable(key))
                return false;
            if (!set.TryGet(key, out var entry))
            {
                set.OnMissing(default, key);
                return false;
            }

            var sb = new StringBuilder(entry.Forms[0].Length + 8);
            if (IsBlank(input[0]))
                sb.Append(' ');
            AppendEncoded(sb, entry.Forms[0], '\0');
            if (IsBlank(input[^1]))
                sb.Append(' ');
            translated = sb.ToString();
            return true;
        }
        finally
        {
            if (rented is not null)
                ArrayPool<char>.Shared.Return(rented);
        }
    }

    internal static bool TryTranslateUtf8(TranslationSet set, ReadOnlySpan<byte> utf8, [NotNullWhen(true)] out string? translated)
    {
        if (utf8.IsEmpty)
        {
            translated = null;
            return false;
        }

        char[]? rented = null;
        Span<char> chars = utf8.Length <= StackLimit ? stackalloc char[StackLimit] : (rented = ArrayPool<char>.Shared.Rent(utf8.Length));
        try
        {
            var count = Encoding.UTF8.GetChars(utf8, chars);
            return TryTranslate(set, chars[..count], out translated);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<char>.Shared.Return(rented);
        }
    }

    // ------------------------------------------------------------------------------------------------
    // Parsing
    // ------------------------------------------------------------------------------------------------

    private static int ReadStartTag(
        string source, int start, List<RmlTextRun> runs, List<(string Name, bool OptedOut)> stack, ref int skipDepth, ref LineCounter lines)
    {
        var nameStart = start + 1;
        var nameEnd = NameEnd(source, nameStart);
        if (nameEnd == nameStart)
        {
            // A lone '<' in text: RmlUi would reject the document; treat the character as text.
            if (skipDepth == 0 && stack.Count > 0)
            {
                var end = source.IndexOf('<', start + 1);
                end = end < 0 ? source.Length : end;
                AddTextRun(runs, source, start, end - start, stack[^1], ref lines);
                return end;
            }

            return start + 1;
        }

        var name = source[nameStart..nameEnd].ToLowerInvariant();
        var attributes = new List<(string Name, int Start, int Length)>();
        var i = nameEnd;
        var selfClosing = false;
        while (i < source.Length)
        {
            var c = source[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c == '>')
            {
                i++;
                break;
            }

            if (c == '/' && i + 1 < source.Length && source[i + 1] == '>')
            {
                selfClosing = true;
                i += 2;
                break;
            }

            var attrStart = i;
            while (i < source.Length && !char.IsWhiteSpace(source[i]) && source[i] is not ('=' or '>' or '/'))
                i++;
            if (i == attrStart)
            {
                i++; // stray character such as a lone '/'
                continue;
            }

            var attrName = source[attrStart..i].ToLowerInvariant();
            while (i < source.Length && char.IsWhiteSpace(source[i]))
                i++;
            if (i >= source.Length || source[i] != '=')
            {
                attributes.Add((attrName, i, 0));
                continue;
            }

            i++;
            while (i < source.Length && char.IsWhiteSpace(source[i]))
                i++;
            if (i < source.Length && source[i] is '"' or '\'')
            {
                var quote = source[i];
                var valueStart = i + 1;
                var valueEnd = source.IndexOf(quote, valueStart);
                if (valueEnd < 0)
                    valueEnd = source.Length;
                attributes.Add((attrName, valueStart, valueEnd - valueStart));
                i = Math.Min(valueEnd + 1, source.Length);
            }
            else
            {
                var valueStart = i;
                while (i < source.Length && !char.IsWhiteSpace(source[i]) && source[i] != '>')
                    i++;
                attributes.Add((attrName, valueStart, i - valueStart));
            }
        }

        var parentOptedOut = stack.Count > 0 && stack[^1].OptedOut;
        var optedOut = parentOptedOut || HasOptOutClass(source, attributes);
        if (skipDepth == 0 && name is not ("head" or "style" or "script" or "textarea"))
        {
            foreach (var (attrName, valueStart, valueLength) in attributes)
            {
                if (!IsTranslatableAttribute(name, attrName, source, attributes))
                    continue;
                var text = Normalize(source.AsSpan(valueStart, valueLength));
                if (!IsTranslatable(text))
                    continue;
                runs.Add(new RmlTextRun(RmlTextKind.Attribute, text, lines.LineAt(valueStart), valueStart, valueLength,
                    name, attrName, optedOut));
            }
        }

        if (selfClosing)
            return i;

        // Raw-text elements: their content is never markup, so jump straight to the end tag.
        if (name is "style" or "script")
        {
            var close = source.IndexOf("</" + name, i, StringComparison.OrdinalIgnoreCase);
            return close < 0 ? source.Length : close;
        }

        stack.Add((name, optedOut));
        if (IsSkipped(name))
            skipDepth++;
        return i;
    }

    private static void AddTextRun(List<RmlTextRun> runs, string source, int start, int length, (string Name, bool OptedOut) parent, ref LineCounter lines)
    {
        var raw = source.AsSpan(start, length);
        var firstVisible = 0;
        while (firstVisible < raw.Length && IsBlank(raw[firstVisible]))
            firstVisible++;
        if (firstVisible == raw.Length)
            return;
        var text = Normalize(raw);
        if (!IsTranslatable(text))
            return;
        runs.Add(new RmlTextRun(RmlTextKind.Text, text, lines.LineAt(start + firstVisible), start, length, parent.Name, null, parent.OptedOut));
    }

    private static bool IsTranslatableAttribute(string element, string attribute, string source, List<(string Name, int Start, int Length)> attributes)
    {
        switch (attribute)
        {
            case "title":
            case "placeholder":
                return true;
            case "value" when element == "input":
                foreach (var (name, start, length) in attributes)
                {
                    if (name != "type")
                        continue;
                    var type = source.AsSpan(start, length).Trim();
                    return type.Equals("submit", StringComparison.OrdinalIgnoreCase) || type.Equals("button", StringComparison.OrdinalIgnoreCase);
                }

                return false;
            default:
                return false;
        }
    }

    private static bool HasOptOutClass(string source, List<(string Name, int Start, int Length)> attributes)
    {
        foreach (var (name, start, length) in attributes)
        {
            if (name != "class")
                continue;
            foreach (var range in source.AsSpan(start, length).SplitAny(" \t\r\n\f"))
            {
                if (source.AsSpan(start, length)[range].SequenceEqual(OptOutClass))
                    return true;
            }
        }

        return false;
    }

    private static bool IsSkipped(string element) => Array.IndexOf(SkippedElements, element) >= 0;

    private static bool IsBlank(char c) => c is ' ' or '\t' or '\n' or '\r' or '\f';

    private static bool At(string source, int index, string token) =>
        string.CompareOrdinal(source, index, token, 0, token.Length) == 0;

    private static int SkipPast(string source, int from, string terminator)
    {
        var end = source.IndexOf(terminator, from, StringComparison.Ordinal);
        return end < 0 ? source.Length : end + terminator.Length;
    }

    private static int NameEnd(string source, int start)
    {
        var i = start;
        while (i < source.Length && (char.IsLetterOrDigit(source[i]) || source[i] is '-' or '_' or ':' or '.'))
            i++;
        return i;
    }

    private static bool TryDecodeEntity(ReadOnlySpan<char> text, Span<char> decoded, out int consumed, out int length)
    {
        consumed = length = 0;
        var semicolon = text[..Math.Min(text.Length, 12)].IndexOf(';');
        if (semicolon < 2)
            return false;
        var name = text[1..semicolon];
        consumed = semicolon + 1;
        length = 1;
        switch (name)
        {
            case "lt": decoded[0] = '<'; return true;
            case "gt": decoded[0] = '>'; return true;
            case "amp": decoded[0] = '&'; return true;
            case "quot": decoded[0] = '"'; return true;
            case "apos": decoded[0] = '\''; return true;
            case "nbsp": decoded[0] = ' '; return true;
        }

        if (name[0] != '#')
            return false;
        var hex = name.Length > 1 && name[1] is 'x' or 'X';
        var digits = hex ? name[2..] : name[1..];
        if (!int.TryParse(digits, hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None, CultureInfo.InvariantCulture, out var code)
            || !Rune.TryCreate(code, out var rune))
        {
            return false;
        }

        length = rune.EncodeToUtf16(decoded);
        return true;
    }

    private static void AppendEncoded(StringBuilder sb, string text, char quote)
    {
        foreach (var c in text)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"' when quote == '"': sb.Append("&quot;"); break;
                case '\'' when quote == '\'': sb.Append("&#39;"); break;
                default: sb.Append(c); break;
            }
        }
    }

    /// <summary>Line numbers for increasing source offsets in one pass.</summary>
    private struct LineCounter(string source)
    {
        private int _position;
        private int _line = 1;

        public int LineAt(int offset)
        {
            if (offset < _position)
            {
                _position = 0;
                _line = 1;
            }

            for (; _position < offset && _position < source.Length; _position++)
            {
                if (source[_position] == '\n')
                    _line++;
            }

            return _line;
        }
    }
}

using System.Text;

namespace MainframeEngine.L10n.Gettext;

/// <summary>
/// Writes <c>.po</c>/<c>.pot</c> files in GNU layout (no line wrapping other than after embedded newlines, LF line
/// endings, UTF-8 without BOM), deterministically, so regenerated files only differ where messages changed.
/// </summary>
internal static class PoWriter
{
    public static void WriteFile(PoCatalog catalog, string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        var text = Write(catalog);
        if (File.Exists(path) && File.ReadAllText(path, Encoding.UTF8) == text)
            return; // unchanged: keep the timestamp so incremental builds stay quiet
        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    public static string Write(PoCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var sb = new StringBuilder();
        foreach (var comment in catalog.FileComments)
            sb.Append(comment.Length == 0 ? "#" : "# " + comment).Append('\n');

        // File comments sit directly above the header entry (GNU layout); a blank line separates the others.
        var first = true;
        foreach (var entry in catalog.Entries)
        {
            if (!first || (catalog.FileComments.Count > 0 && !entry.IsHeader))
                sb.Append('\n');
            first = false;
            WriteEntry(sb, entry);
        }

        return sb.ToString();
    }

    private static void WriteEntry(StringBuilder sb, PoEntry entry)
    {
        foreach (var comment in entry.TranslatorComments)
            sb.Append(comment.Length == 0 ? "#" : "# " + comment).Append('\n');
        foreach (var comment in entry.ExtractedComments)
            sb.Append("#. ").Append(comment).Append('\n');
        if (!entry.Obsolete && entry.References.Count > 0)
            WriteReferences(sb, entry.References);
        if (entry.Flags.Count > 0)
            sb.Append("#, ").Append(string.Join(", ", entry.Flags)).Append('\n');
        if (!entry.Obsolete)
        {
            foreach (var previous in entry.PreviousLines)
                sb.Append("#| ").Append(previous).Append('\n');
        }

        var prefix = entry.Obsolete ? "#~ " : string.Empty;
        if (entry.Context is not null)
            WriteKeyword(sb, prefix, "msgctxt", entry.Context);
        WriteKeyword(sb, prefix, "msgid", entry.Id);
        if (entry.IdPlural is not null)
        {
            WriteKeyword(sb, prefix, "msgid_plural", entry.IdPlural);
            for (var i = 0; i < (entry.Translations.Count == 0 ? 2 : entry.Translations.Count); i++)
                WriteKeyword(sb, prefix, $"msgstr[{i}]", i < entry.Translations.Count ? entry.Translations[i] : string.Empty);
        }
        else
        {
            WriteKeyword(sb, prefix, "msgstr", entry.Translations.Count > 0 ? entry.Translations[0] : string.Empty);
        }
    }

    private static void WriteReferences(StringBuilder sb, List<string> references)
    {
        // GNU style: "#: a b c", wrapped before 80 columns.
        var line = new StringBuilder("#:");
        foreach (var reference in references)
        {
            if (line.Length > 2 && line.Length + 1 + reference.Length > 79)
            {
                sb.Append(line).Append('\n');
                line.Clear().Append("#:");
            }

            line.Append(' ').Append(reference);
        }

        sb.Append(line).Append('\n');
    }

    private static void WriteKeyword(StringBuilder sb, string prefix, string keyword, string value)
    {
        // Split after each embedded newline, like msgcat: "msgid \"\"" then one line per piece.
        var pieces = SplitAfterNewlines(value);
        if (pieces.Count <= 1)
        {
            sb.Append(prefix).Append(keyword).Append(' ');
            AppendQuoted(sb, value);
            sb.Append('\n');
            return;
        }

        sb.Append(prefix).Append(keyword).Append(" \"\"\n");
        foreach (var piece in pieces)
        {
            sb.Append(prefix);
            AppendQuoted(sb, piece);
            sb.Append('\n');
        }
    }

    private static List<string> SplitAfterNewlines(string value)
    {
        var pieces = new List<string>();
        var start = 0;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '\n' || i == value.Length - 1)
                continue;
            pieces.Add(value[start..(i + 1)]);
            start = i + 1;
        }

        if (start < value.Length || pieces.Count == 0)
            pieces.Add(value[start..]);
        return pieces;
    }

    /// <summary>Appends a C-quoted string.</summary>
    public static void AppendQuoted(StringBuilder sb, string value)
    {
        sb.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\t': sb.Append("\\t"); break;
                case '\r': sb.Append("\\r"); break;
                case '\a': sb.Append("\\a"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\v': sb.Append("\\v"); break;
                default:
                    if (c < ' ')
                        sb.Append('\\').Append(Convert.ToString(c, 8).PadLeft(3, '0'));
                    else
                        sb.Append(c);
                    break;
            }
        }

        sb.Append('"');
    }
}

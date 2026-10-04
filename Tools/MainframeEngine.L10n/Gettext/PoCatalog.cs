using System.Globalization;
using System.Text;

namespace MainframeEngine.L10n.Gettext;

/// <summary>A parsed <c>.po</c> or <c>.pot</c> file: its header and messages in file order.</summary>
internal sealed class PoCatalog
{
    /// <summary>Comment lines before the header entry (<c># Spanish translation…</c>), without the <c># </c>.</summary>
    public List<string> FileComments { get; } = [];

    public List<PoEntry> Entries { get; } = [];

    /// <summary>The header entry (<c>msgid ""</c>), or null.</summary>
    public PoEntry? Header => Entries.Find(static e => e.IsHeader && !e.Obsolete);

    /// <summary>Messages other than the header, including obsolete ones.</summary>
    public IEnumerable<PoEntry> Messages => Entries.Where(static e => !e.IsHeader);

    /// <summary>Header fields in order (<c>Language</c>, <c>Plural-Forms</c>, …).</summary>
    public List<KeyValuePair<string, string>> HeaderFields
    {
        get
        {
            var fields = new List<KeyValuePair<string, string>>();
            var header = Header;
            if (header is null || header.Translations.Count == 0)
                return fields;
            foreach (var line in header.Translations[0].Split('\n'))
            {
                var colon = line.IndexOf(':', StringComparison.Ordinal);
                if (colon <= 0)
                    continue;
                fields.Add(new(line[..colon].Trim(), line[(colon + 1)..].Trim()));
            }

            return fields;
        }
    }

    public string? GetHeader(string name)
    {
        foreach (var (key, value) in HeaderFields)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                return value;
        }

        return null;
    }

    /// <summary>Sets (or adds) a header field, creating the header entry if needed.</summary>
    public void SetHeader(string name, string value)
    {
        var fields = HeaderFields;
        var index = fields.FindIndex(f => string.Equals(f.Key, name, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
            fields[index] = new(fields[index].Key, value);
        else
            fields.Add(new(name, value));
        var text = new StringBuilder();
        foreach (var (key, v) in fields)
            text.Append(key).Append(": ").Append(v).Append('\n');

        var header = Header;
        if (header is null)
        {
            header = new PoEntry();
            Entries.Insert(0, header);
        }

        header.Translations.Clear();
        header.Translations.Add(text.ToString());
    }

    /// <summary>The <c>Language</c> header (normalised), or null.</summary>
    public string? Language
    {
        get
        {
            var value = GetHeader("Language");
            var normalized = Localization.LocaleId.Normalize(value);
            return normalized.Length == 0 ? null : normalized;
        }
    }

    /// <summary><c>nplurals</c> from the <c>Plural-Forms</c> header, or null when absent or malformed.</summary>
    public int? PluralCount
    {
        get
        {
            var forms = GetHeader("Plural-Forms");
            if (forms is null)
                return null;
            foreach (var part in forms.Split(';'))
            {
                var kv = part.Split('=', 2, StringSplitOptions.TrimEntries);
                if (kv.Length == 2 && kv[0] == "nplurals" && int.TryParse(kv[1], NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0)
                    return n;
            }

            return null;
        }
    }

    /// <summary>Messages by <see cref="PoEntry.Key"/> (non-obsolete, last wins).</summary>
    public Dictionary<string, PoEntry> ByKey()
    {
        var map = new Dictionary<string, PoEntry>(StringComparer.Ordinal);
        foreach (var entry in Entries)
        {
            if (!entry.Obsolete && !entry.IsHeader)
                map[entry.Key] = entry;
        }

        return map;
    }
}

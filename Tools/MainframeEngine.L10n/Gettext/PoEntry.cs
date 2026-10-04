namespace MainframeEngine.L10n.Gettext;

/// <summary>One message of a <c>.po</c>/<c>.pot</c> file, with its comments.</summary>
internal sealed class PoEntry
{
    /// <summary>The separator gettext puts between a context and a msgid in <c>.mo</c> keys.</summary>
    public const char ContextGlue = '\u0004';

    public string? Context { get; set; }

    public string Id { get; set; } = string.Empty;

    public string? IdPlural { get; set; }

    /// <summary><c>msgstr</c> (one element) or <c>msgstr[0..n-1]</c>.</summary>
    public List<string> Translations { get; } = [];

    /// <summary><c># </c> comments (translator comments, kept by merges).</summary>
    public List<string> TranslatorComments { get; } = [];

    /// <summary><c>#.</c> comments (from the extractor).</summary>
    public List<string> ExtractedComments { get; } = [];

    /// <summary><c>#:</c> references (<c>path:line</c>).</summary>
    public List<string> References { get; } = [];

    /// <summary><c>#,</c> flags (<c>fuzzy</c>, <c>csharp-format</c>, …).</summary>
    public List<string> Flags { get; } = [];

    /// <summary><c>#|</c> previous msgctxt/msgid/msgid_plural (kept as written).</summary>
    public List<string> PreviousLines { get; } = [];

    /// <summary>An obsolete (<c>#~</c>) entry: kept in the file, never compiled.</summary>
    public bool Obsolete { get; set; }

    /// <summary>1-based line of the entry's msgid (0 when built in memory).</summary>
    public int Line { get; set; }

    public bool IsHeader => Context is null && Id.Length == 0;

    public bool IsPlural => IdPlural is not null;

    public bool IsFuzzy => Flags.Contains("fuzzy");

    /// <summary>The <c>.mo</c> key: <c>context\u0004id</c>, or the id.</summary>
    public string Key => Context is null ? Id : Context + ContextGlue + Id;

    /// <summary>The first translation is not empty (what msgfmt compiles).</summary>
    public bool HasTranslation => Translations.Count > 0 && Translations[0].Length > 0;

    /// <summary>Every form is translated.</summary>
    public bool IsFullyTranslated => Translations.Count > 0 && Translations.TrueForAll(static t => t.Length > 0);

    public void SetFlag(string flag, bool on)
    {
        if (on && !Flags.Contains(flag))
            Flags.Add(flag);
        else if (!on)
            Flags.Remove(flag);
    }

    /// <summary>A copy of the message identity and comments without translations.</summary>
    public PoEntry CloneTemplate()
    {
        var copy = new PoEntry { Context = Context, Id = Id, IdPlural = IdPlural, Line = Line };
        copy.ExtractedComments.AddRange(ExtractedComments);
        copy.References.AddRange(References);
        copy.Flags.AddRange(Flags);
        copy.Flags.Remove("fuzzy");
        return copy;
    }

    public override string ToString() => Context is null ? Id : $"{Context}|{Id}";
}

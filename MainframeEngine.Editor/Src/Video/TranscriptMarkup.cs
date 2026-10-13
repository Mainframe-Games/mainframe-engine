using System.Globalization;
using System.Text;
using MainframeEngine.Video.Models;

namespace MainframeEngine.Editor.Video;

/// <summary>
/// The transcript as RML for the transcript panel: one <c>&lt;span id="w-i" class="w" data-i="i"&gt;</c> per word, in
/// paragraphs that break at a long pause. Pure, so it is tested without RmlUi. The panel rebuilds this string only when the
/// transcript itself changes, and flips the <c>cut</c> class on single spans for a cut or an undo (<see cref="TranscriptPanel"/>).
/// </summary>
public static class TranscriptMarkup
{
    /// <summary>A silence of this long or longer between two words starts a new paragraph.</summary>
    public const double ParagraphGapSeconds = 2.0;

    /// <summary>
    /// Builds the paragraphs. Each word is followed by a space, so the text wraps between words; a struck-through
    /// (<paramref name="cut"/>) word gets the <c>cut</c> class.
    /// </summary>
    /// <param name="words">The transcript, in order.</param>
    /// <param name="cut">One flag per word (<c>TranscriptView.CutStates</c>).</param>
    public static string Build(IReadOnlyList<Word> words, IReadOnlyList<bool> cut)
    {
        ArgumentNullException.ThrowIfNull(words);
        ArgumentNullException.ThrowIfNull(cut);
        if (cut.Count < words.Count)
            throw new ArgumentException("There must be a cut flag for every word.", nameof(cut));

        var builder = new StringBuilder(words.Count * 64);
        for (var i = 0; i < words.Count; i++)
        {
            if (i == 0)
                builder.Append("<div class=\"para\">");
            else if (words[i].Start - words[i - 1].End >= ParagraphGapSeconds)
                builder.Append("</div><div class=\"para\">");

            var index = i.ToString(CultureInfo.InvariantCulture);
            builder.Append("<span id=\"w-").Append(index).Append("\" class=\"w").Append(cut[i] ? " cut" : "")
                .Append("\" data-i=\"").Append(index).Append("\">").Append(Escape(words[i].Text)).Append("</span> ");
        }

        if (words.Count > 0)
            builder.Append("</div>");
        return builder.ToString();
    }

    /// <summary>Escapes text for RML (<c>&amp; &lt; &gt; &quot;</c>, and the characters data bindings would read).</summary>
    public static string Escape(string text) => RmlText.Escape(text);
}

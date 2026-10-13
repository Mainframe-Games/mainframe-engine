using System.Globalization;
using System.Text;
using MainframeEngine.Video.Models;

namespace MainframeEngine.Video.Transcript;

/// <summary>
/// Writes the <em>edited</em> transcript: cut words are omitted and times are remapped onto the
/// exported video, so an SRT lines up with the export and the text reads like the finished piece.
/// </summary>
public static class TranscriptExporter
{
    /// <summary>A pause longer than this in the source starts a new paragraph.</summary>
    public const double ParagraphGapSeconds = 2.0;

    /// <summary>A pause longer than this in the source starts a new SRT cue.</summary>
    public const double CueGapSeconds = 0.7;

    public const double MaxCueSeconds = 5.0;

    public const int MaxLineChars = 42;

    public const int MaxCueChars = 2 * MaxLineChars;

    public static string ToPlainText(IReadOnlyList<Word> words, IReadOnlyList<Segment> segments)
    {
        ArgumentNullException.ThrowIfNull(words);
        ArgumentNullException.ThrowIfNull(segments);

        var kept = KeptWords(words, segments);
        if (kept.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        for (var i = 0; i < kept.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(kept[i].Start - kept[i - 1].End > ParagraphGapSeconds ? "\n\n" : " ");
            }

            sb.Append(kept[i].Text);
        }

        sb.Append('\n');
        return sb.ToString();
    }

    public static string ToSrt(IReadOnlyList<Word> words, IReadOnlyList<Segment> segments)
    {
        ArgumentNullException.ThrowIfNull(words);
        ArgumentNullException.ThrowIfNull(segments);

        var kept = KeptWords(words, segments);
        var timeline = new OutputTimeline(segments);

        var cues = new List<List<Word>>();
        var current = new List<Word>();
        var currentChars = 0;
        foreach (var word in kept)
        {
            if (current.Count > 0)
            {
                var gap = word.Start - current[^1].End;
                var chars = currentChars + 1 + word.Text.Length;
                var length = timeline.ToOutput(word.End) - timeline.ToOutput(current[0].Start);
                if (gap > CueGapSeconds || chars > MaxCueChars || length > MaxCueSeconds)
                {
                    cues.Add(current);
                    current = [];
                    currentChars = 0;
                }
            }

            currentChars += (current.Count > 0 ? 1 : 0) + word.Text.Length;
            current.Add(word);
        }

        if (current.Count > 0)
        {
            cues.Add(current);
        }

        var sb = new StringBuilder();
        for (var i = 0; i < cues.Count; i++)
        {
            var cue = cues[i];
            if (i > 0)
            {
                sb.Append('\n');
            }

            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append(FormatSrtTime(timeline.ToOutput(cue[0].Start)))
              .Append(" --> ")
              .Append(FormatSrtTime(timeline.ToOutput(cue[^1].End)))
              .Append('\n');
            sb.Append(Wrap(string.Join(' ', cue.Select(w => w.Text)))).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary><c>HH:MM:SS,mmm</c>, rounded to the millisecond.</summary>
    public static string FormatSrtTime(double seconds)
    {
        var totalMs = (long)Math.Round(Math.Max(0, seconds) * 1000);
        var t = TimeSpan.FromMilliseconds(totalMs);
        return string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00},{t.Milliseconds:000}");
    }

    private static List<Word> KeptWords(IReadOnlyList<Word> words, IReadOnlyList<Segment> segments)
        => words.Where(w => !TranscriptView.IsCut(w, segments)).OrderBy(w => w.Start).ToList();

    /// <summary>Breaks text over <see cref="MaxLineChars"/> into two lines at the space nearest its middle.</summary>
    private static string Wrap(string text)
    {
        if (text.Length <= MaxLineChars)
        {
            return text;
        }

        var middle = text.Length / 2;
        var best = -1;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == ' ' && (best < 0 || Math.Abs(i - middle) < Math.Abs(best - middle)))
            {
                best = i;
            }
        }

        return best < 0 ? text : text[..best] + "\n" + text[(best + 1)..];
    }
}

using MainframeEngine.Video.Models;
using MainframeEngine.Video.Transcript;

namespace MainframeEngine.Video.Timelines;

/// <summary>One auto-caption: its words and when it shows, in timeline seconds.</summary>
public sealed record Caption(string Text, double Start, double End);

/// <summary>
/// Auto-captions (G20.2): a clip's kept transcript words, in timeline time, grouped into captions of at most
/// <c>maxWords</c> words, breaking at a pause and after a sentence end. Words that a cut removes are skipped, so the
/// captions say what plays. Pure.
/// </summary>
public static class CaptionPlanner
{
    public const int DefaultMaxWords = 7;
    public const double DefaultPauseSeconds = 0.5;

    // A caption shows at least this long, and lingers this long after its last word when nothing follows at once.
    private const double MinSeconds = 0.6, LingerSeconds = 0.3;

    public static IReadOnlyList<Caption> Plan(Clip clip, IReadOnlyList<Word> words, FrameRate rate, int maxWords = DefaultMaxWords,
        double pauseSeconds = DefaultPauseSeconds)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(words);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxWords, 1);
        var placed = new List<(Word Word, double Start, double End)>();
        foreach (var word in ClipDetection.WordsInWindow(words, clip.SourceIn, clip.SourceOut))
        {
            if (TranscriptView.IsCut(word, clip.Segments) || TimelineLayout.TimelineTimeOf(clip, word.Start) is not { } start)
                continue;
            var end = TimelineLayout.TimelineTimeOf(clip, word.End) ?? start + (word.End - word.Start) / clip.Speed;
            placed.Add((word, start, Math.Max(start, end)));
        }

        var groups = new List<List<(Word Word, double Start, double End)>>();
        foreach (var item in placed)
        {
            var current = groups.Count > 0 ? groups[^1] : null;
            var last = current?[^1];
            var breaks = current is null || current.Count >= maxWords
                || item.Start - last!.Value.End >= pauseSeconds
                || EndsSentence(last.Value.Word.Text);
            if (breaks)
                groups.Add([item]);
            else
                current!.Add(item);
        }

        var captions = new List<Caption>(groups.Count);
        for (var i = 0; i < groups.Count; i++)
        {
            var group = groups[i];
            var start = rate.Snap(group[0].Start);
            var next = i + 1 < groups.Count ? rate.Snap(groups[i + 1][0].Start) : double.PositiveInfinity;
            var end = rate.Snap(Math.Max(group[^1].End + LingerSeconds, start + MinSeconds));
            end = Math.Max(start + rate.FrameDuration, Math.Min(end, next));
            captions.Add(new Caption(string.Join(' ', group.Select(w => w.Word.Text.Trim())), start, end));
        }

        return captions;
    }

    private static bool EndsSentence(string text)
    {
        var trimmed = text.TrimEnd();
        return trimmed.Length > 0 && trimmed[^1] is '.' or '?' or '!';
    }
}

using MainframeEngine.Video.Models;

namespace MainframeEngine.Video.Transcript;

/// <summary>
/// Read-only questions the transcript panel asks about words against the current partition.
/// A word is "cut" when its alignment anchor (or, without one, its midpoint) falls in a disabled
/// segment. The anchor is used because the heuristic span often misses a short word; the midpoint
/// rule means a cut that clips the very edge
/// of a word should not strike the whole word through.
/// </summary>
public static class TranscriptView
{
    public static bool IsCut(Word word, IReadOnlyList<Segment> segments)
    {
        ArgumentNullException.ThrowIfNull(word);
        ArgumentNullException.ThrowIfNull(segments);
        // The alignment anchor is inside the spoken word when present; the heuristic span may not be.
        var index = SegmentIndexAt(segments, word.Anchor ?? (word.Start + word.End) / 2);
        return index >= 0 && !segments[index].Enabled;
    }

    /// <summary>One flag per word, in order. Equivalent to calling <see cref="IsCut"/> for each.</summary>
    public static bool[] CutStates(IReadOnlyList<Word> words, IReadOnlyList<Segment> segments)
    {
        ArgumentNullException.ThrowIfNull(words);
        ArgumentNullException.ThrowIfNull(segments);
        var states = new bool[words.Count];
        for (var i = 0; i < words.Count; i++)
        {
            states[i] = IsCut(words[i], segments);
        }

        return states;
    }

    /// <summary>Index of the word whose <c>[Start, End)</c> contains <paramref name="seconds"/>, or -1. Words must be sorted by start.</summary>
    public static int IndexAtTime(IReadOnlyList<Word> words, double seconds)
    {
        ArgumentNullException.ThrowIfNull(words);
        if (words.Count == 0 || !double.IsFinite(seconds))
        {
            return -1;
        }

        // Last word whose Start <= seconds.
        int lo = 0, hi = words.Count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) >> 1;
            if (words[mid].Start <= seconds)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return words[lo].Start <= seconds && seconds < words[lo].End ? lo : -1;
    }

    /// <summary>Segment containing <paramref name="time"/>; times at or past the end map to the last segment, before 0 to -1.</summary>
    private static int SegmentIndexAt(IReadOnlyList<Segment> segments, double time)
    {
        if (segments.Count == 0 || time < 0)
        {
            return -1;
        }

        if (time >= segments[^1].End)
        {
            return segments.Count - 1;
        }

        int lo = 0, hi = segments.Count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) >> 1;
            if (segments[mid].Start <= time)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return lo;
    }
}

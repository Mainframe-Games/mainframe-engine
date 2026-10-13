using MainframeEngine.Video.Models;

namespace MainframeEngine.Video.Detection;

/// <summary>
/// Finds where a filler word was actually spoken. The recogniser's start and end for a short
/// "uh" are heuristic and land on the pause beside it more often than not, but its alignment
/// anchor (<see cref="Word.Anchor"/>) does fall inside the sound. So: take the burst of speech in
/// the loudness envelope that contains the anchor. When a neighbouring word's heuristic span
/// swallowed that burst (the tell-tale of a filler run together with its neighbour), split at
/// the quietest point between the two if it is a real valley, and otherwise fall back to a fixed
/// allowance around the anchor. Pure; the caller supplies the envelope.
/// </summary>
public static class FillerSpanLocator
{
    /// <summary>Two bursts closer than this are one burst: a glottal hiccup, not a pause.</summary>
    public const double MergeGapSeconds = 0.060;

    /// <summary>Bursts shorter than this are clicks, not speech.</summary>
    public const double MinBurstSeconds = 0.030;

    /// <summary>How far from an anchor that sits in a pause we look for the burst it belongs to.</summary>
    public const double SearchRadiusSeconds = 0.250;

    /// <summary>A neighbour whose heuristic span covers at least this much of the burst is merged with the filler.</summary>
    public const double MergedOverlapSeconds = 0.150;

    /// <summary>A dip counts as a word boundary only below this fraction of the burst's median loudness.</summary>
    public const double ValleyFraction = 0.75;

    /// <summary>With no valley to split at, a filler merged with the word before it starts this long before its anchor.</summary>
    public const double AnchorLeadSeconds = 0.080;

    /// <summary>With no valley to split at, a filler merged with the word after it ends this long after its anchor.</summary>
    public const double AnchorTailSeconds = 0.300;

    /// <summary>Loudness above which a bucket counts as speech: four times the quiet floor, but never less than 2% of the loudest bucket.</summary>
    public static double SpeechThreshold(IReadOnlyList<double> envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.Count == 0)
        {
            return 0;
        }

        var sorted = envelope.OrderBy(v => v).ToArray();
        var floor = sorted[sorted.Length / 10];
        var loud = sorted[Math.Min(sorted.Length - 1, sorted.Length * 99 / 100)];
        return Math.Max(floor * 4, loud * 0.02);
    }

    /// <summary>
    /// The spoken extent of <paramref name="filler"/>, or null if it has no anchor or no burst of
    /// speech lies within <see cref="SearchRadiusSeconds"/> of it.
    /// </summary>
    /// <param name="envelope">Loudness per bucket, from the start of the source.</param>
    /// <param name="bucketSeconds">Width of one envelope bucket.</param>
    /// <param name="threshold">See <see cref="SpeechThreshold"/>.</param>
    /// <param name="previous">The word before the filler, if any.</param>
    /// <param name="filler">The filler word.</param>
    /// <param name="next">The word after the filler, if any.</param>
    public static (double Start, double End)? Locate(
        IReadOnlyList<double> envelope,
        double bucketSeconds,
        double threshold,
        Word? previous,
        Word filler,
        Word? next)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(filler);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bucketSeconds);

        if (filler.Anchor is not { } anchor)
        {
            return null;
        }

        var bursts = Bursts(envelope, bucketSeconds, threshold);
        if (BurstNear(bursts, bucketSeconds, anchor) is not var (start, end))
        {
            return null;
        }

        if (previous is not null && Overlap(previous, start, end) >= MergedOverlapSeconds)
        {
            var from = Math.Max(start, previous.Anchor ?? start);
            start = Valley(envelope, bucketSeconds, start, end, from, anchor) ?? Math.Max(start, anchor - AnchorLeadSeconds);
        }

        if (next is not null && Overlap(next, start, end) >= MergedOverlapSeconds)
        {
            var to = Math.Min(end, next.Anchor ?? end);
            end = Valley(envelope, bucketSeconds, start, end, anchor, to) ?? Math.Min(end, anchor + AnchorTailSeconds);
        }

        return (start, end);
    }

    private static double Overlap(Word word, double start, double end)
        => Math.Max(0, Math.Min(word.End, end) - Math.Max(word.Start, start));

    /// <summary>Speech bursts in order: runs of buckets above the threshold, gaps under <see cref="MergeGapSeconds"/> bridged, runs under <see cref="MinBurstSeconds"/> dropped.</summary>
    private static List<(int Start, int End)> Bursts(IReadOnlyList<double> envelope, double bucketSeconds, double threshold)
    {
        var raw = new List<(int Start, int End)>();
        int? runStart = null;
        for (var b = 0; b <= envelope.Count; b++)
        {
            var voiced = b < envelope.Count && envelope[b] > threshold;
            if (voiced && runStart is null)
            {
                runStart = b;
            }
            else if (!voiced && runStart is { } s)
            {
                raw.Add((s, b));
                runStart = null;
            }
        }

        var mergeGap = (int)Math.Round(MergeGapSeconds / bucketSeconds);
        var merged = new List<(int Start, int End)>();
        foreach (var run in raw)
        {
            if (merged.Count > 0 && run.Start - merged[^1].End < mergeGap)
            {
                merged[^1] = (merged[^1].Start, run.End);
            }
            else
            {
                merged.Add(run);
            }
        }

        var minLength = (int)Math.Round(MinBurstSeconds / bucketSeconds);
        return merged.Where(r => r.End - r.Start >= minLength).ToList();
    }

    /// <summary>The burst containing the time, else the nearest one within the search radius.</summary>
    private static (double Start, double End)? BurstNear(List<(int Start, int End)> bursts, double bucketSeconds, double time)
    {
        (double Start, double End)? best = null;
        var bestDistance = SearchRadiusSeconds;
        foreach (var (s, e) in bursts)
        {
            var start = s * bucketSeconds;
            var end = e * bucketSeconds;
            var distance = time < start ? start - time : time > end ? time - end : 0;
            if (distance <= bestDistance)
            {
                best = (start, end);
                bestDistance = distance;
            }
        }

        return best;
    }

    /// <summary>
    /// A merged neighbour owns at least this much of the burst's edge, so a valley cannot lie
    /// closer to the edge than this. Also skips the ramp into speech and any click just before it,
    /// which the burst merging can absorb.
    /// </summary>
    private const double EdgeSeconds = 0.100;

    /// <summary>The start of the quietest bucket in <c>[from, to)</c>, if it is a real valley relative to the burst's median.</summary>
    private static double? Valley(IReadOnlyList<double> envelope, double bucketSeconds, double burstStart, double burstEnd, double from, double to)
    {
        var b0 = (int)Math.Round(burstStart / bucketSeconds);
        var b1 = (int)Math.Round(burstEnd / bucketSeconds);
        var median = envelope.Skip(b0).Take(Math.Max(1, b1 - b0)).OrderBy(v => v).ElementAt(Math.Max(0, (b1 - b0) / 2));

        var edge = (int)Math.Round(EdgeSeconds / bucketSeconds);
        var lo = Math.Max(b0 + edge, (int)Math.Round(from / bucketSeconds));
        var hi = Math.Min(b1 - edge, (int)Math.Round(to / bucketSeconds));
        var best = -1;
        for (var b = lo; b < hi; b++)
        {
            if (best < 0 || envelope[b] < envelope[best])
            {
                best = b;
            }
        }

        return best >= 0 && envelope[best] < median * ValleyFraction ? best * bucketSeconds : null;
    }
}

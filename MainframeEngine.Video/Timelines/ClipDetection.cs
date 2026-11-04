using System.Globalization;
using MainframeEngine.Video.Detection;
using MainframeEngine.Video.Models;

namespace MainframeEngine.Video.Timelines;

/// <summary>
/// Cutback's detectors on one timeline clip (G20d): their cuts are planned over the clip's window <c>[In, Out]</c> only,
/// and merged with the cuts of the same origin outside it, so a re-run in one window never drops another window's results
/// (the partition covers the whole source). Manual cuts survive as they always did. Pure.
/// </summary>
public static class ClipDetection
{
    /// <summary>
    /// <paramref name="planned"/> clipped to <c>[windowStart, windowEnd]</c>, plus the disabled segments of
    /// <paramref name="origin"/> in <paramref name="current"/> outside it: what to hand to <c>ReplaceAutoSegments</c>
    /// (<see cref="SegmentOrigin.Auto"/>) or <c>ReplaceOriginCuts</c>.
    /// </summary>
    public static IReadOnlyList<PlannedCut> InWindow(IEnumerable<PlannedCut> planned, IReadOnlyList<Segment> current, SegmentOrigin origin,
        double windowStart, double windowEnd)
    {
        ArgumentNullException.ThrowIfNull(planned);
        ArgumentNullException.ThrowIfNull(current);
        var result = new List<PlannedCut>();
        foreach (var cut in planned)
        {
            var start = Math.Max(cut.Start, windowStart);
            var end = Math.Min(cut.End, windowEnd);
            if (end > start)
                result.Add(cut with { Start = start, End = end });
        }

        foreach (var segment in current)
        {
            if (segment.Enabled || segment.Origin != origin)
                continue;
            var reason = segment.Reason ?? "";
            if (segment.Start < windowStart)
                result.Add(new PlannedCut(segment.Start, Math.Min(segment.End, windowStart), reason));
            if (segment.End > windowEnd)
                result.Add(new PlannedCut(Math.Max(segment.Start, windowEnd), segment.End, reason));
        }

        result.RemoveAll(c => c.End - c.Start <= 1e-9);
        result.Sort((a, b) => a.Start.CompareTo(b.Start));
        return result;
    }

    /// <summary>The silences inside the window (clipped to it): what Detect Silence plans from.</summary>
    public static IReadOnlyList<SilenceSpan> SpansInWindow(IEnumerable<SilenceSpan> spans, double windowStart, double windowEnd)
    {
        ArgumentNullException.ThrowIfNull(spans);
        var result = new List<SilenceSpan>();
        foreach (var span in spans)
        {
            var start = Math.Max(span.Start, windowStart);
            var end = Math.Min(span.End, windowEnd);
            if (end > start)
                result.Add(new SilenceSpan(start, end));
        }

        return result;
    }

    /// <summary>The words that lie inside the window (by their middle).</summary>
    public static IReadOnlyList<Word> WordsInWindow(IReadOnlyList<Word> words, double windowStart, double windowEnd)
    {
        ArgumentNullException.ThrowIfNull(words);
        var result = new List<Word>();
        foreach (var word in words)
        {
            var middle = (word.Start + word.End) / 2;
            if (middle >= windowStart && middle < windowEnd)
                result.Add(word);
        }

        return result;
    }

    /// <summary>Seconds of the window the partition keeps.</summary>
    public static double KeptInWindow(IReadOnlyList<Segment> segments, double windowStart, double windowEnd)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var kept = 0.0;
        foreach (var segment in segments)
            if (segment.Enabled)
                kept += Math.Max(0, Math.Min(segment.End, windowEnd) - Math.Max(segment.Start, windowStart));
        return kept;
    }

    /// <summary>
    /// The notice for a detection run, in Cutback's sentences: <paramref name="found"/> things (<paramref name="singular"/>
    /// / <paramref name="plural"/>) and the kept time they removed from the window (or gave back), or that all of it was
    /// cut already.
    /// </summary>
    public static string Status(int found, string singular, string plural, double keptBefore, double keptAfter, string verb, string none)
    {
        if (found == 0)
            return none;
        var things = string.Create(CultureInfo.InvariantCulture, $"{found} {(found == 1 ? singular : plural)}");
        var change = keptBefore - keptAfter;
        if (Math.Abs(change) < 0.05)
            return $"{verb} {things}; all were already cut.";
        return change > 0 ? $"{verb} {things}, {Clock(change)} removed." : $"{verb} {things}, {Clock(-change)} restored.";
    }

    /// <summary><c>m:ss.t</c> (the editor's clock format).</summary>
    public static string Clock(double seconds)
    {
        var tenths = (long)Math.Round(Math.Max(0, seconds) * 10, MidpointRounding.AwayFromZero);
        return string.Create(CultureInfo.InvariantCulture, $"{tenths / 600}:{tenths / 10 % 60:00}.{tenths % 10}");
    }
}

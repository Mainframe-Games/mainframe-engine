using System.Collections.Immutable;
using MainframeEngine.Video.Models;

namespace MainframeEngine.Video.Timelines;

/// <summary>A range of source seconds, <c>[Start, End)</c>.</summary>
public readonly record struct SourceRange(double Start, double End)
{
    public double Duration => End - Start;
}

/// <summary>
/// Where things are on a timeline (pure). A clip plays the kept parts of its window back to back, so its timeline length
/// is the kept time ÷ speed, rounded to frames, and timeline time maps to source time through the kept ranges.
/// </summary>
/// <remarks>
/// Kept time is measured as a <em>kept position</em> over the clip's whole partition: the kept seconds before a source
/// time. A window's kept time is the difference of its ends' positions, and trims move positions, so a trim that extends
/// a clip past its window walks into the source through kept footage only.
/// </remarks>
public static class TimelineLayout
{
    private const double Epsilon = 1e-6;

    /// <summary>The clip's length on the timeline: kept seconds in its window ÷ speed, rounded to frames.</summary>
    public static double Length(Clip clip, FrameRate rate)
    {
        ArgumentNullException.ThrowIfNull(clip);
        return rate.Snap(KeptDuration(clip) / clip.Speed);
    }

    /// <summary>Where the clip ends on the timeline.</summary>
    public static double End(Clip clip, FrameRate rate) => clip.Start + Length(clip, rate);

    /// <summary>Kept source seconds inside the window.</summary>
    public static double KeptDuration(Clip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        return Math.Max(0, KeptPosition(clip.Segments, clip.SourceOut) - KeptPosition(clip.Segments, clip.SourceIn));
    }

    /// <summary>The kept source ranges inside the window, in order, contiguous ones merged.</summary>
    public static IReadOnlyList<SourceRange> KeptRanges(Clip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        var ranges = new List<SourceRange>();
        foreach (var segment in clip.Segments)
        {
            if (!segment.Enabled)
                continue;
            var start = Math.Max(segment.Start, clip.SourceIn);
            var end = Math.Min(segment.End, clip.SourceOut);
            if (end - start <= Epsilon)
                continue;
            if (ranges.Count > 0 && Math.Abs(ranges[^1].End - start) <= Epsilon)
                ranges[^1] = ranges[^1] with { End = end };
            else
                ranges.Add(new SourceRange(start, end));
        }

        return ranges;
    }

    /// <summary>The source time shown at <paramref name="timelineTime"/>, or null outside the clip.</summary>
    public static double? SourceTimeAt(Clip clip, FrameRate rate, double timelineTime)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (timelineTime < clip.Start - Epsilon || timelineTime >= End(clip, rate) - Epsilon)
            return null;
        var inPosition = KeptPosition(clip.Segments, clip.SourceIn);
        var outPosition = KeptPosition(clip.Segments, clip.SourceOut);
        var position = inPosition + Math.Max(0, timelineTime - clip.Start) * clip.Speed;
        return position >= outPosition - Epsilon
            ? SourceAtKeptPosition(clip.Segments, outPosition, preferNext: false)
            : SourceAtKeptPosition(clip.Segments, position, preferNext: true);
    }

    /// <summary>The timeline time at which <paramref name="sourceTime"/> plays, or null when it is cut or outside the window.</summary>
    public static double? TimelineTimeOf(Clip clip, double sourceTime)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (sourceTime < clip.SourceIn - Epsilon || sourceTime >= clip.SourceOut - Epsilon || !IsKept(clip.Segments, sourceTime))
            return null;
        var offset = KeptPosition(clip.Segments, sourceTime) - KeptPosition(clip.Segments, clip.SourceIn);
        return clip.Start + offset / clip.Speed;
    }

    /// <summary>The clip on <paramref name="trackId"/> playing at <paramref name="time"/>, or null.</summary>
    public static Clip? ClipAt(Timeline timeline, string trackId, double time)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        foreach (var clip in timeline.Clips)
            if (clip.TrackId == trackId && time >= clip.Start - Epsilon && time < End(clip, timeline.Rate) - Epsilon)
                return clip;
        return null;
    }

    /// <summary>The end of the last clip, 0 for an empty timeline.</summary>
    public static double Duration(Timeline timeline)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        var end = 0.0;
        foreach (var clip in timeline.Clips)
            end = Math.Max(end, End(clip, timeline.Rate));
        return end;
    }

    /// <summary>The playback and export range: in to out, defaulting to the whole timeline.</summary>
    public static (double Start, double End) Range(Timeline timeline)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        var start = timeline.InPoint ?? 0;
        var end = timeline.OutPoint ?? Duration(timeline);
        return (start, Math.Max(start, end));
    }

    /// <summary>The kept seconds before <paramref name="sourceTime"/> in a partition.</summary>
    public static double KeptPosition(ImmutableArray<Segment> segments, double sourceTime)
    {
        var position = 0.0;
        foreach (var segment in segments)
        {
            if (segment.Start >= sourceTime)
                break;
            if (segment.Enabled)
                position += Math.Min(segment.End, sourceTime) - segment.Start;
        }

        return position;
    }

    /// <summary>All kept seconds in a partition.</summary>
    public static double TotalKept(ImmutableArray<Segment> segments)
    {
        var total = 0.0;
        foreach (var segment in segments)
            if (segment.Enabled)
                total += segment.Duration;
        return total;
    }

    /// <summary>
    /// The source time at kept position <paramref name="position"/>. At a boundary between kept regions (a cut between
    /// them), <paramref name="preferNext"/> picks the start of the next kept region over the end of the previous one.
    /// </summary>
    public static double SourceAtKeptPosition(ImmutableArray<Segment> segments, double position, bool preferNext)
    {
        var cumulative = 0.0;
        var lastEnd = 0.0;
        var any = false;
        foreach (var segment in segments)
        {
            if (!segment.Enabled)
                continue;
            if (any && Math.Abs(position - cumulative) <= Epsilon && !preferNext)
                return lastEnd;
            var end = cumulative + segment.Duration;
            if (position < end - Epsilon)
                return segment.Start + Math.Max(0, position - cumulative);
            if (Math.Abs(position - end) <= Epsilon && !preferNext)
                return segment.End;
            cumulative = end;
            lastEnd = segment.End;
            any = true;
        }

        return lastEnd;
    }

    private static bool IsKept(ImmutableArray<Segment> segments, double time)
    {
        foreach (var segment in segments)
            if (segment.Contains(time))
                return segment.Enabled;
        return false;
    }
}

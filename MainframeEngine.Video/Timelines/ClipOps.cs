namespace MainframeEngine.Video.Timelines;

/// <summary>Trims and splits of one clip, through kept positions (<see cref="TimelineLayout"/>).</summary>
internal static class ClipOps
{
    /// <summary>
    /// The clip with its start moved to <paramref name="newStart"/> (later: shorter; earlier: longer, into the source's
    /// kept footage) and its end where it was. Clamped to the source and to one frame of length.
    /// </summary>
    public static Clip TrimStartTo(Clip clip, double newStart, FrameRate rate)
    {
        var end = TimelineLayout.End(clip, rate);
        newStart = Math.Min(rate.Snap(newStart), end - rate.FrameDuration);
        var inPosition = TimelineLayout.KeptPosition(clip.Segments, clip.SourceIn);
        var position = inPosition + (newStart - clip.Start) * clip.Speed;
        if (position < 0)
        {
            position = 0;
            newStart = rate.Snap(clip.Start - inPosition / clip.Speed);
        }

        var newIn = TimelineLayout.SourceAtKeptPosition(clip.Segments, position, preferNext: true);
        return clip with { Start = newStart, SourceIn = Math.Min(newIn, clip.SourceOut) };
    }

    /// <summary>The clip with its end moved to <paramref name="newEnd"/>, its start where it was; clamped like <see cref="TrimStartTo"/>.</summary>
    public static Clip TrimEndTo(Clip clip, double newEnd, FrameRate rate)
    {
        newEnd = Math.Max(rate.Snap(newEnd), clip.Start + rate.FrameDuration);
        var inPosition = TimelineLayout.KeptPosition(clip.Segments, clip.SourceIn);
        var total = TimelineLayout.TotalKept(clip.Segments);
        var position = Math.Min(total, inPosition + (newEnd - clip.Start) * clip.Speed);
        var newOut = TimelineLayout.SourceAtKeptPosition(clip.Segments, position, preferNext: false);
        return clip with { SourceOut = Math.Max(newOut, clip.SourceIn) };
    }

    /// <summary>Splits at <paramref name="time"/> (inside the clip): the left half keeps the id, the right one gets a new id.</summary>
    public static (Clip Left, Clip Right) Split(Clip clip, double time, FrameRate rate)
    {
        var left = TrimEndTo(clip, time, rate);
        var right = TrimStartTo(clip, time, rate) with { Id = TimelineFactory.NewId() };
        return (left, right);
    }

    /// <summary>The earliest start a start trim can reach (all kept footage before the window).</summary>
    public static double EarliestStart(Clip clip, FrameRate rate) =>
        rate.Snap(clip.Start - TimelineLayout.KeptPosition(clip.Segments, clip.SourceIn) / clip.Speed);

    /// <summary>The latest end an end trim can reach (all kept footage after the window).</summary>
    public static double LatestEnd(Clip clip, FrameRate rate) =>
        rate.Snap(clip.Start + (TimelineLayout.TotalKept(clip.Segments) - TimelineLayout.KeptPosition(clip.Segments, clip.SourceIn)) / clip.Speed);
}

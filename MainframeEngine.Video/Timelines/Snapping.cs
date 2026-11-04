namespace MainframeEngine.Video.Timelines;

/// <summary>
/// Snapping for timeline gestures (pure): the times a dragged edge or clip can catch, and the nearest one within a
/// tolerance (the UI converts its 8 dp to seconds at the current zoom).
/// </summary>
public static class Snapping
{
    /// <summary>
    /// Clip starts and ends (except <paramref name="exclude"/>: the clips being dragged), the playhead, markers, in and out,
    /// and 0; sorted, duplicates removed.
    /// </summary>
    public static IReadOnlyList<double> Candidates(Timeline timeline, double? playhead, IReadOnlySet<string>? exclude = null)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        var times = new SortedSet<double> { 0 };
        foreach (var clip in timeline.Clips)
        {
            if (exclude?.Contains(clip.Id) == true)
                continue;
            times.Add(clip.Start);
            times.Add(TimelineLayout.End(clip, timeline.Rate));
        }

        foreach (var marker in timeline.Markers)
            times.Add(marker.Time);
        if (playhead is { } p)
            times.Add(p);
        if (timeline.InPoint is { } i)
            times.Add(i);
        if (timeline.OutPoint is { } o)
            times.Add(o);
        return [.. times];
    }

    /// <summary>The candidate nearest <paramref name="time"/> within <paramref name="tolerance"/> seconds, or <paramref name="time"/>.</summary>
    public static (double Time, bool Snapped) Snap(double time, IReadOnlyList<double> candidates, double tolerance)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var best = time;
        var bestDistance = tolerance;
        var snapped = false;
        foreach (var candidate in candidates)
        {
            var distance = Math.Abs(candidate - time);
            if (distance <= bestDistance)
            {
                best = candidate;
                bestDistance = distance;
                snapped = true;
            }
        }

        return (best, snapped);
    }

    /// <summary>
    /// Snaps a dragged clip of <paramref name="length"/> starting at <paramref name="start"/> by whichever of its edges is
    /// nearer a candidate: the start it should take.
    /// </summary>
    public static (double Start, bool Snapped) SnapSpan(double start, double length, IReadOnlyList<double> candidates, double tolerance)
    {
        var (head, headSnapped) = Snap(start, candidates, tolerance);
        var (tail, tailSnapped) = Snap(start + length, candidates, tolerance);
        if (headSnapped && (!tailSnapped || Math.Abs(head - start) <= Math.Abs(tail - start - length)))
            return (head, true);
        return tailSnapped ? (tail - length, true) : (start, false);
    }
}

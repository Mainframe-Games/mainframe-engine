using MainframeEngine.Video.Models;

namespace MainframeEngine.Video.Detection;

/// <summary>
/// Turns filler-word spans into cuts that respect the user's existing edits: manual segments are
/// never touched, spans already inside a cut are dropped, overlapping spans merge, and kept
/// slivers shorter than the minimum keep length between a span and any neighbouring cut are
/// bridged so a filler next to a silence cut does not leave a stutter. A previous run's
/// <see cref="SegmentOrigin.Filler"/> cuts are ignored entirely — both as "already cut" and as
/// bridge targets — because <see cref="SegmentList.ApplyFillerCuts"/> dissolves them before
/// carving the cuts this method returns, so treating them as still there would either drop a span
/// that is about to lose its cut or stretch a new span onto an edge that is about to vanish. Pure.
/// </summary>
public static class FillerCutPlanner
{
    public static IReadOnlyList<PlannedCut> Plan(
        IEnumerable<FillerSpan> spans,
        IReadOnlyList<Segment> current,
        DetectionSettings settings,
        double duration)
    {
        ArgumentNullException.ThrowIfNull(spans);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(duration);

        var minKeep = settings.MinKeepMs / 1000.0;

        // 1. Clamp, drop empty, drop anything touching a manual segment or already cut.
        var candidates = spans
            .Select(s => s with { Start = Math.Max(0, s.Start), End = Math.Min(duration, s.End) })
            .Where(s => s.End > s.Start)
            .Where(s => !IntersectsManual(current, s.Start, s.End))
            .Where(s => !IsAlreadyCut(current, s.Start, s.End))
            .OrderBy(s => s.Start)
            .ToList();

        // 2. Merge overlapping or touching spans.
        var merged = new List<FillerSpan>(candidates.Count);
        foreach (var span in candidates)
        {
            if (merged.Count > 0 && span.Start <= merged[^1].End)
            {
                merged[^1] = merged[^1] with { End = Math.Max(merged[^1].End, span.End), Word = merged[^1].Word + " " + span.Word };
            }
            else
            {
                merged.Add(span);
            }
        }

        // 3. Bridge short kept slivers to the neighbouring existing cut or timeline end.
        var disabledEnds = current.Where(s => !s.Enabled && s.Origin != SegmentOrigin.Filler).Select(s => s.End).Append(0.0).ToList();
        var disabledStarts = current.Where(s => !s.Enabled && s.Origin != SegmentOrigin.Filler).Select(s => s.Start).Append(duration).ToList();
        for (var i = 0; i < merged.Count; i++)
        {
            var span = merged[i];
            var prevEdge = disabledEnds.Where(e => e <= span.Start).DefaultIfEmpty(double.NegativeInfinity).Max();
            var before = span.Start - prevEdge;
            if (before > 0 && before < minKeep && !IntersectsManual(current, prevEdge, span.Start))
            {
                span = span with { Start = prevEdge };
            }

            var nextEdge = disabledStarts.Where(e => e >= span.End).DefaultIfEmpty(double.PositiveInfinity).Min();
            var after = nextEdge - span.End;
            if (after > 0 && after < minKeep && !IntersectsManual(current, span.End, nextEdge))
            {
                span = span with { End = nextEdge };
            }

            merged[i] = span;
        }

        // 4. Bridge short kept slivers between consecutive spans.
        var bridged = new List<FillerSpan>(merged.Count);
        foreach (var span in merged)
        {
            if (bridged.Count > 0
                && span.Start - bridged[^1].End < minKeep
                && !IntersectsManual(current, bridged[^1].End, span.Start))
            {
                bridged[^1] = bridged[^1] with { End = Math.Max(bridged[^1].End, span.End), Word = bridged[^1].Word + " " + span.Word };
            }
            else
            {
                bridged.Add(span);
            }
        }

        return bridged.Select(s => new PlannedCut(s.Start, s.End, "filler: " + s.Word)).ToList();
    }

    private static bool IntersectsManual(IReadOnlyList<Segment> segments, double start, double end)
        => segments.Any(s => s.Origin == SegmentOrigin.Manual && s.Start < end && s.End > start);

    /// <summary>
    /// True when every segment overlapping <c>[start, end)</c> is disabled and not a previous
    /// <see cref="SegmentOrigin.Filler"/> cut (those are about to be dissolved and replaced, so
    /// they must not count as already cut). Segments partition the timeline, so ignoring the
    /// origin check, this means the whole span is cut.
    /// </summary>
    private static bool IsAlreadyCut(IReadOnlyList<Segment> segments, double start, double end)
        => segments.Where(s => s.Start < end && s.End > start).All(s => !s.Enabled && s.Origin != SegmentOrigin.Filler);
}

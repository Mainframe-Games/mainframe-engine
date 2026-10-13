using System.Globalization;
using MainframeEngine.Video.Capture;
using MainframeEngine.Video.Models;

namespace MainframeEngine.Video.Detection;

/// <summary>
/// Turns a Mainframe Engine recording's sidecar into cuts. Idle stretches (no player input, live
/// recordings only) longer than the minimum are cut with padding; loading screens, pauses and the
/// game's Skip spans are cut exactly; Keep spans and the windows around markers are never cut for
/// idleness, and Keep also beats Skip. Results are merged with the shared minimum-keep rules, then
/// manual segments and Keep spans are clipped out of the merged cuts, so a re-run never undoes the
/// user's edits and merging can never cut a Keep span. Pure.
/// </summary>
public static class GameplayCutPlanner
{
    private const string IdleKind = "idle";

    private sealed record Piece(double Start, double End, string Kind);

    public static IReadOnlyList<PlannedCut> Plan(
        CaptureSidecar capture,
        IReadOnlyList<Segment> current,
        GameplaySettings settings,
        double duration)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(duration);

        var pad = settings.PaddingMs / 1000.0;
        var minIdle = settings.MinIdleMs / 1000.0;
        var minKeep = settings.MinKeepMs / 1000.0;
        var window = settings.MarkerWindowMs / 1000.0;

        var keep = Intervals.Clamp(capture.Keep.Select(Range), duration).ToList();
        var pieces = new List<Piece>();

        // Skip-like ranges first, so a merged cut's reason names them before "idle".
        AddExact(pieces, capture.Loading, _ => "loading", keep, duration);
        AddExact(pieces, capture.Paused, _ => "paused", keep, duration);
        AddExact(pieces, capture.Skip, s => s.Label is { Length: > 0 } label ? "skip: " + label : "skip", keep, duration);

        if (capture.Mode == CaptureMode.Live)
        {
            var protectedRanges = keep.Concat(capture.Markers.Select(m => (m.Time - window, m.Time + window)));
            var idle = Intervals.Subtract(Intervals.Complement(Intervals.Clamp(capture.Input.Select(Range), duration), duration), protectedRanges);
            foreach (var (start, end) in idle.Where(i => i.End - i.Start >= minIdle))
            {
                var (s, e) = (Math.Max(0, start + pad), Math.Min(duration, end - pad));
                if (e > s)
                {
                    pieces.Add(new Piece(s, e, IdleKind));
                }
            }
        }

        var merged = CutMerger.Merge(pieces.Select(p => (p.Start, p.End)), minKeep, duration);
        var manual = current.Where(s => s.Origin == SegmentOrigin.Manual).Select(s => (s.Start, s.End)).ToList();

        // Keep was subtracted from the pieces, but merging bridges short kept gaps and absorbs slivers
        // at the file's edges, which can put a Keep span back inside a cut. Subtract it again.
        var result = new List<PlannedCut>();
        foreach (var (start, end) in Intervals.Subtract(merged, manual.Concat(keep)))
        {
            if (end - start >= SegmentList.MinSegmentLength)
            {
                result.Add(new PlannedCut(start, end, Reason(pieces, start, end)));
            }
        }

        return result;
    }

    private static (double Start, double End) Range(CaptureSpan s) => (s.Start, s.End);

    private static void AddExact(List<Piece> pieces, IEnumerable<CaptureSpan> spans, Func<CaptureSpan, string> kind,
        List<(double Start, double End)> keep, double duration)
    {
        foreach (var span in spans)
        {
            foreach (var (s, e) in Intervals.Subtract(Intervals.Clamp([Range(span)], duration), keep))
            {
                pieces.Add(new Piece(s, e, kind(span)));
            }
        }
    }

    private static string Reason(List<Piece> pieces, double start, double end)
    {
        // An idle piece wholly inside an exact cut (a pause with no input) is the same footage; name it once.
        var exact = pieces.Where(p => p.Kind != IdleKind).Select(p => (p.Start, p.End)).ToList();
        var kinds = pieces
            .Where(p => p.Start < end && p.End > start)
            .Where(p => p.Kind != IdleKind || Intervals.Subtract([(p.Start, p.End)], exact).Count > 0)
            .OrderBy(p => p.Start)
            .Select(p => p.Kind)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var label = kinds.Count > 0 ? string.Join(" + ", kinds) : "gameplay";
        return string.Create(CultureInfo.InvariantCulture, $"{label} {end - start:0.0}s");
    }
}

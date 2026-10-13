using System.Globalization;
using MainframeEngine.Video.Models;

namespace MainframeEngine.Video.Detection;

/// <summary>
/// Turns raw silence spans into cuts: shrinks each by the padding so consonants at the edges
/// survive, then absorbs kept fragments shorter than the minimum keep length so the result does
/// not machine-gun. Pure; no ffmpeg here.
/// </summary>
public static class SilenceCutPlanner
{
    public static IReadOnlyList<PlannedCut> Plan(IEnumerable<SilenceSpan> spans, DetectionSettings settings, double duration)
    {
        ArgumentNullException.ThrowIfNull(spans);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(duration);

        var pad = settings.PaddingMs / 1000.0;
        var minKeep = settings.MinKeepMs / 1000.0;

        // 1. Pad and clamp; drop anything that vanishes.
        var cuts = spans
            .Select(s => (Start: Math.Max(0, s.Start + pad), End: Math.Min(duration, s.End - pad)))
            .Where(c => c.End > c.Start)
            .OrderBy(c => c.Start)
            .ToList();

        // 2. Merge, bridge short kept gaps, absorb slivers at the ends.
        var merged = CutMerger.Merge(cuts, minKeep, duration);

        return merged
            .Select(c => new PlannedCut(c.Start, c.End, string.Create(CultureInfo.InvariantCulture, $"silence {c.End - c.Start:0.00}s")))
            .ToList();
    }
}

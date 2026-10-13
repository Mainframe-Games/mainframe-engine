namespace MainframeEngine.Video.Detection;

/// <summary>
/// The cut-merging rules every planner shares: overlapping or touching cuts join, kept gaps shorter
/// than the minimum keep length are bridged, and a short kept sliver at either end of the file is
/// absorbed. Pure.
/// </summary>
internal static class CutMerger
{
    public static List<(double Start, double End)> Merge(IEnumerable<(double Start, double End)> cuts, double minKeep, double duration)
    {
        var merged = new List<(double Start, double End)>();
        foreach (var cut in cuts.OrderBy(c => c.Start))
        {
            if (merged.Count > 0 && cut.Start - merged[^1].End < minKeep)
            {
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, cut.End));
            }
            else
            {
                merged.Add(cut);
            }
        }

        // A short kept sliver at either end of the file is just as bad as one in the middle.
        if (merged.Count > 0)
        {
            if (merged[0].Start > 0 && merged[0].Start < minKeep)
            {
                merged[0] = (0, merged[0].End);
            }

            if (merged[^1].End < duration && duration - merged[^1].End < minKeep)
            {
                merged[^1] = (merged[^1].Start, duration);
            }
        }

        return merged;
    }
}

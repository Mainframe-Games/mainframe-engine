namespace MainframeEngine.Video.Detection;

/// <summary>Set operations on half-open time ranges, in seconds. Inputs may be unsorted and overlapping; outputs never are. Pure.</summary>
internal static class Intervals
{
    public static List<(double Start, double End)> Normalize(IEnumerable<(double Start, double End)> spans)
    {
        var result = new List<(double Start, double End)>();
        foreach (var s in spans.Where(s => s.End > s.Start).OrderBy(s => s.Start))
        {
            if (result.Count > 0 && s.Start <= result[^1].End)
            {
                result[^1] = (result[^1].Start, Math.Max(result[^1].End, s.End));
            }
            else
            {
                result.Add(s);
            }
        }

        return result;
    }

    /// <summary>Clamps every span to <c>[0, duration]</c> and drops what vanishes.</summary>
    public static IEnumerable<(double Start, double End)> Clamp(IEnumerable<(double Start, double End)> spans, double duration)
        => spans.Select(s => (Start: Math.Max(0, s.Start), End: Math.Min(duration, s.End))).Where(s => s.End > s.Start);

    /// <summary>The parts of <c>[0, duration]</c> not covered by <paramref name="spans"/>.</summary>
    public static List<(double Start, double End)> Complement(IEnumerable<(double Start, double End)> spans, double duration)
        => Subtract([(0, duration)], spans);

    /// <summary>The parts of <paramref name="from"/> not covered by <paramref name="remove"/>.</summary>
    public static List<(double Start, double End)> Subtract(IEnumerable<(double Start, double End)> from, IEnumerable<(double Start, double End)> remove)
    {
        var holes = Normalize(remove);
        var result = new List<(double Start, double End)>();
        foreach (var (start, end) in Normalize(from))
        {
            var cursor = start;
            foreach (var hole in holes)
            {
                if (hole.End <= cursor)
                {
                    continue;
                }

                if (hole.Start >= end)
                {
                    break;
                }

                if (hole.Start > cursor)
                {
                    result.Add((cursor, hole.Start));
                }

                cursor = Math.Max(cursor, hole.End);
            }

            if (cursor < end)
            {
                result.Add((cursor, end));
            }
        }

        return result;
    }
}

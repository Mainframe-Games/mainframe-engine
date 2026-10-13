using System.Globalization;
using MainframeEngine.Video.Models;

namespace MainframeEngine.Video.Media.Export;

/// <summary>
/// Fast export copies streams without re-encoding, so a copied range must <em>begin</em> on a keyframe
/// (it can stop anywhere, since frames only reference earlier ones). Each kept range's start is moved
/// back to the preceding keyframe and its end is left exact, so the output only ever includes extra
/// material and never drops something the user kept. Ranges that come to overlap are merged, which
/// means a short cut just before a long GOP can disappear entirely. That is the approximation the
/// UI has to label.
/// </summary>
public static class KeyframeSnapper
{
    /// <param name="segments">The project's segments; only enabled ones matter.</param>
    /// <param name="keyframes">Sorted keyframe timestamps in seconds. Empty means "unknown"; ranges are then left alone.</param>
    /// <param name="duration">Media duration; ends are clamped to it.</param>
    public static IReadOnlyList<(double Start, double End)> Snap(IReadOnlyList<Segment> segments, IReadOnlyList<double> keyframes, double duration)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(keyframes);

        var result = new List<(double Start, double End)>();
        foreach (var s in segments.Where(s => s.Enabled).OrderBy(s => s.Start))
        {
            var start = keyframes.Count == 0 ? s.Start : PrecedingKeyframe(keyframes, s.Start);
            var end = Math.Min(s.End, duration);
            if (end <= start)
            {
                continue;
            }

            if (result.Count > 0 && result[^1].End >= start)
            {
                result[^1] = (result[^1].Start, Math.Max(result[^1].End, end));
            }
            else
            {
                result.Add((start, end));
            }
        }

        return result;
    }

    /// <summary>
    /// Parses <c>ffprobe -show_entries packet=pts_time,flags -of csv=p=0</c> output, one line per
    /// packet, keeping the timestamps of packets flagged as keyframes.
    /// </summary>
    public static IReadOnlyList<double> ParseKeyframes(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var result = new List<double>();
        foreach (var line in lines)
        {
            var comma = line.IndexOf(',');
            if (comma < 0)
            {
                continue;
            }

            var flags = line.AsSpan(comma + 1);
            if (!flags.Contains('K'))
            {
                continue;
            }

            if (double.TryParse(line.AsSpan(0, comma), NumberStyles.Float, CultureInfo.InvariantCulture, out var t))
            {
                result.Add(t);
            }
        }

        result.Sort();
        return result;
    }

    /// <summary>The last keyframe at or before <paramref name="time"/>, or 0 if there is none (the first packet is always a keyframe).</summary>
    private static double PrecedingKeyframe(IReadOnlyList<double> keyframes, double time)
    {
        var lo = 0;
        var hi = keyframes.Count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) >> 1;
            if (keyframes[mid] <= time)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return keyframes[lo] <= time ? keyframes[lo] : 0.0;
    }
}

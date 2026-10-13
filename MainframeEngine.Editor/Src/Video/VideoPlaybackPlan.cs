using MainframeEngine.Video;
using MainframeEngine.Video.Models;

namespace MainframeEngine.Editor.Video;

/// <summary>A kept stretch of the source, in source seconds.</summary>
public readonly record struct PlayRange(double Start, double End)
{
    public double Length => End - Start;
}

/// <summary>
/// What the preview plays from a given playhead: the kept segments from there on, laid end to end on an output timeline.
/// The audio voice counts output seconds, and this plan maps them back to the source frame to show. The next range's
/// video decoder starts <see cref="PrefetchSeconds"/> before the current range ends, so the switch has no gap.
/// </summary>
public sealed class VideoPlaybackPlan
{
    public const double PrefetchSeconds = 1.0;

    private readonly PlayRange[] _ranges;
    private readonly double[] _outputStarts;

    private VideoPlaybackPlan(PlayRange[] ranges)
    {
        _ranges = ranges;
        _outputStarts = new double[ranges.Length];
        var t = 0.0;
        for (var i = 0; i < ranges.Length; i++)
        {
            _outputStarts[i] = t;
            t += ranges[i].Length;
        }

        OutputDuration = t;
    }

    public IReadOnlyList<PlayRange> Ranges => _ranges;

    public double OutputDuration { get; }

    public static VideoPlaybackPlan Build(IReadOnlyList<Segment> segments, double from)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var ranges = new List<PlayRange>();
        foreach (var segment in segments)
        {
            if (!segment.Enabled || segment.End <= from)
                continue;
            var start = Math.Max(segment.Start, from);
            if (segment.End - start < SegmentList.MinSegmentLength)
                continue;
            // Kept segments that meet in the source (a split) play as one range: no decoder restart at a seamless join.
            if (ranges.Count > 0 && ranges[^1].End == start)
                ranges[^1] = ranges[^1] with { End = segment.End };
            else
                ranges.Add(new PlayRange(start, segment.End));
        }

        return new VideoPlaybackPlan([.. ranges]);
    }

    public double OutputStartOf(int range) => _outputStarts[range];

    public bool IsFinished(double output) => output >= OutputDuration;

    /// <summary>
    /// The range playing at <paramref name="output"/>: the later one at an exact join, the first before zero, and -1 once
    /// finished or when there is nothing to play. A binary search, because the preview asks every frame.
    /// </summary>
    public int RangeAt(double output)
    {
        if (_ranges.Length == 0 || IsFinished(output))
            return -1;
        var i = Array.BinarySearch(_outputStarts, output);
        // Not found: ~i is the first start after output, so the range before it holds output (before zero: the first).
        return i >= 0 ? i : Math.Max(0, ~i - 1);
    }

    public double SourceTimeAt(double output)
    {
        if (_ranges.Length == 0)
            return 0;
        output = Math.Max(0, output);
        var i = RangeAt(output);
        return i < 0 ? _ranges[^1].End : _ranges[i].Start + (output - _outputStarts[i]);
    }

    public bool ShouldPrefetch(int range, double output) =>
        range >= 0 && range + 1 < _ranges.Length && output >= _outputStarts[range] + _ranges[range].Length - PrefetchSeconds;
}

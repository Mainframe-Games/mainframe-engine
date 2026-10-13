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
            if (segment.End - start >= SegmentList.MinSegmentLength)
                ranges.Add(new PlayRange(start, segment.End));
        }

        return new VideoPlaybackPlan([.. ranges]);
    }

    public double OutputStartOf(int range) => _outputStarts[range];

    public bool IsFinished(double output) => output >= OutputDuration;

    public int RangeAt(double output)
    {
        if (IsFinished(output))
            return -1;
        for (var i = _ranges.Length - 1; i >= 0; i--)
            if (output >= _outputStarts[i])
                return i;
        return 0;
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

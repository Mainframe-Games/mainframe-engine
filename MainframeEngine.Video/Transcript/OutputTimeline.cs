using MainframeEngine.Video.Models;

namespace MainframeEngine.Video.Transcript;

/// <summary>
/// Maps source times to the times they will have in the exported video, which contains only the
/// enabled segments back to back. A time inside a cut maps to the start of the next kept region,
/// or to the output end if nothing is kept after it.
/// </summary>
public sealed class OutputTimeline
{
    private readonly double[] _starts;
    private readonly double[] _ends;
    private readonly double[] _offsets;

    public OutputTimeline(IReadOnlyList<Segment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var kept = segments.Where(s => s.Enabled).OrderBy(s => s.Start).ToList();
        _starts = new double[kept.Count];
        _ends = new double[kept.Count];
        _offsets = new double[kept.Count];

        var offset = 0.0;
        for (var i = 0; i < kept.Count; i++)
        {
            _starts[i] = kept[i].Start;
            _ends[i] = kept[i].End;
            _offsets[i] = offset;
            offset += kept[i].End - kept[i].Start;
        }

        OutputDuration = offset;
    }

    public double OutputDuration { get; }

    public double ToOutput(double sourceSeconds)
    {
        for (var i = 0; i < _starts.Length; i++)
        {
            if (sourceSeconds < _starts[i])
            {
                return _offsets[i];
            }

            if (sourceSeconds < _ends[i])
            {
                return _offsets[i] + (sourceSeconds - _starts[i]);
            }
        }

        return OutputDuration;
    }
}

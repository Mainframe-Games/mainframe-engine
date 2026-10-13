using System.Globalization;
using MainframeEngine.Video.Detection;

namespace MainframeEngine.Video.Media;

/// <summary>
/// Parses the stderr of ffmpeg's <c>silencedetect</c> filter line by line:
/// <code>
/// [silencedetect @ 0x...] silence_start: 3.02
/// [silencedetect @ 0x...] silence_end: 4.98 | silence_duration: 1.96
/// </code>
/// A silence still open at end of input is closed at the media duration.
/// </summary>
public sealed class SilenceDetectParser
{
    private const string StartKey = "silence_start:";
    private const string EndKey = "silence_end:";

    private readonly double _durationSeconds;
    private readonly List<SilenceSpan> _spans = [];
    private double? _openStart;

    public SilenceDetectParser(double durationSeconds)
    {
        _durationSeconds = durationSeconds;
    }

    public void Feed(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (!line.Contains("silencedetect", StringComparison.Ordinal))
        {
            return;
        }

        if (TryReadValue(line, StartKey, out var start))
        {
            _openStart = start;
        }
        else if (TryReadValue(line, EndKey, out var end) && _openStart is { } open)
        {
            _spans.Add(new SilenceSpan(open, end));
            _openStart = null;
        }
    }

    public IReadOnlyList<SilenceSpan> Finish()
    {
        if (_openStart is { } open && open < _durationSeconds)
        {
            _spans.Add(new SilenceSpan(open, _durationSeconds));
            _openStart = null;
        }

        return _spans;
    }

    private static bool TryReadValue(string line, string key, out double value)
    {
        value = 0;
        var at = line.IndexOf(key, StringComparison.Ordinal);
        if (at < 0)
        {
            return false;
        }

        var rest = line.AsSpan(at + key.Length).TrimStart();
        var end = rest.IndexOf(' ');
        var token = end < 0 ? rest : rest[..end];
        return double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}

using System.Globalization;

namespace MainframeEngine.Video.Media;

/// <summary>
/// Parses the key=value lines ffmpeg writes to <c>-progress pipe:1</c> and turns them into a
/// fraction of <c>totalSeconds</c>. Honest progress, not a guess.
/// </summary>
public sealed class FfmpegProgressParser
{
    private readonly double _totalSeconds;

    public FfmpegProgressParser(double totalSeconds)
    {
        _totalSeconds = totalSeconds;
    }

    public bool IsComplete { get; private set; }

    /// <returns>A fraction in <c>[0, 1]</c> if the line carried timing information, otherwise null.</returns>
    public double? Feed(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var eq = line.IndexOf('=');
        if (eq < 0)
        {
            return null;
        }

        var key = line.AsSpan(0, eq).Trim();
        var value = line.AsSpan(eq + 1).Trim();

        if (key.SequenceEqual("progress"))
        {
            if (value.SequenceEqual("end"))
            {
                IsComplete = true;
                return 1.0;
            }

            return null;
        }

        double seconds;
        if (key.SequenceEqual("out_time_us") || key.SequenceEqual("out_time_ms"))
        {
            // Both keys are microseconds; out_time_ms is a historical misnomer in ffmpeg.
            if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var us))
            {
                return null;
            }

            seconds = us / 1_000_000.0;
        }
        else if (key.SequenceEqual("out_time"))
        {
            if (!TimeSpan.TryParseExact(value, @"hh\:mm\:ss\.ffffff", CultureInfo.InvariantCulture, out var ts)
                && !TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out ts))
            {
                return null;
            }

            seconds = ts.TotalSeconds;
        }
        else
        {
            return null;
        }

        if (_totalSeconds <= 0 || seconds < 0)
        {
            return null;
        }

        return Math.Min(1.0, seconds / _totalSeconds);
    }
}

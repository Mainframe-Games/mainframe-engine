namespace MainframeEngine.Video.Playback;

/// <summary>
/// Smooths a playhead that the media player only reports a few times a second. Between reports
/// the position is assumed to advance at real-time rate from the last known point; a report that
/// lands behind the current estimate (the player's clock lags a little) is absorbed rather than
/// jerking the playhead backwards, while a report ahead of the estimate jumps forward to it. Only
/// a seek may move the playhead backwards. Pure; the caller supplies "now" in seconds from any
/// monotonic clock.
/// </summary>
public sealed class PlayheadInterpolator
{
    private double _base;
    private double _baseNow;
    private bool _running = true;
    private bool _hasReport;

    /// <summary>Upper bound for the estimate, in seconds. Zero until the media length is known.</summary>
    public double Duration { get; set; }

    /// <summary>A real position report from the player. Never moves the playhead backwards.</summary>
    public void Report(double position, double now)
    {
        var current = _hasReport ? Estimate(now) : 0;
        _hasReport = true;
        _base = Math.Max(position, current);
        _baseNow = now;
    }

    /// <summary>The playhead was deliberately moved; backwards is allowed.</summary>
    public void Seek(double position, double now)
    {
        _hasReport = true;
        _base = position;
        _baseNow = now;
    }

    /// <summary>Freezes the estimate where it is now.</summary>
    public void Pause(double now)
    {
        _base = Estimate(now);
        _baseNow = now;
        _running = false;
    }

    /// <summary>Playback continues from the frozen position.</summary>
    public void Resume(double now)
    {
        _baseNow = now;
        _running = true;
    }

    /// <summary>Where the playhead is at <paramref name="now"/>, clamped to <c>[0, Duration]</c>.</summary>
    public double Estimate(double now)
    {
        if (!_hasReport)
        {
            return 0;
        }

        var raw = _running ? _base + Math.Max(0, now - _baseNow) : _base;
        return Math.Clamp(raw, 0, Math.Max(0, Duration));
    }
}

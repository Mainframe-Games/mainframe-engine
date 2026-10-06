using System.Diagnostics;

namespace MainframeEngine;

/// <summary>Clocks for timing and logs: a monotonic tick count since the process started, and wall-clock Unix time.</summary>
public static class Time
{
    private static readonly long Start = Stopwatch.GetTimestamp();

    /// <summary>Milliseconds since the process started (monotonic; for differences, timeouts and log stamps).</summary>
    public static ulong TicksMsec => (ulong)Stopwatch.GetElapsedTime(Start).TotalMilliseconds;

    /// <summary>Microseconds since the process started (monotonic).</summary>
    public static ulong TicksUsec => (ulong)(Stopwatch.GetElapsedTime(Start).Ticks / (TimeSpan.TicksPerMillisecond / 1000));

    /// <summary>Seconds since the Unix epoch, UTC, with fractions (wall clock: can jump).</summary>
    public static double UnixTime => (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;
}

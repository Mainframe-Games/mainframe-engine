using System.Globalization;

namespace MainframeEngine.Editor.Video;

/// <summary>
/// Time labels for video mode, formatted with the invariant culture so a status line reads the same on every machine.
/// Both round to the nearest tick (a ruler tick at 74.9999 s must read 1:15, not 1:14).
/// </summary>
public static class VideoFormat
{
    /// <summary><c>m:ss.t</c> under an hour, <c>h:mm:ss.t</c> from an hour: durations in status lines and the transport.</summary>
    public static string Clock(double seconds)
    {
        var tenths = Ticks(seconds, 10);
        var (t, s, m, h) = (tenths % 10, tenths / 10 % 60, tenths / 600 % 60, tenths / 36000);
        return h > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{h}:{m:00}:{s:00}.{t}")
            : string.Create(CultureInfo.InvariantCulture, $"{m}:{s:00}.{t}");
    }

    /// <summary>
    /// <see cref="Clock"/> written into <paramref name="destination"/> without allocating (the toolbar's position ticks ten
    /// times a second while playing); returns the characters written. 16 characters always suffice for an hour or more.
    /// </summary>
    public static int FormatClock(Span<char> destination, double seconds)
    {
        var tenths = Ticks(seconds, 10);
        var (t, s, m, h) = (tenths % 10, tenths / 10 % 60, tenths / 600 % 60, tenths / 36000);
        var n = 0;
        if (h > 0)
        {
            n += Digits(destination, h, 1);
            destination[n++] = ':';
            n += Digits(destination[n..], m, 2);
        }
        else
        {
            n += Digits(destination, m, 1);
        }

        destination[n++] = ':';
        n += Digits(destination[n..], s, 2);
        destination[n++] = '.';
        destination[n++] = (char)('0' + t);
        return n;
    }

    // value in decimal, zero-padded to at least minDigits.
    private static int Digits(Span<char> destination, long value, int minDigits)
    {
        var count = 1;
        for (var v = value / 10; v > 0; v /= 10)
            count++;
        count = Math.Max(count, minDigits);
        for (var i = count - 1; i >= 0; i--, value /= 10)
            destination[i] = (char)('0' + value % 10);
        return count;
    }

    /// <summary>Compact ruler label: <c>m:ss</c>, or <c>h:mm:ss</c> from an hour. No fraction.</summary>
    public static string Ruler(double seconds)
    {
        var whole = Ticks(seconds, 1);
        var (s, m, h) = (whole % 60, whole / 60 % 60, whole / 3600);
        return h > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{h}:{m:00}:{s:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{m}:{s:00}");
    }

    private static long Ticks(double seconds, int perSecond) =>
        double.IsFinite(seconds) ? (long)Math.Round(Math.Max(0, seconds) * perSecond, MidpointRounding.AwayFromZero) : 0;
}

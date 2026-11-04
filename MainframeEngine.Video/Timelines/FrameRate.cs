using System.Globalization;
using System.Text.Json.Serialization;

namespace MainframeEngine.Video.Timelines;

/// <summary>
/// A timeline's frame rate as a ratio (<c>30/1</c>, <c>30000/1001</c>), so NTSC rates stay exact. Every edit snaps its times
/// to this grid (<see cref="Snap"/>): clips start on frames and last whole frames.
/// </summary>
public readonly record struct FrameRate
{
    [JsonConstructor]
    public FrameRate(int numerator, int denominator)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(numerator);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(denominator);
        Numerator = numerator;
        Denominator = denominator;
    }

    public int Numerator { get; }

    public int Denominator { get; }

    /// <summary>Frames per second.</summary>
    [JsonIgnore]
    public double Fps => (double)Numerator / Denominator;

    /// <summary>Seconds per frame.</summary>
    [JsonIgnore]
    public double FrameDuration => (double)Denominator / Numerator;

    /// <summary>The frame nearest to <paramref name="seconds"/>.</summary>
    public long FrameAt(double seconds) => (long)Math.Round(seconds * Numerator / Denominator, MidpointRounding.AwayFromZero);

    /// <summary>The start of frame <paramref name="frame"/>, in seconds.</summary>
    public double TimeOf(long frame) => (double)frame * Denominator / Numerator;

    /// <summary><paramref name="seconds"/> moved to the nearest frame boundary.</summary>
    public double Snap(double seconds) => TimeOf(FrameAt(seconds));

    /// <summary>The usual ratio for a measured rate: 23.976, 29.97 and 59.94 become their NTSC ratios, others round to whole frames.</summary>
    public static FrameRate FromFps(double fps)
    {
        if (!(fps > 0) || double.IsInfinity(fps))
            throw new ArgumentOutOfRangeException(nameof(fps), fps, "A frame rate is a positive number.");
        foreach (var whole in (ReadOnlySpan<int>)[24, 30, 60, 120])
            if (Math.Abs(fps - whole * 1000.0 / 1001) < 0.005)
                return new FrameRate(whole * 1000, 1001);
        return new FrameRate(Math.Max(1, (int)Math.Round(fps)), 1);
    }

    /// <summary>The value ffmpeg takes for <c>r=</c> and <c>fps=</c>: <c>30</c> or <c>30000/1001</c>.</summary>
    public string ToFfmpeg() => Denominator == 1
        ? Numerator.ToString(CultureInfo.InvariantCulture)
        : string.Create(CultureInfo.InvariantCulture, $"{Numerator}/{Denominator}");

    public override string ToString() => ToFfmpeg();
}

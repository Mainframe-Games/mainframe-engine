using System.Globalization;

namespace MainframeEngine.Video.Media.Export;

/// <summary>
/// What every ffmpeg graph the library builds shares: Cutback's splice fade and the number format. The timeline's graph is
/// <see cref="Timelines.Export.TimelineGraphBuilder"/>; the one-recording graph this class built went with the old video
/// mode (ADR 0241).
/// </summary>
public static class FilterGraphBuilder
{
    /// <summary>Fade at each cut so the splice is not an audible click.</summary>
    public const double FadeSeconds = 0.008;

    /// <summary>Microsecond precision, invariant culture, never scientific notation.</summary>
    public static string F(double seconds) => Math.Round(seconds, 6).ToString("0.######", CultureInfo.InvariantCulture);
}

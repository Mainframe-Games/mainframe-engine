namespace MainframeEngine.RenderTests;

/// <summary>Result of comparing a rendered frame against its golden image.</summary>
public sealed record ImageComparisonResult(
    bool SizeMatches,
    int DifferingPixels,
    int TotalPixels,
    int MaxChannelDelta,
    byte[]? DiffImage)
{
    public double DifferingPercent => TotalPixels == 0 ? 0 : 100.0 * DifferingPixels / TotalPixels;
}

/// <summary>
/// Golden-image comparison: a pixel differs when any RGBA channel differs by more than
/// <c>channelTolerance</c>; the images match when the share of differing pixels is at most
/// <c>maxDifferingPercent</c>. Tolerances absorb rasteriser rounding, not visual changes.
/// </summary>
public static class ImageComparison
{
    public const int DefaultChannelTolerance = 4;
    public const double DefaultMaxDifferingPercent = 0.5;

    public static ImageComparisonResult Compare(PngImage expected, PngImage actual, int channelTolerance = DefaultChannelTolerance)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);

        if (expected.Width != actual.Width || expected.Height != actual.Height)
            return new ImageComparisonResult(false, expected.Width * expected.Height, expected.Width * expected.Height, 255, null);

        var e = expected.Pixels;
        var a = actual.Pixels;
        var diff = new byte[e.Length];
        int differing = 0, maxDelta = 0;

        for (var i = 0; i < e.Length; i += 4)
        {
            var delta = Math.Max(
                Math.Max(Math.Abs(e[i] - a[i]), Math.Abs(e[i + 1] - a[i + 1])),
                Math.Max(Math.Abs(e[i + 2] - a[i + 2]), Math.Abs(e[i + 3] - a[i + 3])));
            maxDelta = Math.Max(maxDelta, delta);

            if (delta > channelTolerance)
            {
                // Differences in solid red, scaled up so small deltas are still visible.
                differing++;
                diff[i] = (byte)Math.Min(255, 128 + delta * 4);
                diff[i + 1] = 0;
                diff[i + 2] = 0;
            }
            else
            {
                // Matching pixels as dimmed greyscale for context.
                var grey = (byte)((e[i] * 77 + e[i + 1] * 150 + e[i + 2] * 29) >> 10);
                diff[i] = diff[i + 1] = diff[i + 2] = grey;
            }

            diff[i + 3] = 255;
        }

        return new ImageComparisonResult(true, differing, e.Length / 4, maxDelta, diff);
    }

    public static bool IsMatch(ImageComparisonResult result, double maxDifferingPercent = DefaultMaxDifferingPercent)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.SizeMatches && result.DifferingPercent <= maxDifferingPercent;
    }
}

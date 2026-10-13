using System.Globalization;
using MainframeEngine.Video.Media.Export;

namespace MainframeEngine.Video.Media.Preview;

/// <summary>
/// ffmpeg arguments for the editor's gapless preview. Each kept range gets one video decode (raw RGBA frames on stdout)
/// and one audio decode (32-bit float stereo on stdout); scrubbing decodes a single frame. Every filter is a fixed size,
/// so passing it inline cannot grow with the cut count. That is unlike the export graph, which goes in a file. Audio gets
/// the export's 8 ms fades, so the preview sounds like the result. <see cref="FfmpegProcess.Start"/> adds the preamble.
/// </summary>
public static class PreviewDecoding
{
    /// <summary>Preview frames are at most this wide: enough for the preview pane, at a quarter of the upload cost of 1080p.</summary>
    public const int MaxWidth = 960;

    /// <summary>Preview frames are decoded at no more than this rate.</summary>
    public const double MaxFrameRate = 30;

    /// <summary>The preview size: at most <paramref name="maxWidth"/> wide, the source's aspect, both sides even.</summary>
    public static (int Width, int Height) PreviewSize(int sourceWidth, int sourceHeight, int maxWidth = MaxWidth)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceHeight);
        // The width rounds down so the preview is never wider than the source or the cap; the height rounds to the nearest even.
        var width = Math.Max(2, Math.Min(sourceWidth, maxWidth) / 2 * 2);
        return (width, Even(sourceHeight * (double)width / sourceWidth));
    }

    /// <summary>The source rate capped at <see cref="MaxFrameRate"/>. An unknown rate previews at the cap.</summary>
    public static double PreviewFrameRate(double sourceFrameRate) =>
        double.IsFinite(sourceFrameRate) && sourceFrameRate > 0 ? Math.Min(sourceFrameRate, MaxFrameRate) : MaxFrameRate;

    /// <summary>Raw RGBA frames of [<paramref name="start"/>, <paramref name="end"/>) at the preview size and rate.</summary>
    public static IReadOnlyList<string> VideoArguments(string source, double start, double end, int width, int height, double frameRate)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        var length = Length(start, end);
        return
        [
            "-ss", FilterGraphBuilder.F(start), "-i", source, "-t", FilterGraphBuilder.F(length), "-an",
            "-vf", $"fps={FilterGraphBuilder.F(frameRate)},scale={width}:{height}:flags=bilinear",
            "-pix_fmt", "rgba", "-f", "rawvideo", "-",
        ];
    }

    /// <summary>Interleaved stereo float samples of [<paramref name="start"/>, <paramref name="end"/>), faded at each end.</summary>
    public static IReadOnlyList<string> AudioArguments(string source, double start, double end, int sampleRate)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        var length = Length(start, end);
        var fade = Math.Min(FilterGraphBuilder.FadeSeconds, length / 2);
        var f = FilterGraphBuilder.F(fade);
        return
        [
            "-ss", FilterGraphBuilder.F(start), "-i", source, "-t", FilterGraphBuilder.F(length), "-vn",
            "-af", $"afade=t=in:st=0:d={f},afade=t=out:st={FilterGraphBuilder.F(length - fade)}:d={f}",
            "-ac", "2", "-ar", sampleRate.ToString(CultureInfo.InvariantCulture), "-f", "f32le", "-",
        ];
    }

    /// <summary>One raw RGBA frame at <paramref name="time"/>, for scrubbing while paused.</summary>
    public static IReadOnlyList<string> FrameArguments(string source, double time, int width, int height)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        return
        [
            "-ss", FilterGraphBuilder.F(Math.Max(0, time)), "-i", source, "-frames:v", "1", "-an",
            "-vf", $"scale={width}:{height}:flags=bilinear", "-pix_fmt", "rgba", "-f", "rawvideo", "-",
        ];
    }

    private static double Length(double start, double end)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        if (!(end > start))
            throw new ArgumentOutOfRangeException(nameof(end), end, "The range must end after it starts.");
        return end - start;
    }

    private static int Even(double value) => Math.Max(2, 2 * (int)Math.Round(value / 2, MidpointRounding.AwayFromZero));
}

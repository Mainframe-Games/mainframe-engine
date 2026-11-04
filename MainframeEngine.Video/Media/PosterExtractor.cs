using System.Globalization;
using MainframeEngine.Video.Media.Export;

namespace MainframeEngine.Video.Media;

/// <summary>A media pool poster: one frame of a video, a second in (or its middle when shorter), as a small PNG.</summary>
public static class PosterExtractor
{
    public static IReadOnlyList<string> Arguments(string source, double durationSeconds, int width, string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        var at = durationSeconds > 2 ? 1.0 : Math.Max(0, durationSeconds / 2);
        return ["-ss", FilterGraphBuilder.F(at), "-i", source, "-frames:v", "1", "-vf", $"scale={width.ToString(CultureInfo.InvariantCulture)}:-2",
            "-update", "1", "-y", outputPath];
    }

    /// <exception cref="FfmpegException">ffmpeg failed.</exception>
    public static async Task ExtractAsync(FfmpegLocation ffmpeg, string source, double durationSeconds, int width, string outputPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ffmpeg);
        using var process = FfmpegProcess.Start(ffmpeg.FfmpegPath, Arguments(source, durationSeconds, width, outputPath), redirectStandardOutput: false);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await FfmpegProcess.WaitForSuccessAsync(process, stderr, "Making a poster", cancellationToken).ConfigureAwait(false);
    }
}

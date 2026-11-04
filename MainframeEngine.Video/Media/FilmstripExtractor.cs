using System.Globalization;
using MainframeEngine.Video.Media.Export;

namespace MainframeEngine.Video.Media;

/// <summary>
/// A clip's filmstrip: <c>frames</c> thumbnails of a video, evenly spaced, tiled left to right into one PNG at
/// <c>height</c> pixels (the timeline draws it along a clip). One ffmpeg run per file, cached by the caller.
/// </summary>
public static class FilmstripExtractor
{
    /// <summary>The ffmpeg arguments: <c>fps=frames/duration,scale=-2:height,tile=frames×1</c>, one output frame.</summary>
    public static IReadOnlyList<string> Arguments(string source, double durationSeconds, int frames, int height, string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frames);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (!(durationSeconds > 0))
            throw new ArgumentOutOfRangeException(nameof(durationSeconds), durationSeconds, "The duration is positive.");
        var count = frames.ToString(CultureInfo.InvariantCulture);
        return
        [
            "-i", source,
            "-vf", $"fps={FilterGraphBuilder.F(frames / durationSeconds)},scale=-2:{height.ToString(CultureInfo.InvariantCulture)},tile={count}x1",
            "-frames:v", "1", "-update", "1", "-y", outputPath,
        ];
    }

    /// <summary>Writes the filmstrip PNG to <paramref name="outputPath"/>.</summary>
    /// <exception cref="FfmpegException">ffmpeg failed.</exception>
    public static async Task ExtractAsync(FfmpegLocation ffmpeg, string source, double durationSeconds, int frames, int height, string outputPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ffmpeg);
        using var process = FfmpegProcess.Start(ffmpeg.FfmpegPath, Arguments(source, durationSeconds, frames, height, outputPath), redirectStandardOutput: false);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await FfmpegProcess.WaitForSuccessAsync(process, stderr, "Making a filmstrip", cancellationToken).ConfigureAwait(false);
    }
}

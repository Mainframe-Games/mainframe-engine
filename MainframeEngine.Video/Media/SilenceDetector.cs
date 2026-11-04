using System.Globalization;
using MainframeEngine.Video.Detection;
using MainframeEngine.Video.Models;

namespace MainframeEngine.Video.Media;

/// <summary>
/// Runs ffmpeg's <c>silencedetect</c> filter over the source and returns the raw silence spans.
/// Turning spans into cuts is <see cref="SilenceCutPlanner"/>'s job, in Core.
/// </summary>
public sealed class SilenceDetector
{
    private readonly FfmpegLocation _ffmpeg;

    public SilenceDetector(FfmpegLocation ffmpeg)
    {
        ArgumentNullException.ThrowIfNull(ffmpeg);
        _ffmpeg = ffmpeg;
    }

    /// <param name="path">Source video.</param>
    /// <param name="durationSeconds">From the probe. Closes a trailing silence and drives progress.</param>
    /// <param name="settings">Only <see cref="DetectionSettings.SilenceThresholdDb"/> and <see cref="DetectionSettings.MinSilenceMs"/> are used here.</param>
    /// <param name="progress">Fraction complete.</param>
    /// <param name="cancellationToken">Kills ffmpeg when cancelled.</param>
    /// <param name="start">Where to start reading, in source seconds (a timeline clip's window).</param>
    /// <param name="end">Where to stop reading; null reads to the end. The spans come back in source time either way.</param>
    public async Task<IReadOnlyList<SilenceSpan>> DetectAsync(
        string path,
        double durationSeconds,
        DetectionSettings settings,
        IProgress<double>? progress,
        CancellationToken cancellationToken,
        double start = 0,
        double? end = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(settings);
        var spans = await DetectCoreAsync(Arguments(path, settings, start, end, durationSeconds), RangeLength(start, end, durationSeconds),
            progress, cancellationToken).ConfigureAwait(false);
        if (start <= 0)
            return spans;
        var shifted = new SilenceSpan[spans.Count];
        for (var i = 0; i < spans.Count; i++)
            shifted[i] = new SilenceSpan(spans[i].Start + start, spans[i].End + start);
        return shifted;
    }

    private static double RangeLength(double start, double? end, double duration) =>
        Math.Max(0, (end ?? duration) - Math.Max(0, start));

    /// <summary>The ffmpeg arguments: input seeking to <paramref name="start"/> and reading the range, when one is given.</summary>
    public static IReadOnlyList<string> Arguments(string path, DetectionSettings settings, double start, double? end, double duration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(settings);

        var filter = string.Create(
            CultureInfo.InvariantCulture,
            $"silencedetect=noise={settings.SilenceThresholdDb:0.##}dB:d={settings.MinSilenceMs / 1000.0:0.###}");

        var args = new List<string>();
        if (start > 0)
            args.AddRange(["-ss", start.ToString("0.###", CultureInfo.InvariantCulture)]);
        if (end is { } stop)
            args.AddRange(["-t", RangeLength(start, stop, duration).ToString("0.###", CultureInfo.InvariantCulture)]);
        args.AddRange(["-i", path, "-vn", "-af", filter, "-f", "null", "-progress", "pipe:1", "-"]);
        return args;
    }

    private async Task<IReadOnlyList<SilenceSpan>> DetectCoreAsync(IReadOnlyList<string> args, double durationSeconds,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        // silencedetect logs at "info" level; the preamble's "error" would hide it.
        using var process = FfmpegProcess.Start(_ffmpeg.FfmpegPath, args, redirectStandardOutput: true, logLevel: "info");

        var parser = new SilenceDetectParser(durationSeconds);
        var stderrTask = ReadStderrAsync(process, parser, cancellationToken);
        var progressParser = new FfmpegProgressParser(durationSeconds);

        try
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
            {
                if (progressParser.Feed(line) is { } fraction)
                {
                    progress?.Report(fraction);
                }
            }
        }
        catch (OperationCanceledException)
        {
            FfmpegProcess.TryKill(process);
            throw;
        }

        await FfmpegProcess.WaitForSuccessAsync(process, stderrTask, "Silence detection", cancellationToken).ConfigureAwait(false);
        progress?.Report(1.0);
        return parser.Finish();
    }

    private static async Task<string> ReadStderrAsync(System.Diagnostics.Process process, SilenceDetectParser parser, CancellationToken cancellationToken)
    {
        var all = new System.Text.StringBuilder();
        string? line;
        while ((line = await process.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
        {
            parser.Feed(line);
            if (all.Length < 64 * 1024)
            {
                all.AppendLine(line);
            }
        }

        return all.ToString();
    }
}

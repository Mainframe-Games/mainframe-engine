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
    public async Task<IReadOnlyList<SilenceSpan>> DetectAsync(
        string path,
        double durationSeconds,
        DetectionSettings settings,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(settings);

        var filter = string.Create(
            CultureInfo.InvariantCulture,
            $"silencedetect=noise={settings.SilenceThresholdDb:0.##}dB:d={settings.MinSilenceMs / 1000.0:0.###}");

        string[] args =
        [
            "-i", path,
            "-vn",
            "-af", filter,
            "-f", "null",
            "-progress", "pipe:1",
            "-",
        ];

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

using System.Buffers;

namespace MainframeEngine.Video.Media;

/// <summary>
/// Decodes the source's audio to 8 kHz mono s16le through an ffmpeg pipe and reduces it to
/// <see cref="Peak"/> buckets as it streams. Memory use is proportional to the number of peaks, not
/// the number of samples: roughly 7 MB per hour of audio at the default resolution.
/// </summary>
public sealed class WaveformExtractor
{
    public const int SampleRate = 8000;

    /// <summary>16 samples = 2 ms per bucket. At 2000 px wide that is a 4 s window at maximum zoom.</summary>
    public const int FinestSamplesPerBucket = 16;

    public const int LevelFactor = 4;

    /// <summary>16, 64, 256, 1024, 4096, 16384 samples per bucket: 2 ms up to ~2 s.</summary>
    public const int LevelCount = 6;

    private readonly FfmpegLocation _ffmpeg;

    public WaveformExtractor(FfmpegLocation ffmpeg)
    {
        ArgumentNullException.ThrowIfNull(ffmpeg);
        _ffmpeg = ffmpeg;
    }

    /// <param name="path">Source video. Opened read-only by ffmpeg.</param>
    /// <param name="expectedDurationSeconds">From <see cref="MediaProbe"/>; used only to report progress. Pass 0 if unknown.</param>
    /// <param name="progress">Fraction complete in <c>[0, 1]</c>.</param>
    /// <param name="cancellationToken">Kills ffmpeg when cancelled.</param>
    /// <exception cref="FfmpegException">ffmpeg failed, for example because the file has no audio stream.</exception>
    public async Task<Waveform> ExtractAsync(
        string path,
        double expectedDurationSeconds,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string[] args =
        [
            "-i", path,
            "-vn",
            "-ac", "1",
            "-ar", SampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-f", "s16le",
            "-acodec", "pcm_s16le",
            "-",
        ];

        using var process = FfmpegProcess.Start(_ffmpeg.FfmpegPath, args, redirectStandardOutput: true);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        var reducer = new PeakReducer(FinestSamplesPerBucket);
        var expectedBytes = expectedDurationSeconds > 0 ? expectedDurationSeconds * SampleRate * 2 : 0;
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            var stdout = process.StandardOutput.BaseStream;
            long totalBytes = 0;
            var lastReported = -1.0;
            int read;
            while ((read = await stdout.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                reducer.AddBytes(buffer.AsSpan(0, read));
                totalBytes += read;

                if (progress is not null && expectedBytes > 0)
                {
                    var fraction = Math.Min(1.0, totalBytes / expectedBytes);
                    if (fraction - lastReported >= 0.01)
                    {
                        lastReported = fraction;
                        progress.Report(fraction);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            FfmpegProcess.TryKill(process);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        await FfmpegProcess.WaitForSuccessAsync(process, stderrTask, "Reading the audio waveform", cancellationToken).ConfigureAwait(false);

        var peaks = reducer.Finish();
        if (peaks.Count == 0)
        {
            var stderr = await stderrTask.ConfigureAwait(false);
            throw new FfmpegException("The file produced no audio samples. It may have no audio track.", process.ExitCode, stderr);
        }

        progress?.Report(1.0);
        return Waveform.Build(peaks, FinestSamplesPerBucket, SampleRate, LevelFactor, LevelCount);
    }
}

using System.Buffers;
using System.Globalization;
using System.Runtime.InteropServices;

namespace MainframeEngine.Video.Media;

/// <summary>
/// Decodes the source's audio to 16 kHz mono 32-bit float PCM through an ffmpeg pipe, the input
/// Whisper expects, and returns the whole signal. Memory is 64 bytes per millisecond of audio,
/// about 230 MB per hour; speech recognition needs the full signal, so it is not streamed.
/// </summary>
public sealed class PcmExtractor
{
    public const int SampleRate = 16000;

    private readonly FfmpegLocation _ffmpeg;

    public PcmExtractor(FfmpegLocation ffmpeg)
    {
        ArgumentNullException.ThrowIfNull(ffmpeg);
        _ffmpeg = ffmpeg;
    }

    /// <param name="path">Source video. Opened read-only by ffmpeg.</param>
    /// <param name="expectedDurationSeconds">Used to size the buffer and report progress. Pass 0 if unknown.</param>
    /// <param name="progress">Fraction complete in <c>[0, 1]</c>.</param>
    /// <param name="cancellationToken">Kills ffmpeg when cancelled.</param>
    /// <exception cref="FfmpegException">ffmpeg failed, or the file produced no audio.</exception>
    public async Task<ReadOnlyMemory<float>> ExtractAsync(
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
            "-ar", SampleRate.ToString(CultureInfo.InvariantCulture),
            "-f", "f32le",
            "-acodec", "pcm_f32le",
            "-",
        ];

        using var process = FfmpegProcess.Start(_ffmpeg.FfmpegPath, args, redirectStandardOutput: true);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        var expectedSamples = expectedDurationSeconds > 0 ? (long)(expectedDurationSeconds * SampleRate) : 0;
        var samples = new float[Math.Clamp(expectedSamples + SampleRate, SampleRate, int.MaxValue - 64)];
        var count = 0;

        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        var carry = 0; // bytes of an incomplete float left over from the previous read
        try
        {
            var stdout = process.StandardOutput.BaseStream;
            var lastReported = -1.0;
            int read;
            while ((read = await stdout.ReadAsync(buffer.AsMemory(carry), cancellationToken).ConfigureAwait(false)) > 0)
            {
                var available = carry + read;
                var whole = available - (available % sizeof(float));
                var floats = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(0, whole));

                if (count + floats.Length > samples.Length)
                {
                    Array.Resize(ref samples, Math.Max(samples.Length * 2, count + floats.Length));
                }

                floats.CopyTo(samples.AsSpan(count));
                count += floats.Length;

                carry = available - whole;
                if (carry > 0)
                {
                    buffer.AsSpan(whole, carry).CopyTo(buffer);
                }

                if (progress is not null && expectedSamples > 0)
                {
                    var fraction = Math.Min(1.0, (double)count / expectedSamples);
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

        await FfmpegProcess.WaitForSuccessAsync(process, stderrTask, "Decoding audio for speech recognition", cancellationToken).ConfigureAwait(false);

        if (count == 0)
        {
            var stderr = await stderrTask.ConfigureAwait(false);
            throw new FfmpegException("The file produced no audio samples. It may have no audio track.", process.ExitCode, stderr);
        }

        progress?.Report(1.0);
        return samples.AsMemory(0, count);
    }
}

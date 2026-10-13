using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;

namespace MainframeEngine.Video.Media.Export;

/// <summary>
/// Chooses the H.264 encoder for a precise export, best first: libx264 (constant quality), then the platform's hardware
/// encoder, then libopenh264 (found in LGPL ffmpeg builds). Export therefore still works on an ffmpeg built without x264.
/// ffmpeg lists hardware encoders even on machines that lack the hardware, so each candidate is probe-encoded before use,
/// as the recorder's <c>EncoderSelector</c> does. The choice is cached per ffmpeg binary.
/// </summary>
public static class DeliveryEncoder
{
    public const string Software = "libx264";
    public const string Fallback = "libopenh264";

    private const double BitsPerPixel = 0.1;
    private const long MinBitrate = 2_000_000;
    private const long MaxBitrate = 80_000_000;
    private const long UnknownBitrate = 12_000_000;

    private static readonly ConcurrentDictionary<string, Task<string>> Selected = new(StringComparer.Ordinal);

    /// <summary>Encoder names from <c>ffmpeg -encoders</c> (lines like <c> V....D libx264 …</c>), skipping the legend.</summary>
    public static IReadOnlyList<string> ParseEncoders(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        return
        [
            .. output.Split('\n')
                .Select(line => line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Where(parts => parts.Length >= 2 && parts[0].Length == 6 && parts[0][0] is 'V' or 'A' or 'S' && parts[1] != "=")
                .Select(parts => parts[1]),
        ];
    }

    /// <summary>The encoders to try, best first, out of those this ffmpeg was built with.</summary>
    public static IReadOnlyList<string> Candidates(OSPlatform os, IReadOnlyCollection<string> available)
    {
        ArgumentNullException.ThrowIfNull(available);
        string[] hardware = os == OSPlatform.OSX ? ["h264_videotoolbox"]
            : os == OSPlatform.Windows ? ["h264_nvenc", "h264_qsv", "h264_amf", "h264_mf"]
            : ["h264_nvenc"];
        return [.. new[] { Software }.Concat(hardware).Append(Fallback).Where(available.Contains)];
    }

    /// <summary>A bitrate for <paramref name="width"/>×<paramref name="height"/> at <paramref name="frameRate"/>; 12 Mbit/s when unknown.</summary>
    public static long TargetBitrate(int width, int height, double frameRate)
    {
        if (width <= 0 || height <= 0 || !(frameRate > 0))
            return UnknownBitrate;
        return Math.Clamp((long)Math.Round(width * (double)height * frameRate * BitsPerPixel), MinBitrate, MaxBitrate);
    }

    /// <summary>The video half of the codec arguments for <paramref name="encoder"/>.</summary>
    public static IReadOnlyList<string> VideoArguments(string encoder, long bitrate)
    {
        ArgumentException.ThrowIfNullOrEmpty(encoder);
        if (encoder == Software)
            return ["-c:v", Software, "-crf", "20", "-preset", "medium", "-pix_fmt", "yuv420p"];
        return
        [
            "-c:v", encoder,
            "-b:v", bitrate.ToString(CultureInfo.InvariantCulture),
            "-maxrate", (bitrate * 3 / 2).ToString(CultureInfo.InvariantCulture),
            "-bufsize", (bitrate * 2).ToString(CultureInfo.InvariantCulture),
            "-pix_fmt", "yuv420p",
        ];
    }

    /// <summary>Encodes one 256×256 frame to nowhere, which fails fast when the encoder's hardware or library is missing.</summary>
    public static IReadOnlyList<string> ProbeArguments(string encoder) =>
        ["-f", "lavfi", "-i", "color=s=256x256:d=0.1", "-c:v", encoder, "-f", "null", "-"];

    /// <summary>What to tell the user when no H.264 encoder works.</summary>
    public static string NoEncoderMessage(OSPlatform os)
    {
        var install = os == OSPlatform.OSX ? "Install one with \"brew install ffmpeg\"."
            : os == OSPlatform.Windows ? "Install one with \"winget install Gyan.FFmpeg\"."
            : "Install the ffmpeg package from your distribution's package manager.";
        return $"This ffmpeg has no working H.264 encoder (it was built without {Software}, and no hardware encoder or {Fallback} works). {install}";
    }

    /// <summary>The best working encoder for <paramref name="ffmpeg"/>, cached per binary.</summary>
    /// <exception cref="FfmpegException">None works (the message is <see cref="NoEncoderMessage"/>).</exception>
    public static Task<string> SelectAsync(FfmpegLocation ffmpeg, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ffmpeg);
        var task = Selected.GetOrAdd(ffmpeg.FfmpegPath, _ => SelectUncachedAsync(ffmpeg, cancellationToken));
        if (task.IsFaulted || task.IsCanceled)
            Selected.TryRemove(new KeyValuePair<string, Task<string>>(ffmpeg.FfmpegPath, task));
        return task;
    }

    private static async Task<string> SelectUncachedAsync(FfmpegLocation ffmpeg, CancellationToken ct)
    {
        var listing = await RunAsync(ffmpeg, ["-encoders"], ct).ConfigureAwait(false);
        var os = OperatingSystem.IsMacOS() ? OSPlatform.OSX : OperatingSystem.IsWindows() ? OSPlatform.Windows : OSPlatform.Linux;
        foreach (var candidate in Candidates(os, ParseEncoders(listing.Output)))
        {
            var probe = await RunAsync(ffmpeg, ProbeArguments(candidate), ct).ConfigureAwait(false);
            if (probe.ExitCode == 0)
                return candidate;
        }

        throw new FfmpegException(NoEncoderMessage(os), -1, string.Empty);
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(FfmpegLocation ffmpeg, IEnumerable<string> args, CancellationToken ct)
    {
        using var process = FfmpegProcess.Start(ffmpeg.FfmpegPath, args, redirectStandardOutput: true);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            FfmpegProcess.TryKill(process);
            throw;
        }

        await stderr.ConfigureAwait(false);
        return (process.ExitCode, await stdout.ConfigureAwait(false));
    }
}

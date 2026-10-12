using System.Globalization;

namespace MainframeEngine;

internal enum PixelLayout
{
    Bgra,
    Rgba,
}

/// <summary>The recorder's ffmpeg command lines, as argument lists (never a shell string).</summary>
internal static class FfmpegArgs
{
    /// <summary>0.25 bits per pixel per frame: a high-quality intermediate, not a delivery file (≈31 Mbit/s at 1080p60).</summary>
    public static long Bitrate(int width, int height, int fps) => (long)width * height * fps / 4;

    public static IReadOnlyList<string> Encode(string encoder, int width, int height, int fps, PixelLayout layout, string outputMkv)
    {
        var inv = CultureInfo.InvariantCulture;
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "rawvideo", "-pix_fmt", layout == PixelLayout.Bgra ? "bgra" : "rgba",
            "-s", $"{width.ToString(inv)}x{height.ToString(inv)}", "-r", fps.ToString(inv), "-i", "-",
            "-an",
        };
        if (width % 2 != 0 || height % 2 != 0)
            args.AddRange(["-vf", $"crop={(width & ~1).ToString(inv)}:{(height & ~1).ToString(inv)}:0:0"]);
        args.AddRange(["-c:v", encoder]);
        if (encoder == "libx264")
            args.AddRange(["-preset", "ultrafast", "-crf", "14"]);
        else
            args.AddRange(["-b:v", Bitrate(width & ~1, height & ~1, fps).ToString(inv)]);
        args.AddRange(["-g", fps.ToString(inv), "-pix_fmt", "yuv420p", "-progress", "pipe:1", "-nostats", outputMkv]);
        return args;
    }

    public static IReadOnlyList<string> Mux(string videoMkv, string audioWav, string outputMp4) =>
    [
        "-hide_banner", "-loglevel", "error", "-y", "-i", videoMkv, "-i", audioWav,
        "-map", "0:v:0", "-map", "1:a:0", "-c:v", "copy", "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", outputMp4,
    ];

    public static IReadOnlyList<string> Probe(string encoder) =>
        ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=s=256x256:d=0.1", "-c:v", encoder, "-f", "null", "-"];

    public static IReadOnlyList<string> ListEncoders() => ["-hide_banner", "-encoders"];
}

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

    /// <summary>
    /// The RGB → YUV conversion, explicit: ffmpeg's implicit <c>bgra</c> → <c>yuv420p</c> uses BT.601 and leaves the
    /// stream untagged, while players assume BT.709 for HD, so reds and greens would shift. <c>scale</c> converts with
    /// BT.709 into limited range and tags the matrix and range; <c>setparams</c> tags the primaries and transfer, which
    /// <c>scale</c> leaves unspecified and which then override the <c>-color_primaries</c>/<c>-color_trc</c> output
    /// options (verified with ffmpeg 9: without it the MP4 says <c>unknown</c> for both).
    /// </summary>
    private const string Bt709 = "scale=out_color_matrix=bt709:out_range=tv,setparams=color_primaries=bt709:color_trc=bt709";

    /// <summary>
    /// Raw frames on stdin to an H.264 MPEG-TS intermediate. MPEG-TS, not MKV: MKV stores millisecond timestamps, so a
    /// 60 fps recording muxed to 60.03 fps (3600000/59999), while the 90 kHz TS clock holds 1/60 s exactly. Like MKV it
    /// needs no trailer, so a crash still leaves a playable file.
    /// </summary>
    public static IReadOnlyList<string> Encode(string encoder, int width, int height, int fps, PixelLayout layout, string outputTs)
    {
        var inv = CultureInfo.InvariantCulture;
        // yuv420p needs even sizes: an odd width or height loses its last column or row before the conversion.
        var filter = width % 2 != 0 || height % 2 != 0
            ? $"crop={(width & ~1).ToString(inv)}:{(height & ~1).ToString(inv)}:0:0,{Bt709}"
            : Bt709;
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "rawvideo", "-pix_fmt", layout == PixelLayout.Bgra ? "bgra" : "rgba",
            "-s", $"{width.ToString(inv)}x{height.ToString(inv)}", "-r", fps.ToString(inv), "-i", "-",
            "-an", "-vf", filter, "-c:v", encoder,
        };
        if (encoder == "libx264")
            args.AddRange(["-preset", "ultrafast", "-crf", "14"]);
        else
            args.AddRange(["-b:v", Bitrate(width & ~1, height & ~1, fps).ToString(inv)]);
        args.AddRange(["-g", fps.ToString(inv), "-pix_fmt", "yuv420p",
            "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709", "-color_range", "tv",
            "-f", "mpegts", "-progress", "pipe:1", "-nostats", outputTs]);
        return args;
    }

    public static IReadOnlyList<string> Mux(string videoTs, string audioWav, string outputMp4) =>
    [
        "-hide_banner", "-loglevel", "error", "-y", "-i", videoTs, "-i", audioWav,
        "-map", "0:v:0", "-map", "1:a:0", "-c:v", "copy", "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", outputMp4,
    ];

    public static IReadOnlyList<string> Probe(string encoder) =>
        ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=s=256x256:d=0.1", "-c:v", encoder, "-f", "null", "-"];

    public static IReadOnlyList<string> ListEncoders() => ["-hide_banner", "-encoders"];
}

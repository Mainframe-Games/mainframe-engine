namespace MainframeEngine;

/// <summary>
/// Which H.264 encoder to record with: the platform's hardware encoder first (cheap enough for live
/// play), then software. Every candidate is probe-encoded before use (<see cref="FfmpegArgs.Probe"/>),
/// because ffmpeg lists hardware encoders even on machines without the hardware.
/// </summary>
internal static class EncoderSelector
{
    private static readonly string[] Software = ["libx264", "libopenh264"];

    public static IReadOnlyList<string> ParseEncoders(string output) =>
    [
        .. output.Split('\n')
            .Select(line => line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length >= 2 && parts[0].Length == 6 && parts[0][0] is 'V' or 'A' or 'S' && parts[1] != "=")
            .Select(parts => parts[1]),
    ];

    public static IReadOnlyList<string> Candidates(OSPlatformKind os, IReadOnlyCollection<string> available)
    {
        string[] hardware = os switch
        {
            OSPlatformKind.MacOS => ["h264_videotoolbox"],
            OSPlatformKind.Windows => ["h264_nvenc", "h264_qsv", "h264_amf", "h264_mf"],
            _ => ["h264_nvenc"],
        };
        return [.. hardware.Concat(Software).Where(available.Contains)];
    }
}

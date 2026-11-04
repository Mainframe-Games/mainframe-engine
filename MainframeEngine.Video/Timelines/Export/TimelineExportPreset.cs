namespace MainframeEngine.Video.Timelines.Export;

/// <summary>The container and codec an export writes.</summary>
public enum TimelineExportFormat
{
    /// <summary>MP4: H.264 (<see cref="Media.Export.DeliveryEncoder"/>'s choice) and AAC.</summary>
    Mp4,

    /// <summary>WebM: VP9 and Opus.</summary>
    WebM,

    /// <summary>An animated GIF, silent, looping, with a generated palette.</summary>
    Gif,
}

/// <summary>
/// An export target (docs/design/future/video-nle.md, Export). The size is either exact (<see cref="Width"/> and
/// <see cref="Height"/>; another aspect is letterboxed), bounded on the long side (<see cref="LongSide"/>), a width with
/// the timeline's aspect (<see cref="Width"/> alone), or the timeline's own (nothing set).
/// </summary>
/// <param name="Id">Stable key (settings, the export dialog).</param>
/// <param name="Name">What the export dialog shows.</param>
/// <param name="Format">Container and codecs.</param>
/// <param name="Width">Exact width with <paramref name="Height"/>, or alone a width keeping the timeline's aspect.</param>
/// <param name="Height">Exact height (with <paramref name="Width"/>).</param>
/// <param name="LongSide">The longer side, keeping the timeline's aspect (720p for a landscape or a portrait timeline).</param>
/// <param name="FrameRate">Output rate, or null for the timeline's.</param>
/// <param name="Crf">Constant quality for libx264 and VP9.</param>
/// <param name="AudioKbps">Audio bitrate; 0 = no audio.</param>
/// <param name="SizeCapBytes">A file-size cap: the video bitrate is chosen to fit it, or null.</param>
public sealed record TimelineExportPreset(
    string Id,
    string Name,
    TimelineExportFormat Format,
    int? Width = null,
    int? Height = null,
    int? LongSide = null,
    FrameRate? FrameRate = null,
    int Crf = 20,
    int AudioKbps = 192,
    long? SizeCapBytes = null)
{
    /// <summary>The file extension this preset writes.</summary>
    public string Extension => Format switch
    {
        TimelineExportFormat.WebM => ".webm",
        TimelineExportFormat.Gif => ".gif",
        _ => ".mp4",
    };

    /// <summary>Whether the output has sound.</summary>
    public bool HasAudio => AudioKbps > 0 && Format != TimelineExportFormat.Gif;

    /// <summary>The output size for a timeline of these settings: even numbers, at least 2.</summary>
    public (int Width, int Height) SizeFor(TimelineSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (Width is { } w && Height is { } h)
            return (Even(w), Even(h));
        var aspect = (double)settings.Width / settings.Height;
        if (LongSide is { } side)
            return aspect >= 1 ? (Even(side), Even(side / aspect)) : (Even(side * aspect), Even(side));
        if (Width is { } width)
            return (Even(width), Even(width / aspect));
        return (settings.Width, settings.Height);
    }

    /// <summary>The output rate for a timeline of these settings.</summary>
    public FrameRate RateFor(TimelineSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return FrameRate ?? settings.FrameRate;
    }

    private static int Even(double pixels) => Math.Max(2, (int)Math.Round(pixels / 2, MidpointRounding.AwayFromZero) * 2);
}

/// <summary>The built-in export presets: socials, YouTube and Steam, chat apps, loops.</summary>
public static class TimelineExportPresets
{
    /// <summary>25 MB: Discord's free upload limit, and a size X takes without trouble.</summary>
    public const long ChatSizeCap = 25L * 1024 * 1024;

    public static TimelineExportPreset Vertical30 { get; } = new("vertical-30", "TikTok / Reels / Shorts (1080×1920, 30 fps)", TimelineExportFormat.Mp4, 1080, 1920, FrameRate: new FrameRate(30, 1));

    public static TimelineExportPreset Vertical60 { get; } = new("vertical-60", "TikTok / Reels / Shorts (1080×1920, 60 fps)", TimelineExportFormat.Mp4, 1080, 1920, FrameRate: new FrameRate(60, 1));

    public static TimelineExportPreset YouTube1080 { get; } = new("youtube-1080", "YouTube / Steam 1080p", TimelineExportFormat.Mp4, 1920, 1080, Crf: 18);

    public static TimelineExportPreset YouTube1080WebM { get; } = new("youtube-1080-webm", "YouTube / Steam 1080p (WebM)", TimelineExportFormat.WebM, 1920, 1080, Crf: 24);

    public static TimelineExportPreset YouTube4K { get; } = new("youtube-4k", "YouTube 4K", TimelineExportFormat.Mp4, 3840, 2160, Crf: 18);

    public static TimelineExportPreset Chat { get; } = new("chat", "X / Discord (720p, 25 MB)", TimelineExportFormat.Mp4, LongSide: 1280, FrameRate: new FrameRate(30, 1), AudioKbps: 128, SizeCapBytes: ChatSizeCap);

    /// <summary>GIF frame delays are hundredths of a second, so its rate divides 100: 20 fps (5 cs), not 15 (6.67 cs).</summary>
    public static TimelineExportPreset GifLoop { get; } = new("gif", "GIF loop (480 px)", TimelineExportFormat.Gif, Width: 480, FrameRate: new FrameRate(20, 1), AudioKbps: 0);

    public static TimelineExportPreset WebMLoop { get; } = new("webm-loop", "WebM loop (720 px, silent)", TimelineExportFormat.WebM, Width: 720, FrameRate: new FrameRate(30, 1), Crf: 30, AudioKbps: 0);

    public static TimelineExportPreset Timeline { get; } = new("timeline", "Timeline settings (H.264)", TimelineExportFormat.Mp4);

    /// <summary>Every preset, in the order the export dialog lists them.</summary>
    public static IReadOnlyList<TimelineExportPreset> All { get; } =
        [Vertical30, Vertical60, YouTube1080, YouTube1080WebM, YouTube4K, Chat, GifLoop, WebMLoop, Timeline];

    /// <summary>The preset with this id, or null.</summary>
    public static TimelineExportPreset? Find(string id) => All.FirstOrDefault(p => p.Id == id);

    /// <summary>
    /// The video bitrate (bits/s) that fits <paramref name="capBytes"/> for <paramref name="durationSeconds"/> next to
    /// <paramref name="audioKbps"/> of audio; at least 100 kbit/s.
    /// </summary>
    public static long SizeCappedVideoBitrate(long capBytes, double durationSeconds, int audioKbps)
    {
        if (!(durationSeconds > 0))
            throw new ArgumentOutOfRangeException(nameof(durationSeconds), durationSeconds, "The duration is positive.");
        var total = capBytes * 8.0 / durationSeconds;
        return Math.Max(100_000, (long)Math.Floor(total - audioKbps * 1000.0));
    }
}

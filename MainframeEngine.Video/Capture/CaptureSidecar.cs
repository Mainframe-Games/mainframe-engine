namespace MainframeEngine.Video.Capture;

/// <summary>A span of the recording's timeline, in seconds. <see cref="Label"/> is set for Keep and Skip spans.</summary>
public sealed record CaptureSpan(double Start, double End, string? Label = null);

/// <summary>A point the game marked as interesting, in seconds.</summary>
public sealed record CaptureMarker(double Time, string Label);

/// <summary><see cref="Live"/>: a person played in real time. <see cref="Offline"/>: a fixed-timestep, scripted render with no player input.</summary>
public enum CaptureMode
{
    Live,
    Offline,
}

/// <summary>
/// What Mainframe Engine knew about a recording, read from the <c>.mfcap.json</c> file written next
/// to the video. The engine owns the format (its <c>docs/design/recording.md</c>); this is the
/// reader's view of version 1. All times are seconds on the video's own timeline.
/// </summary>
public sealed record CaptureSidecar(
    CaptureMode Mode,
    double DurationSeconds,
    double FrameRate,
    int Width,
    int Height,
    string? Game,
    string? Scene,
    IReadOnlyList<CaptureSpan> Input,
    IReadOnlyList<CaptureSpan> Loading,
    IReadOnlyList<CaptureSpan> Paused,
    IReadOnlyList<CaptureSpan> Keep,
    IReadOnlyList<CaptureSpan> Skip,
    IReadOnlyList<CaptureMarker> Markers)
{
    public const string FormatName = "mainframe-capture";

    /// <summary>The newest sidecar version this build understands.</summary>
    public const int SupportedVersion = 1;

    public const string FileSuffix = ".mfcap.json";

    /// <summary>The sidecar path for a video: the full file name plus <see cref="FileSuffix"/>.</summary>
    public static string PathFor(string videoPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoPath);
        return videoPath + FileSuffix;
    }
}

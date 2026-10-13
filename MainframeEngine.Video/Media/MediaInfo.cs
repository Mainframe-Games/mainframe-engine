namespace MainframeEngine.Video.Media;

/// <summary>What ffprobe reports about a source file. Enough to build a project and size the timeline.</summary>
/// <param name="DurationSeconds">Container duration in seconds.</param>
/// <param name="Width">Frame width after rotation metadata is applied.</param>
/// <param name="Height">Frame height after rotation metadata is applied.</param>
/// <param name="FrameRate">Frames per second of the primary video stream.</param>
/// <param name="HasAudio">False if there is no audio stream. Waveform, silence detection and export all need one.</param>
/// <param name="VideoCodec">e.g. "h264". Informational.</param>
/// <param name="AudioCodec">e.g. "aac". Null when <paramref name="HasAudio"/> is false.</param>
public sealed record MediaInfo(
    double DurationSeconds,
    int Width,
    int Height,
    double FrameRate,
    bool HasAudio,
    string? VideoCodec,
    string? AudioCodec);

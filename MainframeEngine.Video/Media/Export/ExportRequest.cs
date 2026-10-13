using MainframeEngine.Video.Models;

namespace MainframeEngine.Video.Media.Export;

/// <param name="SourcePath">The recording. Read-only.</param>
/// <param name="Segments">The project's full partition; disabled segments are what gets removed.</param>
/// <param name="DurationSeconds">Source duration, for keyframe snapping at the end of file.</param>
/// <param name="OutputPath">Target file. The extension chooses the container: .mp4, .mov, .mkv, .webm.</param>
/// <param name="Mode">Precise or fast.</param>
/// <param name="Width">Source width in pixels, for the bitrate of encoders without constant quality; 0 means unknown.</param>
/// <param name="Height">Source height in pixels, for the bitrate of encoders without constant quality; 0 means unknown.</param>
/// <param name="FrameRate">Source frames per second, for the bitrate of encoders without constant quality; 0 means unknown.</param>
public sealed record ExportRequest(
    string SourcePath,
    IReadOnlyList<Segment> Segments,
    double DurationSeconds,
    string OutputPath,
    ExportMode Mode,
    int Width = 0,
    int Height = 0,
    double FrameRate = 0);

/// <param name="Fraction">0..1 overall.</param>
/// <param name="Stage">Short human-readable description of the current step.</param>
public readonly record struct ExportProgress(double Fraction, string Stage);

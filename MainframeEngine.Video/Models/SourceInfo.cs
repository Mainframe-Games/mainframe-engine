namespace MainframeEngine.Video.Models;

/// <summary>
/// Identity and basic properties of the source recording. The source is only ever opened read-only.
/// </summary>
/// <param name="Path">Absolute path to the source video.</param>
/// <param name="Sha256">Hex SHA-256 of the first 8 MB plus the file size. Used to warn, not to block, when the source has changed or moved.</param>
/// <param name="DurationSeconds">Total duration in seconds. Also the right edge of the segment partition.</param>
/// <param name="Width">Frame width in pixels.</param>
/// <param name="Height">Frame height in pixels.</param>
/// <param name="FrameRate">Frames per second.</param>
public sealed record SourceInfo(
    string Path,
    string Sha256,
    double DurationSeconds,
    int Width,
    int Height,
    double FrameRate);

namespace MainframeEngine.Video.Media;

/// <summary>Where the ffmpeg and ffprobe binaries were found.</summary>
/// <param name="FfmpegPath">Full path to the ffmpeg executable.</param>
/// <param name="FfprobePath">Full path to the ffprobe executable, always in the same directory.</param>
/// <param name="Directory">The directory containing both binaries, using the target platform's separators.</param>
/// <param name="Source">Which step of the resolution order produced this location.</param>
public sealed record FfmpegLocation(string FfmpegPath, string FfprobePath, string Directory, FfmpegLocationSource Source);

public enum FfmpegLocationSource
{
    /// <summary>The path saved in app settings.</summary>
    UserSetting,

    /// <summary>Found by walking the <c>PATH</c> environment variable.</summary>
    Path,

    /// <summary>Found in one of the per-platform default install directories.</summary>
    CommonLocation,
}

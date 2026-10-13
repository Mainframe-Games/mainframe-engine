using MainframeEngine.Video.Media;

namespace MainframeEngine.Editor.Video;

/// <summary>
/// Which ffmpeg video mode uses: Editor Settings › Video › ffmpeg when it is set, else <see cref="EnvironmentVariable"/>
/// (the variable Play and Record already honours), else <see cref="FfmpegLocator"/>'s PATH and install-folder search.
/// </summary>
public static class VideoFfmpeg
{
    /// <summary>The same variable the runtime's recorder reads (<c>FfmpegResolver</c>), so one setting serves both.</summary>
    public const string EnvironmentVariable = "MAINFRAME_FFMPEG";

    /// <summary>The path to hand the locator: a non-blank setting, else a non-blank environment value, else null (search).</summary>
    public static string? ConfiguredPath(string? setting, string? environment) =>
        !string.IsNullOrWhiteSpace(setting) ? setting.Trim()
        : !string.IsNullOrWhiteSpace(environment) ? environment.Trim()
        : null;

    /// <summary>Finds ffmpeg and ffprobe for <paramref name="setting"/>.</summary>
    /// <exception cref="FfmpegMissingException">Neither binary pair was found; the message names the install command.</exception>
    public static FfmpegLocation Locate(string? setting) =>
        new FfmpegLocator(ConfiguredPath(setting, Environment.GetEnvironmentVariable(EnvironmentVariable))).Locate();
}

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

    /// <summary>
    /// The ffmpeg to hand a game as <c>--ffmpeg</c>: the setting itself when it names a file or does not exist (the game then
    /// says it was not found), and the <c>ffmpeg</c> inside when it names a folder or the <c>ffprobe</c> beside it. The
    /// setting may be either, but the game's <c>--ffmpeg</c> must be the encoder. Null when the setting is blank.
    /// </summary>
    public static string? GameArgument(string? setting)
    {
        if (string.IsNullOrWhiteSpace(setting))
            return null;
        var path = setting.Trim();
        var folder = Directory.Exists(path) ? path
            : Path.GetFileNameWithoutExtension(path).Equals("ffprobe", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(path)
            : null;
        if (folder is null)
            return path;
        var ffmpeg = Path.Combine(folder, new FfmpegLocator().ExecutableName("ffmpeg"));
        return File.Exists(ffmpeg) ? ffmpeg : path;
    }

    /// <summary>
    /// The notice line's text when <paramref name="what"/> needs ffmpeg and the tab has none. The panel adds an
    /// "Editor Settings" link after it (<see cref="VideoTab.NoticeNeedsFfmpegSetting"/>).
    /// </summary>
    public static string NoticeText(string what) => $"ffmpeg not found: {what} needs it.";

    /// <summary>Finds ffmpeg and ffprobe for <paramref name="setting"/>.</summary>
    /// <exception cref="FfmpegMissingException">Neither binary pair was found; the message names the install command.</exception>
    public static FfmpegLocation Locate(string? setting) =>
        new FfmpegLocator(ConfiguredPath(setting, Environment.GetEnvironmentVariable(EnvironmentVariable))).Locate();
}

namespace MainframeEngine.Video.Media;

/// <summary>
/// ffmpeg or ffprobe could not be located. The message is written for the end user and names the
/// install command for the current platform.
/// </summary>
public sealed class FfmpegMissingException : Exception
{
    public FfmpegMissingException(string message)
        : base(message)
    {
    }
}

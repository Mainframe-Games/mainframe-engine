namespace MainframeEngine.Video.Media;

/// <summary>An ffmpeg invocation exited with a non-zero code. <see cref="StandardError"/> holds its diagnostics.</summary>
public sealed class FfmpegException : Exception
{
    public FfmpegException(string message, int exitCode, string standardError)
        : base(message)
    {
        ExitCode = exitCode;
        StandardError = standardError;
    }

    public int ExitCode { get; }

    public string StandardError { get; }
}

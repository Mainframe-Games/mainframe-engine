namespace MainframeEngine.Video.Analysis;

/// <summary>The speech model could not be downloaded. The message is user-facing.</summary>
public sealed class ModelDownloadException : Exception
{
    public ModelDownloadException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

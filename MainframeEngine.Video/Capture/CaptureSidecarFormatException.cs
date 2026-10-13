namespace MainframeEngine.Video.Capture;

/// <summary>A <c>.mfcap.json</c> file that is not a readable version-1 Mainframe capture sidecar.</summary>
public sealed class CaptureSidecarFormatException : Exception
{
    public CaptureSidecarFormatException(string message)
        : base(message)
    {
    }

    public CaptureSidecarFormatException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

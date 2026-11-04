namespace MainframeEngine.Video.Timelines;

/// <summary>A <c>.mftimeline</c> file that cannot be read: not JSON, a newer version, or contents that do not fit together.</summary>
public sealed class TimelineFormatException : Exception
{
    public TimelineFormatException()
    {
    }

    public TimelineFormatException(string message)
        : base(message)
    {
    }

    public TimelineFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

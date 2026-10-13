namespace MainframeEngine.Video.Projects;

/// <summary>
/// Thrown when a project file cannot be understood: malformed JSON, a missing or
/// unsupported version, or a migration that cannot be performed.
/// </summary>
public sealed class ProjectFormatException : Exception
{
    public ProjectFormatException(string message)
        : base(message)
    {
    }

    public ProjectFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

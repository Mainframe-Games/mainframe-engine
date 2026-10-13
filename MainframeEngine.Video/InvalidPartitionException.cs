namespace MainframeEngine.Video;

/// <summary>
/// Thrown when a set of segments does not form a complete, sorted, non-overlapping partition of
/// <c>[0, duration]</c>. See the "partition invariant" section of CLAUDE.md.
/// </summary>
public sealed class InvalidPartitionException : Exception
{
    public InvalidPartitionException(string message)
        : base(message)
    {
    }
}

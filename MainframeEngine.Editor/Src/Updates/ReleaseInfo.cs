namespace MainframeEngine.Editor;

/// <summary>A file of a GitHub release: name, download URL, size and SHA-256 (lower-case hex; null when GitHub gave none).</summary>
public sealed record ReleaseAsset(string Name, Uri DownloadUrl, long Size, string? Sha256);

/// <summary>A published editor release (GitHub <c>releases/latest</c>).</summary>
public sealed record ReleaseInfo(ReleaseVersion Version, string Tag, string Notes, Uri PageUrl, IReadOnlyList<ReleaseAsset> Assets)
{
    public ReleaseAsset? FindAsset(string name) => Assets.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.Ordinal));
}

/// <summary>An update step failed; the message is a sentence the editor shows as is.</summary>
public sealed class UpdateException : Exception
{
    public UpdateException()
    {
    }

    public UpdateException(string message)
        : base(message)
    {
    }

    public UpdateException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

namespace MainframeEngine.Editor;

public enum UpdateCheckStatus
{
    UpdateAvailable,
    UpToDate,
    DevelopmentBuild,
    UnsupportedPlatform,
    Failed,
}

/// <summary>The outcome of an update check; <see cref="Release"/> and <see cref="Asset"/> are set for an update.</summary>
public sealed record UpdateCheckResult(UpdateCheckStatus Status, ReleaseInfo? Release = null, ReleaseAsset? Asset = null, string? Error = null)
{
    public bool IsUpdate => Status == UpdateCheckStatus.UpdateAvailable && Release is not null && Asset is not null;

    /// <summary>The sentence Help › Check for Updates… shows.</summary>
    public string Describe(string currentVersion) => Status switch
    {
        UpdateCheckStatus.UpdateAvailable => $"Mainframe Engine v{Release?.Version} is available (you have v{currentVersion}).",
        UpdateCheckStatus.UpToDate => $"Mainframe Engine v{currentVersion} is the latest version.",
        UpdateCheckStatus.DevelopmentBuild => $"This is a development build (v{currentVersion}); updates are offered to released builds only.",
        UpdateCheckStatus.UnsupportedPlatform => "The latest release has no editor build for this platform.",
        _ => Error ?? "The update check failed.",
    };
}

/// <summary>
/// Decides whether the latest release is an update for this editor: <paramref name="currentVersion"/> must be a release
/// version (development builds never check), <paramref name="rid"/> a released platform, and the release strictly newer
/// with a checksummed asset for that platform. Never throws, except when the caller cancels.
/// </summary>
public sealed class UpdateChecker(ReleaseFeed feed, string currentVersion, string? rid)
{
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        if (!ReleaseVersion.TryParse(currentVersion, out var current))
            return new UpdateCheckResult(UpdateCheckStatus.DevelopmentBuild);
        if (rid is null)
            return new UpdateCheckResult(UpdateCheckStatus.UnsupportedPlatform);

        ReleaseInfo? release;
        try
        {
            release = await feed.GetLatestAsync(ct).ConfigureAwait(false);
        }
        catch (UpdateException e)
        {
            return new UpdateCheckResult(UpdateCheckStatus.Failed, Error: e.Message);
        }

        if (release is null || release.Version <= current)
            return new UpdateCheckResult(UpdateCheckStatus.UpToDate, release);
        var asset = release.FindAsset(UpdatePlatform.AssetName(release.Version, rid));
        if (asset is null)
            return new UpdateCheckResult(UpdateCheckStatus.UnsupportedPlatform, release);
        if (asset.Sha256 is null)
            return new UpdateCheckResult(UpdateCheckStatus.Failed, release, asset,
                $"The release has no checksum for {asset.Name}, so it cannot be verified.");
        return new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, release, asset);
    }
}

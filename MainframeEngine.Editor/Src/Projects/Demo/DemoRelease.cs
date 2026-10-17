namespace MainframeEngine.Editor;

/// <summary>Where the example project zips (<see cref="ExampleProject"/>) for an editor version live on GitHub Releases.</summary>
public static class DemoRelease
{
    public const string ReleasesBaseUrl = "https://github.com/Mainframe-Games/mainframe-engine/releases";
    public const string DevelopmentVersion = "0.0.0-dev";

    /// <summary>
    /// Development and prerelease versions (<c>0.0.0-dev</c>, <c>1.5.0-rc.1</c>) have no tagged release of their own: they
    /// download from the latest release (<see cref="ReleaseFeed"/>).
    /// </summary>
    public static bool IsDevelopment(string editorVersion) =>
        string.IsNullOrWhiteSpace(editorVersion) || WithoutBuildMetadata(editorVersion).Contains('-');

    /// <summary>The release asset name of <paramref name="example"/> (default: the Demo) for <paramref name="tag"/> (<c>vX.Y.Z</c>).</summary>
    public static string AssetName(string tag, ExampleProject? example = null) => $"{(example ?? ExampleProject.Demo).AssetName}-{tag}.zip";

    /// <summary>
    /// The zip of <paramref name="example"/> (default: the Demo) in the release of <paramref name="editorVersion"/>, a released
    /// version (not <see cref="IsDevelopment"/>); SemVer build metadata (<c>1.4.2+abc123</c>) is not part of the tag.
    /// </summary>
    public static Uri AssetUrl(string editorVersion, ExampleProject? example = null)
    {
        if (IsDevelopment(editorVersion))
            throw new ArgumentException($"'{editorVersion}' has no release of its own; use the latest release's assets.", nameof(editorVersion));
        var tag = "v" + WithoutBuildMetadata(editorVersion).Trim();
        return new Uri($"{ReleasesBaseUrl}/download/{tag}/{AssetName(tag, example)}");
    }

    private static string WithoutBuildMetadata(string version)
    {
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? version : version[..plus];
    }
}

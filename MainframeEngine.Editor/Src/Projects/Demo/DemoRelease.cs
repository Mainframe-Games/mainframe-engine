namespace MainframeEngine.Editor;

/// <summary>Where the example project zips (<see cref="ExampleProject"/>) for an editor version live on GitHub Releases.</summary>
public static class DemoRelease
{
    public const string ReleasesBaseUrl = "https://github.com/Mainframe-Games/mainframe-engine/releases";
    public const string DevelopmentVersion = "0.0.0-dev";

    /// <summary>Development and prerelease versions (<c>0.0.0-dev</c>, <c>1.5.0-rc.1</c>) have no tagged Demo asset: they use the latest release.</summary>
    public static bool IsDevelopment(string editorVersion) =>
        string.IsNullOrWhiteSpace(editorVersion) || WithoutBuildMetadata(editorVersion).Contains('-');

    /// <summary>
    /// The zip of <paramref name="example"/> (default: the Demo) for <paramref name="editorVersion"/>; SemVer build metadata
    /// (<c>1.4.2+abc123</c>) is not part of the tag.
    /// </summary>
    public static Uri AssetUrl(string editorVersion, ExampleProject? example = null)
    {
        var asset = (example ?? ExampleProject.Demo).AssetName;
        if (IsDevelopment(editorVersion))
            return new Uri($"{ReleasesBaseUrl}/latest/download/{asset}.zip");
        var version = WithoutBuildMetadata(editorVersion).Trim();
        return new Uri($"{ReleasesBaseUrl}/download/v{version}/{asset}-v{version}.zip");
    }

    private static string WithoutBuildMetadata(string version)
    {
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? version : version[..plus];
    }
}

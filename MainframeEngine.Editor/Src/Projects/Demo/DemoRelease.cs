namespace MainframeEngine.Editor;

/// <summary>Where the Demo zip for an editor version lives on GitHub Releases.</summary>
public static class DemoRelease
{
    public const string ReleasesBaseUrl = "https://github.com/Mainframe-Games/mainframe-engine/releases";
    public const string DevelopmentVersion = "0.0.0-dev";

    public static bool IsDevelopment(string editorVersion) =>
        string.IsNullOrWhiteSpace(editorVersion) || editorVersion == DevelopmentVersion || editorVersion.Contains('-');

    public static Uri AssetUrl(string editorVersion) => IsDevelopment(editorVersion)
        ? new Uri($"{ReleasesBaseUrl}/latest/download/MainframeEngine.Demo.zip")
        : new Uri($"{ReleasesBaseUrl}/download/v{editorVersion}/MainframeEngine.Demo-v{editorVersion}.zip");
}

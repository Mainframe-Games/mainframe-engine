using System.Reflection;

namespace MainframeEngine.Editor;

/// <summary>Brand assets and version text of the editor (logo "C3 Circuit", docs/images/brand).</summary>
public static class EditorBrand
{
    /// <summary>
    /// The window icon for this platform (engine content <c>Content/Brand/</c>): macOS shows it in the Dock, so it uses the
    /// padded Apple icon-grid variant at Dock resolution; Windows and Linux show it in title bars and task bars (small,
    /// full-bleed: the simplified small logo reads better there).
    /// </summary>
    public static string WindowIconPath => OperatingSystem.IsMacOS() ? "Content/Brand/logo-macos-512.png" : "Content/Brand/logo-48.png";

    /// <summary>The splash logo (RmlUi renders PNGs, not SVG).</summary>
    public const string SplashLogoPath = "/Content/Brand/logo-512.png";

    /// <summary>
    /// "v1.2.3": the editor ships in lock step with the engine core (one release tag, one <c>-p:Version</c> for the whole
    /// build graph — docs/design/release.md), so this is <see cref="EngineInfo.Version"/>. "v0.0.0-dev" for local builds.
    /// </summary>
    public static string Version => "v" + EngineInfo.Version;

    /// <summary>"Mainframe Editor v1.2.3": the window title's suffix and the About box heading.</summary>
    public static string NameWithVersion => "Mainframe Editor " + Version;

    /// <summary>The engine and editor's source repository (the About box links to it).</summary>
    public static readonly Uri RepositoryUrl = new("https://github.com/Mainframe-Games/mainframe-engine");

    /// <summary>The editor assembly's own version (build metadata stripped); equals <see cref="EngineInfo.Version"/> in any consistent build.</summary>
    public static string AssemblyVersion
    {
        get
        {
            var assembly = typeof(EditorBrand).Assembly;
            var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                                ?? assembly.GetName().Version?.ToString(3) ?? "0.0.0";
            var plus = informational.IndexOf('+', StringComparison.Ordinal);
            return plus >= 0 ? informational[..plus] : informational;
        }
    }

    /// <summary>
    /// True when the editor and the engine core it loaded come from the same build. A mismatch means a broken install
    /// (e.g. a hand-copied MainframeEngine.dll); the editor logs an error at startup.
    /// </summary>
    public static bool VersionsMatch => string.Equals(AssemblyVersion, EngineInfo.Version, StringComparison.Ordinal);
}

using System.Reflection;

namespace MainframeEngine.Editor;

/// <summary>Brand assets and version text of the editor (logo "C3 Circuit", docs/images/brand).</summary>
public static class EditorBrand
{
    /// <summary>
    /// The window icon for this platform (engine content <c>Content/Brand/</c>): macOS shows it in the Dock (large),
    /// Windows and Linux in title bars and task bars (small; the simplified small logo reads better there).
    /// </summary>
    public static string WindowIconPath => OperatingSystem.IsMacOS() ? "Content/Brand/logo-256.png" : "Content/Brand/logo-48.png";

    /// <summary>The splash logo (RmlUi renders PNGs, not SVG).</summary>
    public const string SplashLogoPath = "/Content/Brand/logo-512.png";

    /// <summary>"v1.2.3" from the informational version (build metadata stripped), "v0.0.0-dev" for local builds.</summary>
    public static string Version
    {
        get
        {
            var assembly = typeof(EditorBrand).Assembly;
            var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                                ?? assembly.GetName().Version?.ToString() ?? "0.0.0";
            var plus = informational.IndexOf('+', StringComparison.Ordinal);
            return "v" + (plus >= 0 ? informational[..plus] : informational);
        }
    }
}

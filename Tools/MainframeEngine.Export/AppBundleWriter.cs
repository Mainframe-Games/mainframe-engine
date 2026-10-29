using System.Security;
using System.Text;

namespace MainframeEngine.Export;

/// <summary>The identity of a macOS bundle (Info.plist values).</summary>
/// <param name="Name">The app name (<c>CFBundleName</c>, the <c>.app</c> folder).</param>
/// <param name="Executable">The apphost's name in <c>Contents/MacOS</c>.</param>
/// <param name="BundleId">The <c>CFBundleIdentifier</c>.</param>
/// <param name="Version">The short version and the build version.</param>
/// <param name="Copyright">The <c>NSHumanReadableCopyright</c>.</param>
/// <param name="HasIcon">An <c>icon.icns</c> is in <c>Contents/Resources</c>.</param>
public sealed record AppBundleInfo(string Name, string Executable, string BundleId, string Version, string Copyright, bool HasIcon);

/// <summary>
/// Lays out a game's <c>.app</c> (docs/design/release.md#macos-bundle-layout): the apphost alone in <c>Contents/MacOS</c>
/// (only Mach-O code may be there for <c>codesign --verify --strict</c>), the rest of the publish in
/// <c>Contents/MonoBundle</c> (the app's base directory: assemblies, the runtime's and the engine's dylibs, content), the
/// icon in <c>Contents/Resources</c>, and an XML Info.plist with the keys the old <c>package-game.sh</c> wrote.
/// </summary>
public static class AppBundleWriter
{
    /// <summary>The folder the published apphost starts its app from (<c>MacBundleAppDirectory</c>, build/macos/BundleAppHost.targets).</summary>
    public const string AppDirectory = "MonoBundle";

    /// <summary>The Info.plist text.</summary>
    public static string InfoPlist(AppBundleInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        var b = new StringBuilder();
        b.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        b.Append("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n");
        b.Append("<plist version=\"1.0\">\n<dict>\n");
        void Key(string key, string value) =>
            b.Append("\t<key>").Append(key).Append("</key><string>").Append(SecurityElement.Escape(value)).Append("</string>\n");
        Key("CFBundleName", info.Name);
        Key("CFBundleDisplayName", info.Name);
        Key("CFBundleIdentifier", info.BundleId);
        Key("CFBundleExecutable", info.Executable);
        if (info.HasIcon)
            Key("CFBundleIconFile", "icon.icns");
        Key("CFBundlePackageType", "APPL");
        Key("CFBundleShortVersionString", info.Version);
        Key("CFBundleVersion", info.Version);
        Key("NSHumanReadableCopyright", info.Copyright);
        Key("LSMinimumSystemVersion", "11.0");
        b.Append("\t<key>NSHighResolutionCapable</key><true/>\n");
        Key("LSApplicationCategoryType", "public.app-category.games");
        b.Append("</dict>\n</plist>\n");
        return b.ToString();
    }

    /// <summary>
    /// Whether the apphost <paramref name="apphost"/> starts <c>../MonoBundle/&lt;assembly&gt;.dll</c>: the desktop project
    /// imports BundleAppHost.targets (MainframeGame.props, or the package's buildTransitive) and the publish set
    /// <c>MacBundleAppDirectory</c>.
    /// </summary>
    public static bool StartsFromBundle(string apphost, string assemblyName)
    {
        var needle = Encoding.UTF8.GetBytes($"../{AppDirectory}/{assemblyName}.dll");
        return File.ReadAllBytes(apphost).AsSpan().IndexOf(needle) >= 0;
    }

    /// <summary>
    /// Moves <paramref name="publishDirectory"/> into <paramref name="appPath"/> (a new <c>&lt;Name&gt;.app</c>), with the
    /// executable <paramref name="info"/> names (already renamed in the publish) alone in <c>Contents/MacOS</c>.
    /// <paramref name="icns"/> (optional) goes to <c>Contents/Resources/icon.icns</c>.
    /// </summary>
    public static void Write(string publishDirectory, string appPath, AppBundleInfo info, string? icns)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publishDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(appPath);
        ArgumentNullException.ThrowIfNull(info);
        var contents = Path.Combine(appPath, "Contents");
        var macOs = Path.Combine(contents, "MacOS");
        var resources = Path.Combine(contents, "Resources");
        var monoBundle = Path.Combine(contents, AppDirectory);
        if (Directory.Exists(appPath))
            Directory.Delete(appPath, recursive: true);
        Directory.CreateDirectory(contents);
        Directory.Move(publishDirectory, monoBundle);
        Directory.CreateDirectory(macOs);
        Directory.CreateDirectory(resources);
        File.Move(Path.Combine(monoBundle, info.Executable), Path.Combine(macOs, info.Executable));
        if (icns is not null)
            File.Copy(icns, Path.Combine(resources, "icon.icns"), overwrite: true);
        File.WriteAllText(Path.Combine(contents, "Info.plist"), InfoPlist(info), new UTF8Encoding(false));
    }
}

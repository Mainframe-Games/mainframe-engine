using System.Runtime.InteropServices;

namespace MainframeEngine.Editor;

/// <summary>
/// The platforms the release workflow builds (osx-arm64, win-x64, linux-x64) and the layout of their archives
/// (build/package-editor.sh): asset names, the install root inside an extracted archive, the editor executable.
/// </summary>
public static class UpdatePlatform
{
    public const string ExecutableName = "MainframeEngine.Editor";

    /// <summary>The macOS bundle name (docs/design/release.md, macOS app name).</summary>
    public const string MacAppName = "Mainframe Engine.app";

    /// <summary>This process's release RID, or null when no release is built for it.</summary>
    public static string? CurrentRid => Rid(
        OperatingSystem.IsMacOS() ? OSPlatform.OSX : OperatingSystem.IsWindows() ? OSPlatform.Windows : OperatingSystem.IsLinux() ? OSPlatform.Linux : OSPlatform.FreeBSD,
        RuntimeInformation.ProcessArchitecture);

    public static string? Rid(OSPlatform os, Architecture architecture) =>
        os == OSPlatform.OSX && architecture == Architecture.Arm64 ? "osx-arm64"
        : os == OSPlatform.Windows && architecture == Architecture.X64 ? "win-x64"
        : os == OSPlatform.Linux && architecture == Architecture.X64 ? "linux-x64"
        : null;

    public static bool IsMac(string rid) => rid.StartsWith("osx-", StringComparison.Ordinal);

    public static bool IsWindows(string rid) => rid.StartsWith("win-", StringComparison.Ordinal);

    public static string AssetName(ReleaseVersion version, string rid) =>
        $"MainframeEngine-{version}-{rid}{(IsWindows(rid) ? ".zip" : ".tar.gz")}";

    /// <summary>The install root inside an extracted archive: the bundle on macOS, the top folder elsewhere.</summary>
    public static string StagedRoot(string extracted, ReleaseVersion version, string rid) =>
        IsMac(rid) ? Path.Combine(extracted, MacAppName) : Path.Combine(extracted, $"MainframeEngine-{version}-{rid}");

    public static string ExecutablePath(string root, string rid) =>
        IsMac(rid) ? Path.Combine(root, "Contents", "MacOS", ExecutableName)
        : Path.Combine(root, IsWindows(rid) ? ExecutableName + ".exe" : ExecutableName);
}

/// <summary>Where updates are staged and recorded (<c>~/.mainframe/updates</c>).</summary>
public static class UpdatePaths
{
    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mainframe", "updates");

    /// <summary>The applier's outcome, read (and deleted) by the next editor start.</summary>
    public static string ResultFile(string updatesDirectory) => Path.Combine(updatesDirectory, "result.json");

    public static string LogFile(string updatesDirectory) => Path.Combine(updatesDirectory, "update.log");

    /// <summary>Where the old install waits while the new one is copied in.</summary>
    public static string BackupOf(string root) => Path.TrimEndingDirectorySeparator(root) + ".old";
}

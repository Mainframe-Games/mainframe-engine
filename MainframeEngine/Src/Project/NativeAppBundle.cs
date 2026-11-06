using System.Runtime.CompilerServices;

namespace MainframeEngine;

/// <summary>
/// The base directory of a NativeAOT game inside a macOS release bundle (ADR 0259). A JIT bundle starts its app through
/// an apphost in <c>Contents/MacOS</c> that loads <c>../MonoBundle/&lt;app&gt;.dll</c>, so <see cref="AppContext.BaseDirectory"/>
/// is <c>Contents/MonoBundle</c>, where the natives, <c>Assets.mfpak</c> and loose content are. A NativeAOT executable is
/// the app itself, alone in <c>Contents/MacOS</c> (only Mach-O code may be there), so its base directory would be that
/// folder: <see cref="Apply"/> points it at <c>../MonoBundle</c>, the same layout as a JIT bundle.
/// </summary>
public static class NativeAppBundle
{
    /// <summary>The bundle folder that holds everything but the executable (<c>MacBundleAppDirectory</c>).</summary>
    public const string AppDirectory = "MonoBundle";

    private const string BaseDirectoryKey = "APP_CONTEXT_BASE_DIRECTORY";

    /// <summary>
    /// In a NativeAOT process on macOS whose executable is <c>X.app/Contents/MacOS/&lt;exe&gt;</c> beside a
    /// <c>Contents/MonoBundle</c> folder, makes that folder <see cref="AppContext.BaseDirectory"/> and returns it; otherwise
    /// changes nothing and returns null. Call before anything reads the base directory (<see cref="GameHost.Run"/> does).
    /// </summary>
    public static string? Apply()
    {
        if (RuntimeFeature.IsDynamicCodeSupported || !OperatingSystem.IsMacOS() || Environment.ProcessPath is not { } process)
            return null;
        if (BaseDirectoryFor(Path.GetDirectoryName(process)) is not { } baseDirectory)
            return null;
        AppContext.SetData(BaseDirectoryKey, baseDirectory);
        return baseDirectory;
    }

    /// <summary>
    /// <c>…/X.app/Contents/MonoBundle/</c> (with a trailing separator, as <see cref="AppContext.BaseDirectory"/>) when
    /// <paramref name="executableDirectory"/> is <c>…/X.app/Contents/MacOS</c> and that folder exists; otherwise null.
    /// </summary>
    public static string? BaseDirectoryFor(string? executableDirectory)
    {
        if (string.IsNullOrEmpty(executableDirectory))
            return null;
        var macOs = Path.TrimEndingDirectorySeparator(executableDirectory);
        if (!string.Equals(Path.GetFileName(macOs), "MacOS", StringComparison.Ordinal))
            return null;
        var contents = Path.GetDirectoryName(macOs);
        if (contents is null || !string.Equals(Path.GetFileName(contents), "Contents", StringComparison.Ordinal))
            return null;
        var app = Path.Combine(contents, AppDirectory);
        return Directory.Exists(app) ? app + Path.DirectorySeparatorChar : null; // disk-io: the bundle layout, before any content is mounted
    }
}

namespace MainframeEngine.Editor;

/// <summary>The operating system whose install layout and PATH rules <see cref="CutbackLocator"/> follows.</summary>
public enum OsKind
{
    MacOS,
    Windows,
    Linux,
}

/// <summary>
/// Finds the Cutback app (the video trimmer that reads Mainframe recordings): the Editor Settings path,
/// then PATH, then the usual install location per OS. Null when it is not installed. On macOS the setting may name the
/// <c>.app</c> bundle itself, and a bundle is started through <c>open</c> (<see cref="LaunchCommand"/>).
/// </summary>
public static class CutbackLocator
{
    /// <summary>Where the user is sent to install Cutback when <see cref="Find"/> finds nothing.</summary>
    public const string ReleasesUrl = "https://github.com/brogan89/Cutback/releases";

    /// <summary>
    /// <see cref="Find"/> against this process's PATH, home and local application data folder, on the OS it runs on.
    /// </summary>
    public static string? FindForThisMachine(string? settingPath) => Find(
        settingPath, Environment.GetEnvironmentVariable("PATH"),
        OperatingSystem.IsWindows() ? OsKind.Windows : OperatingSystem.IsMacOS() ? OsKind.MacOS : OsKind.Linux,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        File.Exists,
        Directory.Exists);

    /// <summary>
    /// The first existing Cutback in this order: <paramref name="settingPath"/> (when set; on macOS also an existing
    /// <c>.app</c> folder, what the Finder shows as the app), each PATH folder, then the OS's install location.
    /// <paramref name="fileExists"/> and <paramref name="directoryExists"/> are injected so every OS's layout can be tested
    /// on any host.
    /// </summary>
    public static string? Find(string? settingPath, string? pathVariable, OsKind os, string home, string? localAppData, Func<string, bool> fileExists,
        Func<string, bool>? directoryExists = null)
    {
        if (!string.IsNullOrWhiteSpace(settingPath) &&
            (fileExists(settingPath) || (os == OsKind.MacOS && BundleOf(settingPath) is { } bundle && bundle == TrimSlash(settingPath) &&
                                         directoryExists?.Invoke(settingPath) == true)))
            return settingPath;

        var separator = os == OsKind.Windows ? ';' : ':';
        var join = os == OsKind.Windows ? '\\' : '/';
        string[] names = os == OsKind.Windows ? ["Cutback.exe", "cutback.exe"] : ["cutback", "Cutback"];
        foreach (var folder in (pathVariable ?? "").Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var name in names)
            {
                var candidate = folder.Trim('"').TrimEnd('/', '\\') + join + name;
                if (fileExists(candidate))
                    return candidate;
            }
        }

        string? known = os switch
        {
            OsKind.MacOS => "/Applications/Cutback.app/Contents/MacOS/Cutback",
            OsKind.Windows when localAppData is not null => localAppData.TrimEnd('\\') + @"\Programs\Cutback\Cutback.exe",
            OsKind.Linux => home.TrimEnd('/') + "/.local/bin/cutback",
            _ => null,
        };
        return known is not null && fileExists(known) ? known : null;
    }

    /// <summary>
    /// How to start <paramref name="app"/> on <paramref name="file"/>. On macOS a bundle (<c>X.app</c>, or its
    /// <c>X.app/Contents/MacOS/*</c> executable) goes through <c>open -a X.app --args file</c>: Launch Services starts and
    /// activates it as an app of its own, outside the editor's process tree (a child would die with the editor, and an
    /// executable started directly opens behind it). Anything else runs directly with the file as its argument.
    /// </summary>
    public static (string FileName, IReadOnlyList<string> Arguments) LaunchCommand(string app, string file, OsKind os)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(app);
        ArgumentNullException.ThrowIfNull(file);
        if (os == OsKind.MacOS && BundleOf(app) is { } bundle)
            return ("open", ["-a", bundle, "--args", file]);
        return (app, [file]);
    }

    /// <summary><c>/x/Cutback.app</c> for <c>/x/Cutback.app</c>, <c>/x/Cutback.app/</c> or <c>/x/Cutback.app/Contents/MacOS/Cutback</c>; else null.</summary>
    private static string? BundleOf(string path)
    {
        var trimmed = TrimSlash(path);
        if (trimmed.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            return trimmed;
        var inside = trimmed.IndexOf(".app/Contents/MacOS/", StringComparison.OrdinalIgnoreCase);
        return inside > 0 ? trimmed[..(inside + ".app".Length)] : null;
    }

    private static string TrimSlash(string path) => path.TrimEnd('/');
}

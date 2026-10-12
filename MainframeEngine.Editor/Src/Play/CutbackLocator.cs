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
/// then PATH, then the usual install location per OS. Null when it is not installed.
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
        File.Exists);

    /// <summary>
    /// The first existing Cutback in this order: <paramref name="settingPath"/> (when set), each PATH folder, then the
    /// OS's install location. <paramref name="fileExists"/> is injected so every OS's layout can be tested on any host.
    /// </summary>
    public static string? Find(string? settingPath, string? pathVariable, OsKind os, string home, string? localAppData, Func<string, bool> fileExists)
    {
        if (!string.IsNullOrWhiteSpace(settingPath) && fileExists(settingPath))
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
}

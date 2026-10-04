namespace MainframeEngine;

/// <summary>
/// Per-user, per-game writable folders (logs, saves, settings): never the install folder, which may be read-only.
/// <list type="bullet">
/// <item>Windows: <c>%APPDATA%\{game}</c></item>
/// <item>macOS: <c>~/Library/Application Support/{game}</c></item>
/// <item>Linux: <c>$XDG_DATA_HOME/{game}</c> (default <c>~/.local/share/{game}</c>)</item>
/// </list>
/// <c>MAINFRAME_USER_DATA</c> overrides the base folder (tests, CI, portable installs).
/// </summary>
public static class UserDataPaths
{
    /// <summary>Environment variable that replaces the per-OS base folder.</summary>
    public const string OverrideVariable = "MAINFRAME_USER_DATA";

    /// <summary>The base folder holding one sub-folder per game.</summary>
    public static string BaseDirectory
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable(OverrideVariable);
            if (!string.IsNullOrWhiteSpace(overridden))
                return Path.GetFullPath(overridden);
            if (OperatingSystem.IsWindows())
                return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (OperatingSystem.IsMacOS())
                return Path.Combine(home, "Library", "Application Support");
            var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            return !string.IsNullOrWhiteSpace(xdg) && Path.IsPathRooted(xdg) ? xdg : Path.Combine(home, ".local", "share");
        }
    }

    /// <summary><c>{BaseDirectory}/{game}</c> (not created).</summary>
    public static string GetDirectory(string gameName) => Path.Combine(BaseDirectory, SafeName(gameName));

    /// <summary><c>{BaseDirectory}/{game}/logs</c> (not created).</summary>
    public static string LogDirectory(string gameName) => Path.Combine(GetDirectory(gameName), "logs");

    /// <summary><paramref name="gameName"/> with characters that are invalid in file names replaced by <c>_</c>.</summary>
    public static string SafeName(string gameName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameName);
        var invalid = Path.GetInvalidFileNameChars();
        Span<char> buffer = gameName.Length <= 256 ? stackalloc char[gameName.Length] : new char[gameName.Length];
        for (var i = 0; i < gameName.Length; i++)
        {
            var c = gameName[i];
            buffer[i] = Array.IndexOf(invalid, c) >= 0 || c is '/' or '\\' or ':' ? '_' : c;
        }

        var name = buffer.ToString().Trim().TrimEnd('.');
        return name.Length == 0 || name is "." or ".." ? "game" : name;
    }
}

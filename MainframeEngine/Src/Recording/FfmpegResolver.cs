namespace MainframeEngine;

internal enum OSPlatformKind
{
    Windows,
    MacOS,
    Linux,
}

/// <summary>Recording needs ffmpeg and none was found. The message names the platform's install command.</summary>
public sealed class FfmpegNotFoundException(string message) : Exception(message);

/// <summary>
/// Finds the user's ffmpeg: <c>--ffmpeg</c> → <c>MAINFRAME_FFMPEG</c> → <c>PATH</c> → common install folders.
/// The engine never ships or links ffmpeg (ADR 0190); recording runs it as a separate program.
/// </summary>
internal static class FfmpegResolver
{
    public const string EnvironmentVariable = "MAINFRAME_FFMPEG";

    public static string ResolveForThisMachine(string? explicitPath) =>
        Resolve(explicitPath, Environment.GetEnvironmentVariable(EnvironmentVariable), Environment.GetEnvironmentVariable("PATH"),
            CurrentOs, File.Exists);

    public static OSPlatformKind CurrentOs =>
        OperatingSystem.IsWindows() ? OSPlatformKind.Windows : OperatingSystem.IsMacOS() ? OSPlatformKind.MacOS : OSPlatformKind.Linux;

    public static string Resolve(string? explicitPath, string? envPath, string? pathVariable, OSPlatformKind os, Func<string, bool> fileExists)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return fileExists(explicitPath) ? explicitPath : throw new FfmpegNotFoundException($"ffmpeg was not found at {explicitPath}.");
        if (!string.IsNullOrWhiteSpace(envPath) && fileExists(envPath))
            return envPath;

        var exe = os == OSPlatformKind.Windows ? "ffmpeg.exe" : "ffmpeg";
        var separator = os == OSPlatformKind.Windows ? ';' : ':';
        var join = os == OSPlatformKind.Windows ? '\\' : '/';
        var folders = (pathVariable ?? "").Split(separator, StringSplitOptions.RemoveEmptyEntries).Concat(KnownFolders(os));
        foreach (var folder in folders)
        {
            var candidate = folder.TrimEnd('/', '\\') + join + exe;
            if (fileExists(candidate))
                return candidate;
        }

        throw new FfmpegNotFoundException("Recording needs ffmpeg, and it was not found. Install it with " + InstallHint(os) +
            ", or pass --ffmpeg <path> (or set " + EnvironmentVariable + ").");
    }

    private static IEnumerable<string> KnownFolders(OSPlatformKind os) => os switch
    {
        OSPlatformKind.MacOS => ["/opt/homebrew/bin", "/usr/local/bin"],
        OSPlatformKind.Windows =>
        [
            @"C:\ffmpeg\bin",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links"),
        ],
        _ => ["/usr/bin", "/usr/local/bin", "/snap/bin"],
    };

    private static string InstallHint(OSPlatformKind os) => os switch
    {
        OSPlatformKind.MacOS => "`brew install ffmpeg`",
        OSPlatformKind.Windows => "`winget install Gyan.FFmpeg`",
        _ => "`sudo apt install ffmpeg` (or your distribution's package manager)",
    };
}

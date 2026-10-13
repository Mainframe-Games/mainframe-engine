using System.Runtime.InteropServices;

namespace MainframeEngine.Video.Media;

/// <summary>
/// Finds ffmpeg and ffprobe. Resolution order: the user's saved setting, then <c>PATH</c>, then the
/// usual per-platform install directories. Both binaries must exist in the same directory.
/// </summary>
/// <remarks>
/// The default constructor reads the real environment. The other constructor takes every input
/// explicitly so the resolution order is unit-testable without touching the file system.
/// </remarks>
public sealed class FfmpegLocator
{
    private readonly OSPlatform _platform;
    private readonly Func<string, bool> _fileExists;
    private readonly string? _userConfiguredPath;
    private readonly string? _pathVariable;
    private readonly IReadOnlyDictionary<string, string> _environment;

    /// <param name="userConfiguredPath">The ffmpeg path (file or directory) saved in app settings, if any.</param>
    public FfmpegLocator(string? userConfiguredPath = null)
        : this(
            CurrentPlatform(),
            File.Exists,
            userConfiguredPath,
            Environment.GetEnvironmentVariable("PATH"),
            CurrentEnvironment())
    {
    }

    public FfmpegLocator(
        OSPlatform platform,
        Func<string, bool> fileExists,
        string? userConfiguredPath,
        string? pathVariable,
        IReadOnlyDictionary<string, string> environment)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        ArgumentNullException.ThrowIfNull(environment);
        _platform = platform;
        _fileExists = fileExists;
        _userConfiguredPath = string.IsNullOrWhiteSpace(userConfiguredPath) ? null : userConfiguredPath;
        _pathVariable = pathVariable;
        _environment = environment;
    }

    /// <summary>Resolves the binaries or throws a user-readable <see cref="FfmpegMissingException"/>.</summary>
    public FfmpegLocation Locate() => TryLocate() ?? throw new FfmpegMissingException(BuildNotFoundMessage());

    /// <summary>Resolves the binaries, or returns null if they are not installed anywhere we look.</summary>
    public FfmpegLocation? TryLocate()
    {
        if (_userConfiguredPath is not null)
        {
            var dir = _fileExists(_userConfiguredPath) ? DirectoryOf(_userConfiguredPath) : _userConfiguredPath;
            if (dir is not null && Probe(dir, FfmpegLocationSource.UserSetting) is { } fromSetting)
            {
                return fromSetting;
            }
        }

        foreach (var dir in PathDirectories())
        {
            if (Probe(dir, FfmpegLocationSource.Path) is { } fromPath)
            {
                return fromPath;
            }
        }

        foreach (var dir in CommonDirectories())
        {
            if (Probe(dir, FfmpegLocationSource.CommonLocation) is { } fromCommon)
            {
                return fromCommon;
            }
        }

        return null;
    }

    /// <summary>The platform-specific file name for a tool, e.g. <c>ffmpeg.exe</c> on Windows.</summary>
    public string ExecutableName(string tool) => _platform == OSPlatform.Windows ? tool + ".exe" : tool;

    private FfmpegLocation? Probe(string directory, FfmpegLocationSource source)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        var ffmpeg = Join(directory, ExecutableName("ffmpeg"));
        var ffprobe = Join(directory, ExecutableName("ffprobe"));
        return _fileExists(ffmpeg) && _fileExists(ffprobe)
            ? new FfmpegLocation(ffmpeg, ffprobe, TrimSeparators(directory), source)
            : null;
    }

    // Path.GetDirectoryName and Path.Combine use the *host* OS separators, which is wrong when
    // this locator is configured for another platform (as the unit tests do), so paths are split
    // and joined by hand using the target platform's separator.

    private string Join(string directory, string file)
    {
        var separator = _platform == OSPlatform.Windows ? '\\' : '/';
        return TrimSeparators(directory) + separator + file;
    }

    private string? DirectoryOf(string path)
    {
        var index = _platform == OSPlatform.Windows ? path.LastIndexOfAny(['\\', '/']) : path.LastIndexOf('/');
        return index < 0 ? null : path[..Math.Max(index, 1)];
    }

    private static string TrimSeparators(string directory)
    {
        var trimmed = directory.TrimEnd('\\', '/');
        return trimmed.Length == 0 ? directory : trimmed;
    }

    private string[] PathDirectories()
    {
        if (string.IsNullOrEmpty(_pathVariable))
        {
            return [];
        }

        var separator = _platform == OSPlatform.Windows ? ';' : ':';
        return _pathVariable.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private IEnumerable<string> CommonDirectories()
    {
        if (_platform == OSPlatform.Windows)
        {
            var programFiles = Env("ProgramFiles");
            var localAppData = Env("LOCALAPPDATA");
            var userProfile = Env("USERPROFILE");
            var programData = Env("ProgramData");
            return
            [
                localAppData is null ? "" : $@"{localAppData}\Microsoft\WinGet\Links",
                programFiles is null ? "" : $@"{programFiles}\ffmpeg\bin",
                programFiles is null ? "" : $@"{programFiles}\FFmpeg\bin",
                programData is null ? "" : $@"{programData}\chocolatey\bin",
                userProfile is null ? "" : $@"{userProfile}\scoop\shims",
                @"C:\ffmpeg\bin",
            ];
        }

        if (_platform == OSPlatform.OSX)
        {
            return ["/opt/homebrew/bin", "/usr/local/bin", "/opt/local/bin"];
        }

        var home = Env("HOME");
        return
        [
            "/usr/bin",
            "/usr/local/bin",
            "/snap/bin",
            home is null ? "" : $"{home}/.local/bin",
            "/var/lib/flatpak/exports/bin",
        ];
    }

    private string? Env(string name) => _environment.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value) ? value : null;

    private string BuildNotFoundMessage()
    {
        var install = _platform == OSPlatform.Windows
            ? "winget install Gyan.FFmpeg"
            : _platform == OSPlatform.OSX
                ? "brew install ffmpeg"
                : "sudo apt install ffmpeg   (or your distribution's equivalent)";

        var message = "The video editor needs ffmpeg and ffprobe, but could not find them on PATH or in the usual install locations.\n\n"
            + $"Install FFmpeg with:\n    {install}\n\n"
            + "Then restart the editor, or point it at the ffmpeg binary.";

        if (_userConfiguredPath is not null)
        {
            message += $"\n\nThe configured path \"{_userConfiguredPath}\" no longer contains both ffmpeg and ffprobe.";
        }

        var ffmpegOnly = PathDirectories().Concat(CommonDirectories())
            .FirstOrDefault(d => !string.IsNullOrWhiteSpace(d) && _fileExists(Join(d, ExecutableName("ffmpeg"))));
        if (ffmpegOnly is not null)
        {
            message += $"\n\nffmpeg was found in \"{ffmpegOnly}\" but ffprobe was not. The video editor needs both; a full FFmpeg install provides them together.";
        }

        return message;
    }

    private static OSPlatform CurrentPlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            return OSPlatform.Windows;
        }

        return OperatingSystem.IsMacOS() ? OSPlatform.OSX : OSPlatform.Linux;
    }

    private static Dictionary<string, string> CurrentEnvironment()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
            {
                result[key] = value;
            }
        }

        return result;
    }
}

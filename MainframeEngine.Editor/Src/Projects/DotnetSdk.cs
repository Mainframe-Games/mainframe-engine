using System.Globalization;
using System.Runtime.InteropServices;

namespace MainframeEngine.Editor;

/// <summary>The outcome of <see cref="DotnetSdk.DetectAsync"/>.</summary>
/// <param name="DotnetPath">The <c>dotnet</c> executable that was asked (null when none was found).</param>
/// <param name="Versions">Every installed SDK version, as <c>dotnet --list-sdks</c> printed them, in its order.</param>
/// <param name="BestVersion">The highest installed SDK version, or null when there is none.</param>
/// <param name="IsSupported">An SDK of <see cref="DotnetSdk.RequiredMajor"/> or newer is installed.</param>
/// <param name="Error">
/// Why projects cannot be created or built: a user-facing sentence ending with <see cref="DotnetSdk.DownloadUrl"/>.
/// Null when <see cref="IsSupported"/>.
/// </param>
public sealed record DotnetSdkInfo(string? DotnetPath, IReadOnlyList<string> Versions, string? BestVersion, bool IsSupported, string? Error);

/// <summary>
/// Finds the <c>dotnet</c> host and checks that a .NET SDK of <see cref="RequiredMajor"/> or newer is installed. The
/// editor itself is self-contained, but creating a game (<c>dotnet new</c>) and building it for Play
/// (<c>dotnet build</c>) need the SDK (docs/design/release.md). An editor started from Finder or a desktop shortcut gets
/// a minimal PATH, so the well-known install folders are searched too.
/// </summary>
public static class DotnetSdk
{
    /// <summary>Where users get the SDK; every "SDK missing" message ends with it.</summary>
    public const string DownloadUrl = "https://dotnet.microsoft.com/download/dotnet/10.0";

    /// <summary>The oldest SDK major version that builds game projects (net10.0).</summary>
    public const int RequiredMajor = 10;

    /// <summary>Environment variable naming the <c>dotnet</c> executable (or its folder) to use; checked first.</summary>
    public const string OverrideVariable = "MAINFRAME_DOTNET";

    /// <summary>How long <c>dotnet --list-sdks</c> may take (a cold first run can be slow).</summary>
    public static readonly TimeSpan ListSdksTimeout = TimeSpan.FromSeconds(20);

    private const string Requirement = "The .NET 10 SDK is required to create and build game projects";

    /// <summary>The executable's file name on this OS (<c>dotnet.exe</c> on Windows).</summary>
    public static string ExecutableName => OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";

    /// <summary>
    /// The <c>dotnet</c> executable: <see cref="OverrideVariable"/>, then <c>DOTNET_ROOT</c> (and
    /// <c>DOTNET_ROOT_ARM64</c>), then each PATH entry, then the well-known install folders
    /// (<see cref="WellKnownInstallDirectories"/>). Null when none holds it.
    /// </summary>
    public static string? FindDotnet()
    {
        foreach (var candidate in Candidates())
        {
            var file = AsExecutable(candidate);
            if (file is not null)
                return file;
        }

        return null;
    }

    /// <summary>The install folders searched after PATH, in order, for this OS.</summary>
    public static IReadOnlyList<string> WellKnownInstallDirectories()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var list = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            var programFiles = Environment.GetEnvironmentVariable("ProgramFiles");
            if (!string.IsNullOrEmpty(programFiles))
                list.Add(Path.Combine(programFiles, "dotnet"));
            list.Add(@"C:\Program Files\dotnet");
        }
        else
        {
            list.AddRange(["/usr/local/share/dotnet", "/opt/homebrew/share/dotnet", "/opt/homebrew/lib/dotnet", "/usr/share/dotnet", "/usr/lib/dotnet"]);
        }

        if (!string.IsNullOrEmpty(home))
            list.Add(Path.Combine(home, ".dotnet"));
        return list;
    }

    /// <summary>
    /// Runs <c>dotnet --list-sdks</c> (<paramref name="dotnetPath"/>, or <see cref="FindDotnet"/>) and reports the
    /// installed SDKs. Never throws for a missing or broken <c>dotnet</c>, a timeout or cancellation: the result is
    /// unsupported, with an <see cref="DotnetSdkInfo.Error"/> to show the user.
    /// </summary>
    public static async Task<DotnetSdkInfo> DetectAsync(string? dotnetPath = null, CancellationToken ct = default)
    {
        dotnetPath ??= FindDotnet();
        if (dotnetPath is null)
            return Unsupported(null, [], null,
                $"{Requirement}, but 'dotnet' was not found. Install it from {DownloadUrl} and restart the editor.");

        var result = await ProcessRunner.RunAsync(dotnetPath, ["--list-sdks"], workingDirectory: NeutralWorkingDirectory(),
            timeout: ListSdksTimeout, cancellationToken: ct).ConfigureAwait(false);
        if (result.StartError is not null)
            return Unsupported(dotnetPath, [], null,
                $"{Requirement}, but '{dotnetPath}' could not be run ({result.StartError}). Install the SDK from {DownloadUrl} and restart the editor.");
        if (result.Cancelled)
            return Unsupported(dotnetPath, [], null, $"The .NET SDK check was cancelled. {Requirement}; get it from {DownloadUrl}.");
        if (result.TimedOut)
            return Unsupported(dotnetPath, [], null,
                $"{Requirement}, but '{dotnetPath} --list-sdks' did not answer within {ListSdksTimeout.TotalSeconds:0} seconds. " +
                $"Check the installation (reinstall from {DownloadUrl}) and restart the editor.");
        if (result.ExitCode != 0)
            return Unsupported(dotnetPath, [], null,
                $"{Requirement}, but '{dotnetPath} --list-sdks' failed (exit code {result.ExitCode}): {Tail(result.Output, 3)} " +
                $"Install the SDK from {DownloadUrl} and restart the editor.");
        return FromListSdksOutput(dotnetPath, result.Output);
    }

    /// <summary>
    /// Parses <c>dotnet --list-sdks</c> output (lines like <c>10.0.401 [/usr/local/share/dotnet/sdk]</c>; prerelease
    /// versions such as <c>10.0.100-rc.2.25502.107</c> count). Lines that are not SDK entries are ignored.
    /// </summary>
    public static DotnetSdkInfo FromListSdksOutput(string? dotnetPath, IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var versions = new List<string>();
        string? best = null;
        var bestParsed = default(SdkVersion);
        foreach (var raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            var line = raw.Trim();
            var bracket = line.IndexOf(" [", StringComparison.Ordinal);
            if (bracket <= 0 || !line.EndsWith(']'))
                continue;
            var version = line[..bracket].Trim();
            if (!SdkVersion.TryParse(version, out var parsed) || versions.Contains(version, StringComparer.Ordinal))
                continue;
            versions.Add(version);
            if (best is null || parsed.CompareTo(bestParsed) > 0)
            {
                best = version;
                bestParsed = parsed;
            }
        }

        if (best is not null && bestParsed.Major >= RequiredMajor)
            return new DotnetSdkInfo(dotnetPath, versions, best, true, null);

        var where = dotnetPath is null ? "" : $" ('{dotnetPath}')";
        var error = best is null
            ? $"{Requirement}, but no .NET SDK is installed{where}; only the runtime is. Install the SDK from {DownloadUrl} and restart the editor."
            : $"{Requirement}, but the newest installed SDK is {best}{where}. Install .NET 10 from {DownloadUrl} and restart the editor.";
        return Unsupported(dotnetPath, versions, best, error);
    }

    private static DotnetSdkInfo Unsupported(string? dotnetPath, IReadOnlyList<string> versions, string? best, string error) =>
        new(dotnetPath, versions, best, false, error);

    private static string Tail(IReadOnlyList<string> output, int count)
    {
        var lines = output.Where(l => !string.IsNullOrWhiteSpace(l)).TakeLast(count).Select(l => l.Trim()).ToArray();
        return lines.Length == 0 ? "(no output)." : string.Join(' ', lines);
    }

    // A folder without a global.json above it, so a pinned SDK in the caller's folder cannot make the check fail.
    internal static string NeutralWorkingDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return !string.IsNullOrEmpty(home) && Directory.Exists(home) ? home : Path.GetTempPath();
    }

    private static IEnumerable<string> Candidates()
    {
        if (Environment.GetEnvironmentVariable(OverrideVariable) is { Length: > 0 } explicitPath)
            yield return ExpandHome(explicitPath.Trim().Trim('"'));

        var arm64First = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        foreach (var variable in arm64First ? (string[])["DOTNET_ROOT_ARM64", "DOTNET_ROOT"] : ["DOTNET_ROOT", "DOTNET_ROOT_ARM64"])
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } root)
                yield return ExpandHome(root.Trim().Trim('"'));

        if (Environment.GetEnvironmentVariable("PATH") is { Length: > 0 } path)
            foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                yield return ExpandHome(entry.Trim('"'));

        foreach (var directory in WellKnownInstallDirectories())
            yield return directory;
    }

    // A candidate is the executable itself or a folder holding it; null when it is neither.
    private static string? AsExecutable(string candidate)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(candidate) || !Path.IsPathFullyQualified(candidate))
                return null;
            if (File.Exists(candidate) && string.Equals(Path.GetFileName(candidate), ExecutableName,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                return Path.GetFullPath(candidate);
            var inFolder = Path.Combine(candidate, ExecutableName);
            return File.Exists(inFolder) ? Path.GetFullPath(inFolder) : null;
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private static string ExpandHome(string path)
    {
        if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home))
                return path.Length == 1 ? home : Path.Combine(home, path[2..]);
        }

        return path;
    }

    /// <summary>An SDK version: <c>major.minor.patch</c> plus an optional prerelease label (which sorts lower).</summary>
    private readonly record struct SdkVersion(int Major, int Minor, int Patch, string? Prerelease) : IComparable<SdkVersion>
    {
        public static bool TryParse(string text, out SdkVersion version)
        {
            version = default;
            var plus = text.IndexOf('+', StringComparison.Ordinal);
            if (plus >= 0)
                text = text[..plus];
            var dash = text.IndexOf('-', StringComparison.Ordinal);
            var core = dash >= 0 ? text[..dash] : text;
            var prerelease = dash >= 0 ? text[(dash + 1)..] : null;
            var parts = core.Split('.');
            if (parts.Length is < 2 or > 3)
                return false;
            var numbers = new int[3];
            for (var i = 0; i < parts.Length; i++)
                if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
                    return false;
            if (prerelease is { Length: 0 })
                return false;
            version = new SdkVersion(numbers[0], numbers[1], numbers[2], prerelease);
            return true;
        }

        public int CompareTo(SdkVersion other)
        {
            var c = Major.CompareTo(other.Major);
            if (c == 0)
                c = Minor.CompareTo(other.Minor);
            if (c == 0)
                c = Patch.CompareTo(other.Patch);
            if (c != 0)
                return c;
            if (Prerelease is null || other.Prerelease is null)
                return (Prerelease is null).CompareTo(other.Prerelease is null); // a release beats its prereleases
            return ComparePrerelease(Prerelease, other.Prerelease);
        }

        // SemVer: dot-separated identifiers, numeric ones compared as numbers.
        private static int ComparePrerelease(string a, string b)
        {
            var left = a.Split('.');
            var right = b.Split('.');
            for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
            {
                var leftNumeric = long.TryParse(left[i], NumberStyles.None, CultureInfo.InvariantCulture, out var l);
                var rightNumeric = long.TryParse(right[i], NumberStyles.None, CultureInfo.InvariantCulture, out var r);
                var c = leftNumeric && rightNumeric ? l.CompareTo(r)
                    : leftNumeric ? -1
                    : rightNumeric ? 1
                    : string.CompareOrdinal(left[i], right[i]);
                if (c != 0)
                    return c;
            }

            return left.Length.CompareTo(right.Length);
        }
    }
}

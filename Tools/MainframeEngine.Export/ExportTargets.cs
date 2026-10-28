using System.Runtime.InteropServices;
using System.Text;

namespace MainframeEngine.Export;

/// <summary>The operating system an export runs on.</summary>
public enum ExportHost
{
    Windows,
    MacOS,
    Linux,
}

/// <summary>
/// The desktop targets an export can build: the RIDs the engine ships natives for (<c>MainframeEngine/runtimes/</c>), per
/// platform, and the names an export derives from a preset (slug, executable, archive).
/// </summary>
public static class ExportTargets
{
    /// <summary>The supported RIDs of each platform, the default first.</summary>
    public static IReadOnlyList<string> RidsOf(ExportPlatform platform) => platform switch
    {
        ExportPlatform.Windows => [.. EngineNatives.Rids.Where(r => r.StartsWith("win-", StringComparison.Ordinal)).OrderBy(Order)],
        ExportPlatform.MacOS => [.. EngineNatives.Rids.Where(r => r.StartsWith("osx-", StringComparison.Ordinal)).OrderBy(Order)],
        ExportPlatform.Linux => [.. EngineNatives.Rids.Where(r => r.StartsWith("linux-", StringComparison.Ordinal)).OrderBy(Order)],
        _ => [],
    };

    // x64 first on Windows and Linux; arm64 first on macOS (Apple Silicon).
    private static int Order(string rid) => rid switch
    {
        "win-x64" or "linux-x64" or "osx-arm64" => 0,
        _ => 1,
    };

    /// <summary>The platform a RID belongs to, or null when it is not a desktop RID.</summary>
    public static ExportPlatform? PlatformOf(string rid)
    {
        ArgumentNullException.ThrowIfNull(rid);
        if (rid.StartsWith("win-", StringComparison.Ordinal))
            return ExportPlatform.Windows;
        if (rid.StartsWith("osx-", StringComparison.Ordinal))
            return ExportPlatform.MacOS;
        if (rid.StartsWith("linux-", StringComparison.Ordinal))
            return ExportPlatform.Linux;
        return null;
    }

    /// <summary>This machine's OS.</summary>
    public static ExportHost CurrentHost =>
        OperatingSystem.IsWindows() ? ExportHost.Windows : OperatingSystem.IsMacOS() ? ExportHost.MacOS : ExportHost.Linux;

    /// <summary>This machine's desktop RID (<c>osx-arm64</c>, <c>win-x64</c>, …).</summary>
    public static string CurrentRid
    {
        get
        {
            var arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
            return CurrentHost switch
            {
                ExportHost.Windows => "win-" + arch,
                ExportHost.MacOS => "osx-" + arch,
                _ => "linux-" + arch,
            };
        }
    }

    /// <summary>The platform of <see cref="CurrentHost"/>.</summary>
    public static ExportPlatform PlatformOf(ExportHost host) => host switch
    {
        ExportHost.Windows => ExportPlatform.Windows,
        ExportHost.MacOS => ExportPlatform.MacOS,
        _ => ExportPlatform.Linux,
    };

    /// <summary>
    /// The host's own target for an "Add Preset" default: its RID when the engine ships natives for it, else the platform's
    /// default RID.
    /// </summary>
    public static string HostRid(ExportHost host, string hostRid)
    {
        var rids = RidsOf(PlatformOf(host));
        return rids.Contains(hostRid, StringComparer.Ordinal) ? hostRid : rids.Count > 0 ? rids[0] : hostRid;
    }

    /// <summary>The preset's RID: its <c>rid</c>, else the platform's first supported one.</summary>
    public static string EffectiveRid(ExportPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        if (preset.Options is DesktopExportOptions { Rid.Length: > 0 } desktop)
            return desktop.Rid;
        var rids = RidsOf(preset.Platform);
        return rids.Count > 0 ? rids[0] : "";
    }

    /// <summary>
    /// A folder-safe slug of a preset name: lower case ASCII letters and digits, other runs as one '-'
    /// (<c>Windows (Steam demo)</c> → <c>windows-steam-demo</c>); <c>preset</c> when nothing is left.
    /// </summary>
    public static string Slug(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var slug = new StringBuilder(name.Length);
        var dash = false;
        foreach (var c in name)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                if (dash && slug.Length > 0)
                    slug.Append('-');
                slug.Append(char.ToLowerInvariant(c));
                dash = false;
            }
            else
            {
                dash = true;
            }
        }

        return slug.Length == 0 ? "preset" : slug.ToString();
    }

    /// <summary>The default executable name: the project name without spaces (the old script's rule).</summary>
    public static string DefaultExe(string projectName)
    {
        ArgumentNullException.ThrowIfNull(projectName);
        var exe = projectName.Replace(" ", "", StringComparison.Ordinal);
        return exe.Length == 0 ? "Game" : exe;
    }

    /// <summary>Whether <paramref name="version"/> is <c>MAJOR.MINOR.PATCH</c> with an optional <c>-suffix</c>.</summary>
    public static bool IsSemanticVersion(string version)
    {
        ArgumentNullException.ThrowIfNull(version);
        var dash = version.IndexOf('-', StringComparison.Ordinal);
        var core = dash >= 0 ? version[..dash] : version;
        if (dash >= 0 && dash == version.Length - 1)
            return false;
        var parts = core.Split('.');
        return parts.Length == 3 && parts.All(p => p.Length > 0 && p.All(char.IsAsciiDigit));
    }
}

/// <summary>The engine's natives per RID (generated from <c>MainframeEngine/runtimes/*/native</c>; see the project file).</summary>
internal static partial class EngineNatives
{
    private static readonly Lazy<Dictionary<string, string[]>> ByRid = new(Build);

    /// <summary>Every RID with engine natives, ordered.</summary>
    public static IReadOnlyList<string> Rids => [.. ByRid.Value.Keys.Order(StringComparer.Ordinal)];

    /// <summary>The native file names of <paramref name="rid"/> (empty for an unknown RID).</summary>
    public static IReadOnlyList<string> FilesOf(string rid) => ByRid.Value.TryGetValue(rid, out var files) ? files : [];

    private static Dictionary<string, string[]> Build()
    {
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (folder, file) in Files)
        {
            var rid = folder.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)[0];
            if (!map.TryGetValue(rid, out var list))
                map[rid] = list = [];
            list.Add(file);
        }

        return map.ToDictionary(p => p.Key, p => p.Value.Order(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
    }
}

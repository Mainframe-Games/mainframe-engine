using System.Reflection;

namespace MainframeEngine;

/// <summary>Facts about the running engine build.</summary>
public static class EngineInfo
{
    /// <summary>
    /// The engine's version (SemVer, without build metadata): the release tag's <c>X.Y.Z</c> in published builds,
    /// <c>0.0.0-dev</c> in local and CI builds (see docs/design/release.md).
    /// </summary>
    public static string Version { get; } = ReadVersion();

    /// <summary>
    /// The commit the engine was built from: the <c>+&lt;sha&gt;</c> build metadata the SDK's SourceLink adds to the
    /// informational version (shortened to 8 digits); null when the build had none (no git, or SourceLink off).
    /// </summary>
    public static string? Commit { get; } = ReadCommit(
        typeof(EngineInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    /// <summary>The commit in an informational version (<c>0.42.0+92dbb40a…</c> → <c>92dbb40a</c>), or null.</summary>
    internal static string? ReadCommit(string? informational)
    {
        if (string.IsNullOrWhiteSpace(informational))
            return null;
        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        if (plus < 0 || plus == informational.Length - 1)
            return null;
        var metadata = informational[(plus + 1)..];
        // SourceLink appends the full sha; other metadata may come first (1.0.0+build.5.92dbb40a…): take the last part.
        var dot = metadata.LastIndexOf('.');
        var sha = dot >= 0 ? metadata[(dot + 1)..] : metadata;
        if (sha.Length < 7 || !IsHex(sha))
            return null;
        return sha.Length > 8 ? sha[..8] : sha;

        static bool IsHex(string text)
        {
            foreach (var c in text)
                if (!char.IsAsciiHexDigit(c))
                    return false;
            return true;
        }
    }

    /// <summary><see cref="Version"/> packed for <c>VkApplicationInfo.engineVersion</c> (0.0.0 for development builds).</summary>
    internal static Silk.NET.Core.Version32 VulkanVersion
    {
        get
        {
            if (!TryParse(Version, out var v))
                return new Silk.NET.Core.Version32(0, 0, 0);
            return new Silk.NET.Core.Version32((uint)v.Major, (uint)v.Minor, (uint)v.Build);
        }
    }

    /// <summary>
    /// True when a project created with engine <paramref name="projectVersion"/> may need an upgrade on this engine:
    /// the versions differ in major or minor. Development builds (<c>0.0.0-*</c>) and unparsable versions are always
    /// compatible.
    /// </summary>
    public static bool IsDifferentRelease(string? projectVersion) => IsDifferentRelease(projectVersion, Version);

    /// <summary><see cref="IsDifferentRelease(string?)"/> against an explicit engine version.</summary>
    public static bool IsDifferentRelease(string? projectVersion, string engineVersion)
    {
        if (!TryParse(projectVersion, out var project) || !TryParse(engineVersion, out var current))
            return false;
        if (project == new System.Version(0, 0, 0) || current == new System.Version(0, 0, 0))
            return false;
        return project.Major != current.Major || project.Minor != current.Minor;
    }

    private static bool TryParse(string? text, out System.Version version)
    {
        version = new System.Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var core = text.AsSpan();
        var cut = core.IndexOfAny('-', '+');
        if (cut >= 0)
            core = core[..cut];
        if (!System.Version.TryParse(core, out var parsed))
            return false;
        version = new System.Version(parsed.Major, Math.Max(0, parsed.Minor), Math.Max(0, parsed.Build));
        return true;
    }

    private static string ReadVersion()
    {
        var informational = typeof(EngineInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(informational))
            return typeof(EngineInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0-dev";
        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 ? informational[..plus] : informational;
    }
}

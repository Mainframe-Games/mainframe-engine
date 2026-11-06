namespace MainframeEngine;

/// <summary>
/// The file extensions the engine, editor and video library define. All of them start with <see cref="Prefix"/>
/// (ADR 0249), and <c>FileExtensionRuleTests</c> fails on a new one that does not. Projects made before ADR 0249 use the
/// <see cref="Legacy"/> names: the runtime still reads them (<see cref="Matches"/>, <see cref="FindExisting"/>,
/// <see cref="Upgrade"/>), and the editor renames them when it opens such a project.
/// </summary>
public static class FileExtensions
{
    /// <summary>What every engine extension starts with.</summary>
    public const string Prefix = ".mf";

    /// <summary>A scene (<c>x.mfscene</c>, JSON with its own UID).</summary>
    public const string Scene = ".mfscene";

    /// <summary>A resource (<c>x.mfres</c>, JSON with its own UID).</summary>
    public const string Resource = ".mfres";

    /// <summary>An adaptive music manifest (<c>x.mfmusic</c>, ADR 0196).</summary>
    public const string Music = ".mfmusic";

    /// <summary>An editor song (<c>x.mfsong</c>).</summary>
    public const string Song = ".mfsong";

    /// <summary>A save slot (<c>slot.mfsave</c>, header + payload).</summary>
    public const string Save = ".mfsave";

    /// <summary>The sidecar of an imported asset (<c>x.png.mfmeta</c>: its UID and import settings).</summary>
    public const string Meta = ".mfmeta";

    /// <summary>A light-probe bake's coefficients (<c>x.mfprobes</c>, the data file of <c>x.mfres</c>).</summary>
    public const string LightProbes = ".mfprobes";

    /// <summary>A surface shader's build lock (<c>x.slang.mfspvlock</c>: source hash and dependencies).</summary>
    public const string ShaderLock = ".mfspvlock";

    /// <summary>A surface shader's reflected layout (<c>x.slang.mflayout.json</c>).</summary>
    public const string ShaderLayout = ".mflayout.json";

    /// <summary>The project file (<c>project.mfproj</c>).</summary>
    public const string Project = ".mfproj";

    /// <summary>An export's content pack (<c>Assets.mfpak</c>).</summary>
    public const string Pack = ".mfpak";

    /// <summary>A video timeline (<c>x.mftimeline</c>, ADR 0241).</summary>
    public const string Timeline = ".mftimeline";

    /// <summary>A recording's capture sidecar (<c>video.mp4.mfcap.json</c>, ADR 0190).</summary>
    public const string CaptureSidecar = ".mfcap.json";

    /// <summary>A media file's cached transcript (<c>media.mp4.mftranscript.json</c>).</summary>
    public const string Transcript = ".mftranscript.json";

    /// <summary>The names before ADR 0249, each with the extension that replaced it.</summary>
    public static IReadOnlyList<(string Legacy, string Current)> Legacy { get; } =
    [
        (".mscene", Scene),
        (".mres", Resource),
        (".mmusic", Music),
        (".msong", Song),
        (".msave", Save),
        (".meta", Meta),
        (".probes", LightProbes),
        (".spvlock", ShaderLock),
        (".layout.json", ShaderLayout),
    ];

    /// <summary>The legacy name of <paramref name="current"/>, or null when it never had another.</summary>
    public static string? LegacyOf(string current)
    {
        ArgumentNullException.ThrowIfNull(current);
        foreach (var (legacy, now) in Legacy)
            if (string.Equals(now, current, StringComparison.OrdinalIgnoreCase))
                return legacy;
        return null;
    }

    /// <summary>
    /// Whether <paramref name="path"/> ends with <paramref name="current"/> or with its legacy name (case-insensitive).
    /// A current name never ends with a legacy one (<c>.mfmeta</c> does not end with <c>.meta</c>), so the two never
    /// match the same file.
    /// </summary>
    public static bool Matches(string path, string current)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path.EndsWith(current, StringComparison.OrdinalIgnoreCase)
               || (LegacyOf(current) is { } legacy && path.EndsWith(legacy, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// <paramref name="path"/> with a legacy scene, resource, music or song extension replaced by the current one; any
    /// other path unchanged. Sidecars (<c>.meta</c>, <c>.layout.json</c>, …) are never named in content, so they are not
    /// mapped here.
    /// </summary>
    public static string Upgrade(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        foreach (var current in (ReadOnlySpan<string>)[Scene, Resource, Music, Song])
        {
            var legacy = LegacyOf(current)!;
            if (path.EndsWith(legacy, StringComparison.OrdinalIgnoreCase))
                return string.Concat(path.AsSpan(0, path.Length - legacy.Length), current);
        }

        return path;
    }

    /// <summary>
    /// <paramref name="basePath"/> + <paramref name="current"/> when that file exists, else <paramref name="basePath"/> +
    /// its legacy name when that one does, else null. For sidecars: <c>FindExisting("a.png", Meta)</c> is
    /// <c>a.png.mfmeta</c> or, in a project not migrated yet, <c>a.png.meta</c>.
    /// </summary>
    public static string? FindExisting(string basePath, string current, Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(basePath);
        ArgumentNullException.ThrowIfNull(exists);
        var path = basePath + current;
        if (exists(path))
            return path;
        return LegacyOf(current) is { } legacy && exists(basePath + legacy) ? basePath + legacy : null;
    }
}

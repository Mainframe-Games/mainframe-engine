using System.Text;
using System.Text.RegularExpressions;

namespace MainframeEngine.Editor;

/// <summary>How a game project references the engine.</summary>
public enum ProjectEngineSource
{
    /// <summary>No engine reference was found (not a game project, or a layout the editor does not read).</summary>
    Unknown,

    /// <summary>The <c>MainframeEngine</c> NuGet package at a version (the mfgame template's default).</summary>
    Package,

    /// <summary>Project references into an engine checkout (<c>--engine-source project</c>): whatever that checkout is.</summary>
    Checkout,
}

/// <summary>A package project's engine version next to the editor's (<see cref="ProjectEngineVersion.StatusAgainst"/>).</summary>
public enum ProjectEngineStatus
{
    /// <summary>Not comparable: a checkout, an unknown reference, a pre-release, or a development editor (<c>0.0.0-dev</c>).</summary>
    Unversioned,

    /// <summary>Older than the editor: opening the project upgrades it (<see cref="ProjectEngineVersion.Upgrade"/>).</summary>
    Older,

    Current,

    /// <summary>Made with a newer engine than this editor; it is never downgraded.</summary>
    Newer,
}

/// <summary>
/// The engine a game project builds against, read from its MSBuild files: the mfgame template's
/// <c>&lt;MainframeEngineVersion&gt;</c> in <c>Directory.Build.props</c>, a literal <c>MainframeEngine</c>
/// <c>PackageReference</c>/<c>PackageVersion</c>, or a project reference into an engine checkout. The
/// <c>engineVersion</c> in <c>project.mfproj</c> is not used: it is what the project was made with and can lag the
/// package. The Project Manager shows it as a badge, and opening a project older than the editor upgrades its package
/// (<see cref="Upgrade"/>).
/// </summary>
public readonly partial record struct ProjectEngineVersion(ProjectEngineSource Source, string? Version)
{
    // Only the project's own MSBuild files: the root and two folders down (Game/Game.csproj), never build output or
    // Assets/ (which can hold thousands of files).
    private const int MaxDepth = 2;

    public static readonly ProjectEngineVersion Unknown = new(ProjectEngineSource.Unknown, null);

    /// <summary>The package version compared with <paramref name="editorVersion"/> (release versions only).</summary>
    public ProjectEngineStatus StatusAgainst(string editorVersion)
    {
        if (Source != ProjectEngineSource.Package || !ReleaseVersion.TryParse(Version, out var project) ||
            !ReleaseVersion.TryParse(editorVersion, out var editor))
            return ProjectEngineStatus.Unversioned;
        return project < editor ? ProjectEngineStatus.Older : project > editor ? ProjectEngineStatus.Newer : ProjectEngineStatus.Current;
    }

    /// <summary>The engine reference of the project in <paramref name="projectRoot"/>; <see cref="Unknown"/> when there is none.</summary>
    public static ProjectEngineVersion Read(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        if (!Directory.Exists(projectRoot))
            return Unknown;

        string? property = null, literal = null;
        var checkout = false;
        try
        {
            foreach (var file in MsBuildFiles(projectRoot))
            {
                var text = File.ReadAllText(file);
                if (property is null && PropertyRegex().Match(text) is { Success: true } p)
                    property = p.Groups["version"].Value;
                if (literal is null)
                    literal = PackageVersions(text).FirstOrDefault();
                checkout |= CheckoutRegex().IsMatch(text);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Unknown;
        }

        return property is not null ? new ProjectEngineVersion(ProjectEngineSource.Package, property)
            : literal is not null ? new ProjectEngineVersion(ProjectEngineSource.Package, literal)
            : checkout ? new ProjectEngineVersion(ProjectEngineSource.Checkout, null)
            : Unknown;
    }

    /// <summary>
    /// Moves a package project older than <paramref name="editorVersion"/> to it: the <c>MainframeEngineVersion</c>
    /// property and literal <c>MainframeEngine</c> package versions in its MSBuild files, and <c>engineVersion</c> in
    /// <c>project.mfproj</c>. The next build restores the new package. Returns the project-relative files rewritten; empty
    /// (nothing touched) for a current, newer, checkout or unknown project and for a development editor.
    /// </summary>
    public static IReadOnlyList<string> Upgrade(string projectRoot, string editorVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        if (Read(projectRoot).StatusAgainst(editorVersion) != ProjectEngineStatus.Older)
            return [];

        var root = Path.GetFullPath(projectRoot);
        var rewritten = new List<string>();
        foreach (var file in MsBuildFiles(root))
        {
            var text = File.ReadAllText(file, Encoding.UTF8);
            var updated = PropertyRegex().Replace(text, m => Splice(m, m.Groups["version"], editorVersion));
            updated = PackageRegex().Replace(updated, m => VersionAttributeRegex().Replace(m.Value, a =>
                a.Groups["version"].Value.StartsWith('$') ? a.Value : Splice(a, a.Groups["version"], editorVersion)));
            if (Write(file, text, updated))
                rewritten.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
        }

        var mfproj = Path.Combine(root, ProjectSettings.FileName);
        if (File.Exists(mfproj))
        {
            var text = File.ReadAllText(mfproj, Encoding.UTF8);
            var updated = MfprojVersionRegex().Replace(text, m => Splice(m, m.Groups["version"], editorVersion), 1);
            if (Write(mfproj, text, updated))
                rewritten.Add(ProjectSettings.FileName);
        }

        rewritten.Sort(StringComparer.Ordinal);
        return rewritten;
    }

    // The literal (non-property) Version attributes of MainframeEngine package elements.
    private static IEnumerable<string> PackageVersions(string text)
    {
        foreach (Match element in PackageRegex().Matches(text))
            if (VersionAttributeRegex().Match(element.Value) is { Success: true } a && !a.Groups["version"].Value.StartsWith('$'))
                yield return a.Groups["version"].Value;
    }

    private static string Splice(Match match, Group group, string value) =>
        string.Concat(match.Value.AsSpan(0, group.Index - match.Index), value, match.Value.AsSpan(group.Index - match.Index + group.Length));

    private static bool Write(string file, string before, string after)
    {
        if (string.Equals(before, after, StringComparison.Ordinal))
            return false;
        File.WriteAllText(file, after, new UTF8Encoding(encoderShouldEmitUTF8Identifier: HasBom(file)));
        return true;
    }

    private static bool HasBom(string file)
    {
        Span<byte> head = stackalloc byte[3];
        using var stream = File.OpenRead(file);
        return stream.Read(head) == 3 && head is [0xEF, 0xBB, 0xBF];
    }

    private static IEnumerable<string> MsBuildFiles(string root)
    {
        var pending = new Stack<(string Directory, int Depth)>();
        pending.Push((root, 0));
        while (pending.Count > 0)
        {
            var (directory, depth) = pending.Pop();
            foreach (var file in Directory.EnumerateFiles(directory))
                if (Path.GetExtension(file) is ".csproj" or ".props" or ".targets")
                    yield return file;
            if (depth == MaxDepth)
                continue;
            foreach (var sub in Directory.EnumerateDirectories(directory))
            {
                var name = Path.GetFileName(sub);
                if (!ProjectFileSystem.HiddenFolderNames.Contains(name) && !name.StartsWith('.') &&
                    !string.Equals(name, ContentPaths.FolderName, StringComparison.OrdinalIgnoreCase))
                    pending.Push((sub, depth + 1));
            }
        }
    }

    [GeneratedRegex(@"<MainframeEngineVersion>\s*(?<version>[^<\s]+)\s*</MainframeEngineVersion>")]
    private static partial Regex PropertyRegex();

    // A MainframeEngine PackageReference/PackageVersion element, attributes in any order.
    [GeneratedRegex(@"<Package(?:Reference|Version)\b[^>]*\bInclude\s*=\s*""MainframeEngine""[^>]*>")]
    private static partial Regex PackageRegex();

    [GeneratedRegex(@"\bVersion\s*=\s*""(?<version>[^""]*)""")]
    private static partial Regex VersionAttributeRegex();

    [GeneratedRegex(@"MainframeEngine[\\/]MainframeEngine\.csproj")]
    private static partial Regex CheckoutRegex();

    [GeneratedRegex(@"""engineVersion""\s*:\s*""(?<version>[^""]*)""")]
    private static partial Regex MfprojVersionRegex();
}

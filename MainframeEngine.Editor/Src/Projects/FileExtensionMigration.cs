using System.Text;
using System.Text.RegularExpressions;

namespace MainframeEngine.Editor;

/// <summary>
/// ADR 0249: every engine file extension starts with <c>mf</c> (<see cref="FileExtensions"/>). Projects made before it
/// have <c>x.mscene</c>, <c>x.mres</c>, <c>x.png.meta</c> and the other <see cref="FileExtensions.Legacy"/> names.
/// Opening one in the editor offers this migration: the files under <c>Assets/</c> are renamed, and the scene, resource,
/// music, song and probe-data paths in the project's text files (scenes, resources, UI, project file, MSBuild files and
/// C#) are rewritten. References by UID survive as they are; sidecars are never named in content.
/// </summary>
public static partial class FileExtensionMigration
{
    // Text files that can name a scene, resource, music, song or probe-data file. Other files are only renamed.
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        FileExtensions.Scene, FileExtensions.Resource, FileExtensions.Music, FileExtensions.Song, ".rml", ".rcss", ".json",
        ".po", ".pot", FileExtensions.Project, ".cs", ".csproj", ".props", ".targets",
    };

    /// <summary>True when <paramref name="projectRoot"/>'s <c>Assets/</c> has a file with a legacy name.</summary>
    public static bool IsNeeded(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        return LegacyFiles(Path.Combine(Path.GetFullPath(projectRoot), ContentPaths.FolderName)).Any();
    }

    /// <summary>
    /// Renames the legacy files under <c>Assets/</c> (a file whose new name is taken is left as it is, with a warning)
    /// and rewrites the paths; returns the project-relative files renamed and rewritten. Build output and tool folders
    /// (<see cref="ProjectFileSystem.HiddenFolderNames"/>) are left alone.
    /// </summary>
    public static MigrationResult Run(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        var root = Path.GetFullPath(projectRoot);
        var renamed = new List<string>();
        var skipped = new List<string>();
        foreach (var (file, upgraded) in LegacyFiles(Path.Combine(root, ContentPaths.FolderName)).ToArray())
        {
            if (File.Exists(upgraded))
            {
                skipped.Add(Relative(root, file));
                continue;
            }

            File.Move(file, upgraded);
            renamed.Add(Relative(root, upgraded));
        }

        var rewritten = new List<string>();
        foreach (var file in TextFiles(root))
        {
            var text = File.ReadAllText(file, Encoding.UTF8);
            var updated = Rewrite(text);
            if (string.Equals(text, updated, StringComparison.Ordinal))
                continue;
            File.WriteAllText(file, updated, new UTF8Encoding(encoderShouldEmitUTF8Identifier: HasBom(file)));
            rewritten.Add(Relative(root, file));
        }

        renamed.Sort(StringComparer.Ordinal);
        skipped.Sort(StringComparer.Ordinal);
        rewritten.Sort(StringComparer.Ordinal);
        return new MigrationResult(renamed, skipped, rewritten);
    }

    /// <summary>What <see cref="Run"/> did, as project-relative paths.</summary>
    /// <param name="Renamed">The files' new names.</param>
    /// <param name="Skipped">Legacy files left alone because their new name was already taken.</param>
    /// <param name="Rewritten">Text files whose paths were updated.</param>
    public sealed record MigrationResult(IReadOnlyList<string> Renamed, IReadOnlyList<string> Skipped, IReadOnlyList<string> Rewritten);

    /// <summary>
    /// <paramref name="text"/> with every <c>.mscene</c>, <c>.mres</c>, <c>.mmusic</c>, <c>.msong</c> and <c>.probes</c>
    /// file name moved to its current extension (not <c>.mresults</c> or <c>x.probes2</c>).
    /// </summary>
    public static string Rewrite(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return ReferencedExtension().Replace(text, static m => m.Value switch
        {
            ".mscene" => FileExtensions.Scene,
            ".mres" => FileExtensions.Resource,
            ".mmusic" => FileExtensions.Music,
            ".msong" => FileExtensions.Song,
            _ => FileExtensions.LightProbes,
        });
    }

    /// <summary>The file's name after the migration, or null when it has no legacy extension.</summary>
    public static string? UpgradedName(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        foreach (var (legacy, current) in FileExtensions.Legacy)
        {
            if (!fileName.EndsWith(legacy, StringComparison.OrdinalIgnoreCase))
                continue;
            // A shader's layout and lock only next to the shader (not any settings.layout.json).
            if ((current == FileExtensions.ShaderLayout || current == FileExtensions.ShaderLock)
                && !fileName.EndsWith(".slang" + legacy, StringComparison.OrdinalIgnoreCase))
                return null;
            return string.Concat(fileName.AsSpan(0, fileName.Length - legacy.Length), current);
        }

        return null;
    }

    private static IEnumerable<(string File, string Upgraded)> LegacyFiles(string assets)
    {
        if (!Directory.Exists(assets))
            yield break;
        foreach (var file in Walk(assets, includeFile: static _ => true))
            if (UpgradedName(Path.GetFileName(file)) is { } name)
                yield return (file, Path.Combine(Path.GetDirectoryName(file)!, name));
    }

    private static IEnumerable<string> TextFiles(string root) =>
        Walk(root, static file => TextExtensions.Contains(Path.GetExtension(file)));

    private static IEnumerable<string> Walk(string root, Func<string, bool> includeFile)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var sub in Directory.EnumerateDirectories(directory))
                if (!ProjectFileSystem.HiddenFolderNames.Contains(Path.GetFileName(sub)) && !Path.GetFileName(sub).StartsWith('.'))
                    pending.Push(sub);
            foreach (var file in Directory.EnumerateFiles(directory))
                if (includeFile(file))
                    yield return file;
        }
    }

    private static string Relative(string root, string file) => Path.GetRelativePath(root, file).Replace('\\', '/');

    private static bool HasBom(string file)
    {
        Span<byte> head = stackalloc byte[3];
        using var stream = File.OpenRead(file);
        return stream.Read(head) == 3 && head is [0xEF, 0xBB, 0xBF];
    }

    // ".mscene", "x.mres\"", "x.probes" — not ".mresults" or "x.probes2".
    [GeneratedRegex(@"\.(mscene|mres|mmusic|msong|probes)(?![A-Za-z0-9_])")]
    private static partial Regex ReferencedExtension();
}

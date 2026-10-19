using System.Text;
using System.Text.RegularExpressions;

namespace MainframeEngine.Editor;

/// <summary>
/// ADR 0197: a game's content folder is <c>Assets/</c>; projects made before (project format 3 and older) have
/// <c>Content/</c>. Opening one in the editor offers this migration: the folder is renamed and every <c>Content/…</c>
/// path in the project's scenes, resources, UI, project file, MSBuild files and C# is rewritten to <c>Assets/…</c>.
/// <c>.meta</c> sidecars hold only UIDs, so references by UID survive as they are.
/// </summary>
public static partial class AssetsFolderMigration
{
    // Text files that hold content paths. Other files (images, audio, models) are only moved.
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mscene", ".mres", ".mmusic", ".msong", ".rml", ".rcss", ".json", ".po", ".pot", ".mfproj", ".cs",
        ".csproj", ".props", ".targets",
    };

    // MSBuild files also name the folder bare: LinkBase="Content", <LocaleContentRoot>../Content</…>, '…/../Content'.
    private static readonly HashSet<string> MsBuildExtensions = new(StringComparer.OrdinalIgnoreCase) { ".csproj", ".props", ".targets" };

    /// <summary>True when <paramref name="projectRoot"/> still has <c>Content/</c> and no <c>Assets/</c>.</summary>
    public static bool IsNeeded(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        return Directory.Exists(Path.Combine(projectRoot, ContentPaths.LegacyFolderName)) &&
               !Directory.Exists(Path.Combine(projectRoot, ContentPaths.FolderName));
    }

    /// <summary>
    /// Renames <c>Content/</c> to <c>Assets/</c> and rewrites the paths; returns the project-relative files rewritten.
    /// Build output and tool folders (<see cref="ProjectFileSystem.HiddenFolderNames"/>) are left alone.
    /// </summary>
    public static IReadOnlyList<string> Run(string projectRoot)
    {
        if (!IsNeeded(projectRoot))
            throw new InvalidOperationException($"'{projectRoot}' has no {ContentPaths.LegacyFolderName}/ folder to rename.");
        var root = Path.GetFullPath(projectRoot);
        Directory.Move(Path.Combine(root, ContentPaths.LegacyFolderName), Path.Combine(root, ContentPaths.FolderName));

        var rewritten = new List<string>();
        foreach (var file in TextFiles(root))
        {
            var text = File.ReadAllText(file, Encoding.UTF8);
            var updated = Rewrite(text, MsBuildExtensions.Contains(Path.GetExtension(file)));
            if (string.Equals(text, updated, StringComparison.Ordinal))
                continue;
            File.WriteAllText(file, updated, new UTF8Encoding(encoderShouldEmitUTF8Identifier: HasBom(file)));
            rewritten.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
        }

        rewritten.Sort(StringComparer.Ordinal);
        return rewritten;
    }

    /// <summary>
    /// <paramref name="text"/> with <c>Content/</c> and <c>Content\</c> path prefixes moved to <c>Assets</c>; in MSBuild
    /// files also the folder named on its own (<c>"Content"</c>, <c>../Content&lt;</c>, <c>/Content'</c>).
    /// </summary>
    public static string Rewrite(string text, bool msBuild)
    {
        ArgumentNullException.ThrowIfNull(text);
        text = PathPrefix().Replace(text, ContentPaths.FolderName);
        return msBuild ? BareFolder().Replace(text, ContentPaths.FolderName) : text;
    }

    private static IEnumerable<string> TextFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var sub in Directory.EnumerateDirectories(directory))
                if (!ProjectFileSystem.HiddenFolderNames.Contains(Path.GetFileName(sub)))
                    pending.Push(sub);
            foreach (var file in Directory.EnumerateFiles(directory))
                if (TextExtensions.Contains(Path.GetExtension(file)))
                    yield return file;
        }
    }

    private static bool HasBom(string file)
    {
        Span<byte> head = stackalloc byte[3];
        using var stream = File.OpenRead(file);
        return stream.Read(head) == 3 && head is [0xEF, 0xBB, 0xBF];
    }

    // "Content/…", "/Content/…", "Content\…" — not "MyContent/" or "ContentPacks/".
    [GeneratedRegex(@"(?<![A-Za-z0-9_.<\-])Content(?=[/\\])")]
    private static partial Regex PathPrefix();

    [GeneratedRegex(@"(?<![A-Za-z0-9_.<\-])Content(?="")|(?<=[/>])Content(?=[<'])")]
    private static partial Regex BareFolder();
}

namespace MainframeEngine.Export;

/// <summary>
/// The preset content globs: <c>*</c> (any characters within one folder), <c>**</c> (any depth, also none: <c>a/**/b</c>
/// matches <c>a/b</c>) and <c>?</c> (one character but '/'), matched case-sensitively against project-relative paths with
/// '/'. A small matcher, so the export needs no globbing package (<c>FileSystemName.MatchesSimpleExpression</c> has no
/// <c>**</c>).
/// </summary>
public static class Glob
{
    /// <summary>Whether <paramref name="path"/> (project-relative, '/') matches <paramref name="pattern"/>.</summary>
    public static bool IsMatch(string pattern, string path)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(path);
        var segments = pattern.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return MatchSegments(segments, 0, parts, 0);
    }

    /// <summary>Whether <paramref name="path"/> matches any of <paramref name="patterns"/>.</summary>
    public static bool IsMatchAny(IEnumerable<string> patterns, string path)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        foreach (var pattern in patterns)
            if (IsMatch(pattern, path))
                return true;
        return false;
    }

    /// <summary>A pattern's problem (empty, absolute, backslashes, <c>..</c>), or null when it is usable.</summary>
    public static string? Problem(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return "is empty";
        if (pattern.StartsWith('/') || Path.IsPathRooted(pattern))
            return "must be project-relative";
        if (pattern.Contains('\\', StringComparison.Ordinal))
            return "must use '/' as the separator";
        if (pattern.Split('/').Contains(".."))
            return "must not contain '..'";
        return null;
    }

    private static bool MatchSegments(string[] pattern, int p, string[] path, int i)
    {
        while (p < pattern.Length)
        {
            if (pattern[p] == "**")
            {
                // Collapse runs of ** and try every split point (including none).
                while (p < pattern.Length && pattern[p] == "**")
                    p++;
                if (p == pattern.Length)
                    return true;
                for (var k = i; k <= path.Length; k++)
                    if (MatchSegments(pattern, p, path, k))
                        return true;
                return false;
            }

            if (i >= path.Length || !MatchSegment(pattern[p], path[i]))
                return false;
            p++;
            i++;
        }

        return i == path.Length;
    }

    // One folder or file name against a pattern segment with * and ? (iterative, with backtracking on the last *).
    private static bool MatchSegment(string pattern, string text)
    {
        int p = 0, t = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || pattern[p] == text[t]))
            {
                p++;
                t++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = t;
            }
            else if (star >= 0)
            {
                p = star + 1;
                t = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
            p++;
        return p == pattern.Length;
    }
}

/// <summary>
/// Filters the staged <c>Assets/</c> folder with a preset's <c>include</c>/<c>exclude</c> globs. Only the game's own content
/// is filtered: files that also exist in the project's <c>Assets/</c>. Files the build added (the engine's shaders and UI,
/// compiled <c>.mo</c> catalogs) always ship, so a narrow <c>include</c> cannot break the engine.
/// </summary>
public static class ContentFilter
{
    /// <summary>Whether the game file at <paramref name="projectPath"/> (<c>Assets/…</c>) ships under these globs.</summary>
    public static bool Ships(string projectPath, IReadOnlyList<string> include, IReadOnlyList<string> exclude)
    {
        ArgumentNullException.ThrowIfNull(projectPath);
        ArgumentNullException.ThrowIfNull(include);
        ArgumentNullException.ThrowIfNull(exclude);
        return Glob.IsMatchAny(include, projectPath) && !Glob.IsMatchAny(exclude, projectPath);
    }

    /// <summary>
    /// Deletes the files of <paramref name="stageRoot"/>/Assets that are game files (present in
    /// <paramref name="projectRoot"/>/Assets) and do not ship, then empty folders. Returns the removed project paths, sorted.
    /// </summary>
    public static IReadOnlyList<string> Apply(string stageRoot, string projectRoot, IReadOnlyList<string> include, IReadOnlyList<string> exclude)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stageRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        var stageAssets = Path.Combine(stageRoot, ContentPaths.FolderName);
        var removed = new List<string>();
        if (!Directory.Exists(stageAssets))
            return removed;
        foreach (var file in Directory.EnumerateFiles(stageAssets, "*", SearchOption.AllDirectories).ToArray())
        {
            var relative = Path.GetRelativePath(stageRoot, file).Replace('\\', '/');
            var inProject = Path.Combine(projectRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(inProject) || Ships(relative, include, exclude))
                continue;
            File.Delete(file);
            removed.Add(relative);
        }

        DeleteEmptyFolders(stageAssets);
        removed.Sort(StringComparer.Ordinal);
        return removed;
    }

    /// <summary>The game's <c>Assets/</c> files (project paths) and whether each ships.</summary>
    public static IEnumerable<(string Path, bool Ships)> GameFiles(string projectRoot, IReadOnlyList<string> include, IReadOnlyList<string> exclude)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        var assets = Path.Combine(projectRoot, ContentPaths.FolderName);
        if (!Directory.Exists(assets))
            yield break;
        foreach (var file in Directory.EnumerateFiles(assets, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(projectRoot, file).Replace('\\', '/');
            yield return (relative, Ships(relative, include, exclude));
        }
    }

    private static void DeleteEmptyFolders(string root)
    {
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length).ToArray())
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
    }
}

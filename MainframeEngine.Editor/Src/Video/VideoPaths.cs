namespace MainframeEngine.Editor.Video;

/// <summary>
/// Whether two paths name the same file, for the checks that protect the recording (Export, Save As, Export Transcript):
/// writing over the source is the one unrecoverable mistake the video editor can make.
/// </summary>
/// <remarks>
/// Paths are resolved to their real location one component at a time, so a symbolic link anywhere in the path counts
/// (on macOS <c>/tmp</c> and <c>/var</c> are links), and a file that does not exist yet is judged by its deepest existing
/// ancestor. The comparison ignores case on Windows and macOS, whose default file systems do. Known limits: a hard link
/// is a second name for the same data and is not detected; a <c>..</c> after a link is collapsed textually, as
/// <see cref="Path.GetFullPath(string)"/> does; and a case-insensitive volume on Linux is compared case-sensitively.
/// </remarks>
public static class VideoPaths
{
    // A chain of links longer than this is a loop (the kernel stops at 40 as well).
    private const int MaxLinkDepth = 40;

    /// <summary>True when <paramref name="a"/> and <paramref name="b"/> are the same file once links are followed.</summary>
    public static bool SameFile(string a, string b) =>
        string.Equals(Canonical(a), Canonical(b),
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>The full path with every existing link in it replaced by its target; the path itself when it has none.</summary>
    public static string Canonical(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return Resolve(Path.GetFullPath(path), 0);
    }

    private static string Resolve(string full, int depth)
    {
        var root = Path.GetPathRoot(full) ?? "";
        var parts = full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var i = 0; i < parts.Length; i++)
        {
            current = Path.Combine(current, parts[i]);
            if (depth >= MaxLinkDepth || LinkTarget(current) is not { } target)
                continue;
            // The target may itself sit under a link (the target of /var is private/var, and /var/folders is below it).
            current = Resolve(target, depth + 1);
        }

        return current;
    }

    // The final target of the link at path (relative targets resolved against the link's folder), or null when path is not
    // a link, does not exist, or the link cannot be read.
    private static string? LinkTarget(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.LinkTarget is null ? null : info.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null; // an unreadable link is compared by its own path
        }
    }
}

namespace MainframeEngine.Editor;

/// <summary>A project's Project Manager icon: an absolute PNG path (or null) and whether the file changed since last asked.</summary>
public readonly record struct ProjectIcon(string? Path, bool Changed);

/// <summary>
/// Resolves <c>window.icon</c> of a project (its icon, as in Godot) to an existing PNG inside the project folder, caching
/// by the modification time and size of <c>project.mfproj</c> (two quick writes can share a timestamp) and the icon's
/// modification time. Never throws for a bad project.
/// </summary>
public sealed class ProjectIconResolver
{
    private readonly Dictionary<string, (FileStamp Project, string? Candidate, DateTime IconWrite)> _cache = new(StringComparer.Ordinal);

    private readonly record struct FileStamp(DateTime Write, long Length);

    public ProjectIcon Resolve(string projectDirectory)
    {
        var root = Path.GetFullPath(projectDirectory);
        var projectFile = GameProjectLayout.ProjectFileOf(root);
        var projectInfo = new FileInfo(projectFile);

        if (!projectInfo.Exists)
        {
            _cache.Remove(root);
            return new ProjectIcon(null, false);
        }

        var isFirstResolution = !_cache.ContainsKey(root);
        _cache.TryGetValue(root, out var cached);
        var projectStamp = new FileStamp(projectInfo.LastWriteTimeUtc, projectInfo.Length);
        var candidate = cached.Project == projectStamp ? cached.Candidate : ReadIcon(root, projectFile);
        var iconWrite = candidate is not null && File.Exists(candidate) ? File.GetLastWriteTimeUtc(candidate) : DateTime.MinValue;
        var icon = candidate is not null && iconWrite != DateTime.MinValue ? candidate : null;
        var previousIcon = cached.Candidate is not null && cached.IconWrite != DateTime.MinValue ? cached.Candidate : null;
        var changed = isFirstResolution || previousIcon != icon || (icon is not null && cached.IconWrite != iconWrite);

        _cache[root] = (projectStamp, candidate, iconWrite);
        return new ProjectIcon(icon, changed);
    }

    private static string? ReadIcon(string root, string projectFile)
    {
        try
        {
            if (ProjectSettings.Load(projectFile).Window.Icon is not { Length: > 0 } relative)
                return null;
            var full = Path.GetFullPath(Path.Combine(root, relative));
            var inside = full.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal);
            return inside && string.Equals(Path.GetExtension(full), ".png", StringComparison.OrdinalIgnoreCase) ? full : null;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}

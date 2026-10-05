namespace MainframeEngine.Editor;

/// <summary>A project's Project Manager icon: an absolute PNG path (or null) and whether the file changed since last asked.</summary>
public readonly record struct ProjectIcon(string? Path, bool Changed);

/// <summary>
/// Resolves <c>window.icon</c> of a project (its icon, as in Godot) to an existing PNG inside the project folder, caching
/// by the modification times of <c>project.mfproj</c> and the icon. Never throws for a bad project.
/// </summary>
public sealed class ProjectIconResolver
{
    private readonly Dictionary<string, (DateTime ProjectWrite, string? Candidate, DateTime IconWrite)> _cache = new(StringComparer.Ordinal);

    public ProjectIcon Resolve(string projectDirectory)
    {
        var root = Path.GetFullPath(projectDirectory);
        var projectFile = GameProjectLayout.ProjectFileOf(root);
        var projectWrite = File.Exists(projectFile) ? File.GetLastWriteTimeUtc(projectFile) : DateTime.MinValue;

        // If project.mfproj doesn't exist, return default immediately without calling ProjectSettings.Load
        if (projectWrite == DateTime.MinValue)
        {
            _cache.Remove(root);
            return new ProjectIcon(null, false);
        }

        var isFirstResolution = !_cache.ContainsKey(root);
        _cache.TryGetValue(root, out var cached);

        // Re-read candidate only if project.mfproj's mtime changed
        var candidate = cached.ProjectWrite == projectWrite ? cached.Candidate : ReadIcon(root, projectFile);

        // Always stat the candidate (check existence and get modification time)
        var iconWrite = candidate is not null && File.Exists(candidate) ? File.GetLastWriteTimeUtc(candidate) : DateTime.MinValue;

        // Path is the candidate if it exists (IconWrite != MinValue), else null
        var icon = candidate is not null && iconWrite != DateTime.MinValue ? candidate : null;

        // Previous icon for change detection: reconstruct from cache
        var previousIcon = cached.Candidate is not null && cached.IconWrite != DateTime.MinValue ? cached.Candidate : null;

        // Changed on first resolution, when icon path changed, or when (icon exists and mtime changed)
        var changed = isFirstResolution || previousIcon != icon || (icon is not null && cached.IconWrite != iconWrite);

        _cache[root] = (projectWrite, candidate, iconWrite);
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

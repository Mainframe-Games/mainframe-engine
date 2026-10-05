namespace MainframeEngine.Editor;

/// <summary>A project's Project Manager icon: an absolute PNG path (or null) and whether the file changed since last asked.</summary>
public readonly record struct ProjectIcon(string? Path, bool Changed);

/// <summary>
/// Resolves <c>window.icon</c> of a project (its icon, as in Godot) to an existing PNG inside the project folder, caching
/// by the modification times of <c>project.mfproj</c> and the icon. Never throws for a bad project.
/// </summary>
public sealed class ProjectIconResolver
{
    private readonly Dictionary<string, (DateTime ProjectWrite, string? Icon, DateTime IconWrite)> _cache = new(StringComparer.Ordinal);

    public ProjectIcon Resolve(string projectDirectory)
    {
        var root = Path.GetFullPath(projectDirectory);
        var projectFile = GameProjectLayout.ProjectFileOf(root);
        var projectWrite = File.Exists(projectFile) ? File.GetLastWriteTimeUtc(projectFile) : DateTime.MinValue;
        _cache.TryGetValue(root, out var cached);
        var icon = cached.ProjectWrite == projectWrite && cached.ProjectWrite != default ? cached.Icon : ReadIcon(root, projectFile);
        var iconWrite = icon is not null && File.Exists(icon) ? File.GetLastWriteTimeUtc(icon) : DateTime.MinValue;
        if (icon is not null && iconWrite == DateTime.MinValue)
            icon = null;
        var changed = icon is not null && (cached.Icon != icon || cached.IconWrite != iconWrite);
        _cache[root] = (projectWrite, icon, iconWrite);
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
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }
}

using MainframeEngine.UI.Rml;

namespace MainframeEngine;

/// <summary>
/// The UI file interface: <c>.rml</c>/<c>.rcss</c>/templates/fonts/images through <see cref="ContentPaths"/>, with
/// optional <b>source content directories</b> checked first. In development the Sandbox points one at its project's
/// <c>Content/</c> folder, so documents load straight from the files being edited and hot reload needs no rebuild.
/// </summary>
/// <remarks>
/// A path <c>Content/UI/hud.rml</c> is looked up as <c>&lt;source&gt;/UI/hud.rml</c> in each source directory (in
/// order), then in the application's <c>Content/</c>. Absolute paths are used as-is.
/// </remarks>
public sealed class UiFileInterface : RmlContentFileInterface
{
    private readonly List<string> _sourceDirectories = [];

    /// <summary>Directories standing in for <c>Content/</c>, checked before the application's copy.</summary>
    public IReadOnlyList<string> SourceDirectories => _sourceDirectories;

    public void AddSourceDirectory(string contentDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentDirectory);
        var full = Path.GetFullPath(contentDirectory);
        if (!_sourceDirectories.Contains(full, StringComparer.Ordinal))
            _sourceDirectories.Add(full);
    }

    public override string? ResolvePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        if (Path.IsPathRooted(path))
            return File.Exists(path) ? path : null;

        var relative = ContentRelative(path);
        foreach (var directory in _sourceDirectories)
        {
            var candidate = Path.GetFullPath(Path.Combine(directory, relative));
            if (File.Exists(candidate))
                return candidate;
        }

        return base.ResolvePath(path);
    }

    /// <summary>The directory for a content path (source directories first), or null.</summary>
    public string? ResolveDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (Path.IsPathRooted(path))
            return Directory.Exists(path) ? path : null;
        var relative = ContentRelative(path);
        foreach (var directory in _sourceDirectories)
        {
            var candidate = Path.GetFullPath(Path.Combine(directory, relative));
            if (Directory.Exists(candidate))
                return candidate;
        }

        var resolved = ContentPaths.Resolve(path);
        return Directory.Exists(resolved) ? resolved : null;
    }

    /// <summary><c>Content/UI/x.rml</c> → <c>UI/x.rml</c>; other relative paths are already content-relative.</summary>
    internal static string ContentRelative(string path)
    {
        var p = path.Replace('\\', '/');
        const string prefix = ContentPaths.FolderName + "/";
        return p.StartsWith(prefix, StringComparison.Ordinal) ? p[prefix.Length..] : p;
    }
}

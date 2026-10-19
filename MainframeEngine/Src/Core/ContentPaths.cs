namespace MainframeEngine;

/// <summary>
/// Resolves content paths against the application's output folder (<see cref="AppContext.BaseDirectory"/>),
/// never the process working directory, so the engine finds its shaders and assets however it was launched
/// (<c>dotnet run</c>, an IDE, a test runner, a published app). Every engine file load goes through
/// <see cref="Resolve"/>.
/// </summary>
/// <remarks>
/// Three forms are accepted, so engine-relative and game-relative paths share one API:
/// <list type="bullet">
/// <item>rooted paths are returned unchanged (normalised);</item>
/// <item><c>"Assets/…"</c> (the form games pass, e.g. <c>"Assets/Sky/sky.png"</c>) is relative to
/// <see cref="BaseDirectory"/>;</item>
/// <item>anything else is relative to <see cref="Root"/> (<c>"Shaders/Sky/Sky.vk.vert.spv"</c>).</item>
/// </list>
/// <c>"Content/…"</c>, the folder's name before project format 4 (ADR 0197), is read as <c>"Assets/…"</c>
/// (<see cref="UpgradeLegacyPath"/>), so paths in projects the editor has not migrated yet still load.
/// </remarks>
public static class ContentPaths
{
    /// <summary>Name of the content folder copied next to the application.</summary>
    public const string FolderName = "Assets";

    /// <summary>The content folder's name before project format 4; <see cref="UpgradeLegacyPath"/> maps it.</summary>
    public const string LegacyFolderName = "Content";

    /// <summary>The application's output folder (where the engine assembly and <c>Assets/</c> live).</summary>
    public static string BaseDirectory { get; } = AppContext.BaseDirectory;

    /// <summary>The <c>Assets/</c> folder next to the application.</summary>
    public static string Root { get; } = Path.Combine(BaseDirectory, FolderName);

    /// <summary>
    /// A game project folder (holding its own <c>Assets/</c>) that <c>"Assets/…"</c> paths resolve against first,
    /// falling back to <see cref="BaseDirectory"/> when the file or folder does not exist there. Tools set it to the
    /// open project (the editor edits a project's sources, not a build output); null (default) for games.
    /// </summary>
    public static string? ProjectDirectory
    {
        get => Volatile.Read(ref _projectDirectory);
        set => Volatile.Write(ref _projectDirectory, string.IsNullOrWhiteSpace(value) ? null : Path.GetFullPath(value));
    }

    private static string? _projectDirectory;

    /// <summary>Absolute path of a content file; see the type remarks for the accepted forms.</summary>
    public static string Resolve(string path)
    {
        path = UpgradeLegacyPath(path);
        if (ProjectDirectory is { } project && !string.IsNullOrWhiteSpace(path) && !Path.IsPathRooted(path) && IsContentRelativeToBase(path))
        {
            var inProject = Path.GetFullPath(Path.Combine(project, path));
            if (File.Exists(inProject) || Directory.Exists(inProject))
                return inProject;
        }

        return Resolve(path, BaseDirectory);
    }

    /// <summary><see cref="Resolve(string)"/> against an explicit base directory (tests, tools).</summary>
    public static string Resolve(string path, string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        path = UpgradeLegacyPath(path);
        if (Path.IsPathRooted(path))
            return Path.GetFullPath(path);

        var relative = IsContentRelativeToBase(path) ? path : Path.Combine(FolderName, path);
        return Path.GetFullPath(Path.Combine(baseDirectory, relative));
    }

    /// <summary><c>"Content"</c>, <c>"Content/x"</c> or <c>"Content\x"</c> → the same under <c>"Assets"</c>; any other
    /// path is returned unchanged.</summary>
    public static string UpgradeLegacyPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return StartsWithFolder(path, LegacyFolderName) ? FolderName + path[LegacyFolderName.Length..] : path;
    }

    private static bool IsContentRelativeToBase(string path) => StartsWithFolder(path, FolderName);

    // "Assets", "Assets/x" or "Assets\x" (the content folder name is case-sensitive on Linux, so match exactly).
    private static bool StartsWithFolder(string path, string folder) =>
        path.StartsWith(folder, StringComparison.Ordinal) &&
        (path.Length == folder.Length || path[folder.Length] is '/' or '\\');
}

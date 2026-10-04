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
/// <item><c>"Content/…"</c> (the form games and the Sandbox pass, e.g. <c>"Content/Sky/sky.png"</c>) is relative to
/// <see cref="BaseDirectory"/>;</item>
/// <item>anything else is relative to <see cref="Root"/> (<c>"Shaders/Sky/Sky.vk.vert.spv"</c>).</item>
/// </list>
/// </remarks>
public static class ContentPaths
{
    /// <summary>Name of the content folder copied next to the application.</summary>
    public const string FolderName = "Content";

    /// <summary>The application's output folder (where the engine assembly and <c>Content/</c> live).</summary>
    public static string BaseDirectory { get; } = AppContext.BaseDirectory;

    /// <summary>The <c>Content/</c> folder next to the application.</summary>
    public static string Root { get; } = Path.Combine(BaseDirectory, FolderName);

    /// <summary>Absolute path of a content file; see the type remarks for the accepted forms.</summary>
    public static string Resolve(string path) => Resolve(path, BaseDirectory);

    /// <summary><see cref="Resolve(string)"/> against an explicit base directory (tests, tools).</summary>
    public static string Resolve(string path, string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        if (Path.IsPathRooted(path))
            return Path.GetFullPath(path);

        var relative = IsContentRelativeToBase(path) ? path : Path.Combine(FolderName, path);
        return Path.GetFullPath(Path.Combine(baseDirectory, relative));
    }

    // "Content", "Content/x" or "Content\x" (the content folder name is case-sensitive on Linux, so match exactly).
    private static bool IsContentRelativeToBase(string path) =>
        path.StartsWith(FolderName, StringComparison.Ordinal) &&
        (path.Length == FolderName.Length || path[FolderName.Length] is '/' or '\\');
}

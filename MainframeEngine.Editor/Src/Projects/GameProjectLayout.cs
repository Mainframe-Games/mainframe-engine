namespace MainframeEngine.Editor;

/// <summary>
/// Where things are in a game project made from the <c>mfgame</c> template (docs/design/project-and-gamehost.md):
/// <c>project.mfproj</c> and the solution at the root, the game library at <c>Name/Name.csproj</c> (the assembly the
/// editor loads) and the desktop head at <c>Name.Desktop/Name.Desktop.csproj</c> (the executable Play runs; projects made
/// before the rename have <c>Name.Launcher</c>, still found). Hand-renamed projects are found by looking one folder level
/// down.
/// </summary>
/// <remarks>
/// The project paths returned are real paths (<see cref="RealPath"/>): MSBuild fails to find the engine's project
/// references when a game is built through a symlinked path (macOS <c>/tmp</c> and <c>/var/folders</c> are symlinks to
/// <c>/private/…</c>), so whatever builds a game must use these.
/// </remarks>
public static class GameProjectLayout
{
    /// <summary>The desktop head's project suffix (<c>MyGame.Desktop.csproj</c>).</summary>
    public const string DesktopSuffix = ".Desktop.csproj";

    /// <summary>The desktop head's suffix in projects made before it was renamed (<c>MyGame.Launcher.csproj</c>).</summary>
    public const string LegacyDesktopSuffix = ".Launcher.csproj";

    /// <summary>The project file of <paramref name="projectDirectory"/>: <c>&lt;dir&gt;/project.mfproj</c>.</summary>
    public static string ProjectFileOf(string projectDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        return Path.Combine(projectDirectory, ProjectSettings.FileName);
    }

    /// <summary>
    /// <paramref name="path"/> made absolute with every symbolic link (or junction) in it resolved, as far as it
    /// exists; the missing rest is appended unchanged. Never throws for I/O problems (returns the full path then).
    /// </summary>
    public static string RealPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Resolve(Path.GetFullPath(path), depth: 0);
    }

    private static string Resolve(string full, int depth)
    {
        full = Path.TrimEndingDirectorySeparator(full);
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root) || depth > 32)
            return full;
        try
        {
            var current = root;
            var parts = full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < parts.Length; i++)
            {
                var next = Path.Combine(current, parts[i]);
                FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
                if (!info.Exists)
                    return Path.Combine([next, .. parts[(i + 1)..]]);
                if (info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                    next = Resolve(Path.GetFullPath(target.FullName), depth + 1); // the target may hold links too
                current = next;
            }

            return current;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return full;
        }
    }

    /// <summary>
    /// The solution to build: the <c>*.slnx</c> (else <c>*.sln</c>) in <paramref name="projectDirectory"/>, preferring
    /// one named after the folder when there are several. Null when there is none.
    /// </summary>
    public static string? SolutionOf(string projectDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        if (!Directory.Exists(projectDirectory))
            return null;
        var folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectDirectory)));
        projectDirectory = RealPath(projectDirectory);
        foreach (var pattern in (string[])["*.slnx", "*.sln"])
        {
            var found = Directory.GetFiles(projectDirectory, pattern).Where(f => Path.GetExtension(f).Length == pattern.Length - 1).Order(StringComparer.Ordinal).ToArray();
            if (found.Length == 0)
                continue;
            return found.FirstOrDefault(f => string.Equals(Path.GetFileNameWithoutExtension(f), folderName, StringComparison.OrdinalIgnoreCase)) ?? found[0];
        }

        return null;
    }

    /// <summary>
    /// The game library's <c>.csproj</c> (built and loaded by the editor): <c>&lt;dir&gt;/&lt;A&gt;/&lt;A&gt;.csproj</c>
    /// for the first of <see cref="ProjectSettings.Assemblies"/>, else the only project one level down that is not a desktop head.
    /// Null when there is none or it is ambiguous.
    /// </summary>
    public static string? GameLibraryProjectOf(string projectDirectory, ProjectSettings settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        ArgumentNullException.ThrowIfNull(settings);
        if (!Directory.Exists(projectDirectory))
            return null;
        projectDirectory = RealPath(projectDirectory);
        if (settings.Assemblies.Count > 0 && !string.IsNullOrWhiteSpace(settings.Assemblies[0]))
        {
            var name = settings.Assemblies[0];
            var conventional = Path.Combine(projectDirectory, name, name + ".csproj");
            if (File.Exists(conventional))
                return conventional;
        }

        var libraries = ProjectsOneLevelDown(projectDirectory).Where(p => !IsDesktop(p)).ToArray();
        return libraries.Length == 1 ? libraries[0] : null;
    }

    /// <summary>
    /// The desktop head (<c>*/*.Desktop.csproj</c>, else a legacy <c>*/*.Launcher.csproj</c>) that Play builds and runs;
    /// the one named after the folder when there are several. Null when there is none (or several, none named so).
    /// </summary>
    public static string? DesktopProjectOf(string projectDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        if (!Directory.Exists(projectDirectory))
            return null;
        var folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectDirectory)));
        projectDirectory = RealPath(projectDirectory);
        var projects = ProjectsOneLevelDown(projectDirectory);
        foreach (var suffix in (string[])[DesktopSuffix, LegacyDesktopSuffix])
        {
            var heads = projects.Where(p => p.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (heads.Length == 1)
                return heads[0];
            if (heads.Length > 1)
                return heads.FirstOrDefault(p => string.Equals(Path.GetFileName(p), folderName + suffix, StringComparison.OrdinalIgnoreCase));
        }

        return null;
    }

    private static bool IsDesktop(string project) =>
        project.EndsWith(DesktopSuffix, StringComparison.OrdinalIgnoreCase) || project.EndsWith(LegacyDesktopSuffix, StringComparison.OrdinalIgnoreCase);

    private static List<string> ProjectsOneLevelDown(string projectDirectory)
    {
        var projects = new List<string>();
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(projectDirectory))
            {
                var name = Path.GetFileName(directory);
                if (name.StartsWith('.') || name is "bin" or "obj")
                    continue;
                projects.AddRange(Directory.EnumerateFiles(directory, "*.csproj").Where(f => f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"[Editor] Could not list the projects in '{projectDirectory}': {e.Message}");
        }

        projects.Sort(StringComparer.Ordinal);
        return projects;
    }
}

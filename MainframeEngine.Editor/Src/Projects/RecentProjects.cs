using System.Text.Json;
using System.Text.Json.Serialization;

namespace MainframeEngine.Editor;

/// <summary>One entry of the Project Manager's recent list.</summary>
/// <param name="Path">The absolute project folder (the one holding <c>project.mfproj</c>).</param>
/// <param name="Name">The project's display name when it was last opened (shown even if the folder is gone).</param>
/// <param name="LastOpenedUtc">When the project was last opened or created.</param>
public sealed record RecentProject(string Path, string Name, DateTime LastOpenedUtc);

/// <summary>
/// The Project Manager's recently opened projects, newest first, persisted as JSON in
/// <c>~/.mainframe/recent_projects.json</c> (written atomically, like the editor layout). A missing, corrupt or
/// newer-format file gives an empty list and a warning, never an exception: losing the recent list must not stop the
/// editor from starting. Entries whose folder has gone stay listed (the UI greys them out via <see cref="IsValid"/>)
/// until the user removes them, since the folder may be on a drive that is not mounted right now.
/// </summary>
public sealed class RecentProjects
{
    /// <summary>The most entries kept; older ones fall off the end.</summary>
    public const int MaxItems = 20;

    /// <summary>The file format written by this editor (a newer file is not overwritten).</summary>
    public const int CurrentFormat = 1;

    private readonly List<RecentProject> _items = [];
    private bool _readOnly;

    /// <summary>Creates an empty list saved to <paramref name="path"/> (null keeps it in memory only).</summary>
    public RecentProjects(string? path)
    {
        FilePath = path is null ? null : System.IO.Path.GetFullPath(path);
    }

    /// <summary>The default file: <c>~/.mainframe/recent_projects.json</c> (user profile on every OS).</summary>
    public static string DefaultPath =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mainframe", "recent_projects.json");

    /// <summary>Where the list is saved, or null for an in-memory list.</summary>
    public string? FilePath { get; }

    /// <summary>The entries, newest first (at most <see cref="MaxItems"/>).</summary>
    public IReadOnlyList<RecentProject> Items => _items;

    // Project folders are compared like the file system does: case-insensitively on Windows only (macOS volumes are
    // usually case-insensitive too, but a case-sensitive one would make two real folders look like one).
    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// Reads the list from <paramref name="path"/> (null = an empty in-memory list). Never throws: a missing file is an
    /// empty list; an unreadable, corrupt or newer-format file is an empty list plus a <see cref="Log.Warning"/> (a
    /// newer-format file is also never overwritten by this editor).
    /// </summary>
    public static RecentProjects Load(string? path)
    {
        var recent = new RecentProjects(path);
        if (recent.FilePath is not { } file)
            return recent;
        try
        {
            if (!File.Exists(file))
                return recent;
            var data = JsonSerializer.Deserialize(File.ReadAllBytes(file), ProjectsJsonContext.Default.RecentProjectsFile);
            if (data is null)
                return recent;
            if (data.Format > CurrentFormat)
            {
                Log.Warning($"[Editor] The recent projects file '{file}' is from a newer editor (format {data.Format}); it is not used or changed.");
                recent._readOnly = true;
                return recent;
            }

            foreach (var item in (data.Projects ?? []).OrderByDescending(p => p?.LastOpenedUtc ?? DateTime.MinValue))
            {
                if (item is null || string.IsNullOrWhiteSpace(item.Path) || !System.IO.Path.IsPathFullyQualified(item.Path))
                    continue;
                var entry = item with
                {
                    Path = Normalize(item.Path),
                    Name = string.IsNullOrWhiteSpace(item.Name) ? System.IO.Path.GetFileName(Normalize(item.Path)) : item.Name,
                    LastOpenedUtc = DateTime.SpecifyKind(item.LastOpenedUtc, DateTimeKind.Utc),
                };
                if (recent.IndexOf(entry.Path) < 0 && recent._items.Count < MaxItems)
                    recent._items.Add(entry);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or ArgumentException)
        {
            Log.Warning($"[Editor] Ignoring the recent projects file '{file}': {e.Message}");
            recent._items.Clear();
        }

        return recent;
    }

    /// <summary>
    /// Records that the project in <paramref name="projectDirectory"/> was opened (or created) now: adds it, or moves
    /// it to the top with the new <paramref name="name"/>, trims the list to <see cref="MaxItems"/> and saves.
    /// </summary>
    public void Touch(string projectDirectory, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var path = Normalize(projectDirectory);
        var index = IndexOf(path);
        if (index >= 0)
            _items.RemoveAt(index);
        // Never older than the current top entry, so the order survives a clock step backwards.
        var now = DateTime.UtcNow;
        if (_items.Count > 0 && _items[0].LastOpenedUtc > now)
            now = _items[0].LastOpenedUtc;
        _items.Insert(0, new RecentProject(path, name, now));
        if (_items.Count > MaxItems)
            _items.RemoveRange(MaxItems, _items.Count - MaxItems);
        Save();
    }

    /// <summary>
    /// Adds <paramref name="project"/> as given — with its own <see cref="RecentProject.LastOpenedUtc"/>, in date order
    /// (replacing an entry for the same folder) — and saves. For importing lists and QA; opening a project uses
    /// <see cref="Touch"/>.
    /// </summary>
    public void Add(RecentProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(project.Path);
        ArgumentException.ThrowIfNullOrWhiteSpace(project.Name);
        var entry = project with { Path = Normalize(project.Path), LastOpenedUtc = DateTime.SpecifyKind(project.LastOpenedUtc, DateTimeKind.Utc) };
        var index = IndexOf(entry.Path);
        if (index >= 0)
            _items.RemoveAt(index);
        var at = _items.FindIndex(p => p.LastOpenedUtc < entry.LastOpenedUtc);
        _items.Insert(at < 0 ? _items.Count : at, entry);
        if (_items.Count > MaxItems)
            _items.RemoveRange(MaxItems, _items.Count - MaxItems);
        Save();
    }

    /// <summary>Removes the project in <paramref name="projectDirectory"/> and saves; false when it was not listed.</summary>
    public bool Remove(string projectDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        var index = IndexOf(Normalize(projectDirectory));
        if (index < 0)
            return false;
        _items.RemoveAt(index);
        Save();
        return true;
    }

    /// <summary>True when the entry's folder still holds a <c>project.mfproj</c> (it can be opened).</summary>
    public static bool IsValid(RecentProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        try
        {
            return File.Exists(GameProjectLayout.ProjectFileOf(project.Path));
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Writes the list to <see cref="FilePath"/> atomically (temp file + rename). Does nothing for an in-memory list or
    /// a newer-format file; a failed write is logged, not thrown (the list stays in memory).
    /// </summary>
    public void Save()
    {
        if (FilePath is not { } file || _readOnly)
            return;
        try
        {
            var data = new RecentProjectsFile(CurrentFormat, [.. _items]);
            AtomicFile.WriteAllBytes(file, JsonSerializer.SerializeToUtf8Bytes(data, ProjectsJsonContext.Default.RecentProjectsFile));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"[Editor] Could not save the recent projects to '{file}': {e.Message}");
        }
    }

    private int IndexOf(string normalizedPath)
    {
        for (var i = 0; i < _items.Count; i++)
            if (string.Equals(_items[i].Path, normalizedPath, PathComparison))
                return i;
        return -1;
    }

    private static string Normalize(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        var trimmed = System.IO.Path.TrimEndingDirectorySeparator(full);
        return trimmed.Length == 0 ? full : trimmed;
    }
}

/// <summary>The JSON shape of <c>recent_projects.json</c>.</summary>
internal sealed record RecentProjectsFile(int Format, List<RecentProject>? Projects);

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)]
[JsonSerializable(typeof(RecentProjectsFile))]
internal sealed partial class ProjectsJsonContext : JsonSerializerContext;

using System.Text.Json;
using MainframeEngine.Serialization;

namespace MainframeEngine.Editor;

/// <summary>
/// The outcome of a <see cref="FileOperations"/> call. <paramref name="Path"/> is the resulting absolute path (the new
/// location after a rename/move); <paramref name="UpdatedFiles"/> are the files whose references were rewritten;
/// <paramref name="TrashUnavailable"/> means a delete failed because the trash could not take the item, so the UI may
/// offer <see cref="FileOperations.DeletePermanently"/> after asking.
/// </summary>
public sealed record FileOperationResult(bool Succeeded, string? Path, string? Error, IReadOnlyList<string> UpdatedFiles, bool TrashUnavailable = false)
{
    internal static FileOperationResult Ok(string path, IReadOnlyList<string>? updatedFiles = null, string? warning = null) =>
        new(true, path, warning, updatedFiles ?? []);

    internal static FileOperationResult Fail(string error, string? path = null, bool trashUnavailable = false) =>
        new(false, path, error, [], trashUnavailable);
}

/// <summary>
/// File operations of the FileSystem panel: create folders, scenes and resources; rename and move with reference
/// fixups (<see cref="ReferenceFixer"/>; UIDs and <c>.meta</c> sidecars are kept); delete to the trash. Expected
/// failures (bad names, conflicts, I/O errors) come back as <see cref="FileOperationResult"/>s naming the file, never as
/// exceptions. Paths may be absolute or project-relative. Main thread.
/// </summary>
public sealed class FileOperations
{
    private static readonly char[] WindowsInvalidChars = ['<', '>', ':', '"', '|', '?', '*'];

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private readonly AssetDatabase _database;
    private readonly ITrash _trash;

    public FileOperations(string projectRoot, AssetDatabase database, ITrash trash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(trash);
        ProjectRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
        _database = database;
        _trash = trash;
    }

    /// <summary>The project folder (absolute).</summary>
    public string ProjectRoot { get; }

    /// <summary>
    /// Null when <paramref name="name"/> is a valid file or folder name on every desktop OS, else why not: empty, a path
    /// separator, <c>..</c>, an invalid character, a reserved Windows name, a trailing dot or space, a <c>.meta</c> name.
    /// </summary>
    public static string? ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "The name cannot be empty.";
        if (name.Contains("..", StringComparison.Ordinal) || name == ".")
            return $"'{name}' is not a valid name ('.' and '..' refer to folders).";
        if (name.Contains('/', StringComparison.Ordinal) || name.Contains('\\', StringComparison.Ordinal))
            return $"'{name}' cannot contain '/' or '\\'.";
        foreach (var c in name)
            if (char.IsControl(c) || Array.IndexOf(WindowsInvalidChars, c) >= 0 || Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0)
                return $"'{name}' contains the invalid character '{(char.IsControl(c) ? $"U+{(int)c:X4}" : c.ToString())}'.";
        if (name != name.Trim())
            return $"'{name}' cannot start or end with a space.";
        if (name.EndsWith('.'))
            return $"'{name}' cannot end with a dot.";
        if (name.Length > 255)
            return "The name is too long (255 characters at most).";
        if (ReservedNames.Contains(name.Split('.')[0]))
            return $"'{name}' is a reserved name on Windows.";
        if (name.EndsWith(AssetDatabase.MetaExtension, StringComparison.OrdinalIgnoreCase))
            return $"'{name}': .meta files are managed by the editor.";
        return null;
    }

    /// <summary>Creates the folder <paramref name="name"/> in <paramref name="parentDirectory"/>.</summary>
    public FileOperationResult CreateFolder(string parentDirectory, string name)
    {
        ArgumentNullException.ThrowIfNull(parentDirectory);
        ArgumentNullException.ThrowIfNull(name);
        if (!TryNewTarget(parentDirectory, name, out var target, out var error))
            return FileOperationResult.Fail(error);
        try
        {
            Directory.CreateDirectory(target);
            return FileOperationResult.Ok(target);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return FileOperationResult.Fail($"Could not create the folder '{name}': {e.Message}", target);
        }
    }

    /// <summary>
    /// Creates a scene file (<c>.mscene</c> appended when missing) whose root is a <paramref name="rootType"/> named after
    /// the file.
    /// </summary>
    public FileOperationResult CreateScene(string parentDirectory, string name, string rootType = "Node3D")
    {
        ArgumentNullException.ThrowIfNull(parentDirectory);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootType);
        name = WithExtension(name, SceneFormat.SceneExtension);
        if (!TryNewTarget(parentDirectory, name, out var target, out var error))
            return FileOperationResult.Fail(error);
        if (TypeRegistry.Get(rootType) is not { IsNode: true, IsAbstract: false })
            return FileOperationResult.Fail($"Cannot create '{name}': '{rootType}' is not a node type.");

        Node? root = null;
        try
        {
            root = TypeRegistry.CreateNode(rootType);
            root.Name = Path.GetFileNameWithoutExtension(name);
            var node = root;
            WithDatabase(() => SceneSaver.Save(node, target));
            return FileOperationResult.Ok(target);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
        {
            return FileOperationResult.Fail($"Could not create the scene '{name}': {e.Message}", target);
        }
        finally
        {
            root?.Free();
        }
    }

    /// <summary>Creates a resource file (<c>.mres</c> appended when missing) holding a new <paramref name="resourceType"/>.</summary>
    public FileOperationResult CreateResource(string parentDirectory, string name, Type resourceType)
    {
        ArgumentNullException.ThrowIfNull(parentDirectory);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(resourceType);
        name = WithExtension(name, SceneFormat.ResourceExtension);
        if (!TryNewTarget(parentDirectory, name, out var target, out var error))
            return FileOperationResult.Fail(error);
        if (!typeof(Resource).IsAssignableFrom(resourceType) || resourceType.IsAbstract || resourceType == typeof(PackedScene))
            return FileOperationResult.Fail($"Cannot create '{name}': {resourceType.Name} is not a resource type that can be saved.");

        try
        {
            var resource = (TypeRegistry.Get(resourceType)?.CreateInstance() ?? Activator.CreateInstance(resourceType)) as Resource
                           ?? throw new InvalidOperationException($"{resourceType.Name} could not be created.");
            WithDatabase(() => ResourceSaver.Save(resource, target));
            return FileOperationResult.Ok(target);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException
                                      or MissingMethodException or System.Reflection.TargetInvocationException)
        {
            return FileOperationResult.Fail($"Could not create the resource '{name}': {e.Message}", target);
        }
    }

    /// <summary>Renames a file or folder in place (its <c>.meta</c> and UID follow; references are fixed).</summary>
    public FileOperationResult Rename(string path, string newName)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(newName);
        if (!TryExisting(path, out var full, out var error))
            return FileOperationResult.Fail(error);
        if (ValidateName(newName) is { } invalid)
            return FileOperationResult.Fail(invalid, full);

        var parent = Path.GetDirectoryName(full)!;
        var target = Path.Combine(parent, newName);
        if (string.Equals(target, full, StringComparison.Ordinal))
            return FileOperationResult.Ok(full);
        var caseOnly = string.Equals(target, full, StringComparison.OrdinalIgnoreCase);
        if (!caseOnly && Exists(target))
            return FileOperationResult.Fail($"'{newName}' already exists in '{Display(parent)}'.", full);
        return MoveCore(full, target, caseOnly);
    }

    /// <summary>Moves a file or folder into <paramref name="destinationDirectory"/> (references are fixed).</summary>
    public FileOperationResult Move(string path, string destinationDirectory)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(destinationDirectory);
        if (!TryExisting(path, out var full, out var error) || !TryFolder(destinationDirectory, out var destination, out error))
            return FileOperationResult.Fail(error);

        var name = Path.GetFileName(full);
        if (string.Equals(destination, Path.GetDirectoryName(full), StringComparison.Ordinal))
            return FileOperationResult.Ok(full);
        if (Directory.Exists(full) && IsSameOrInside(destination, full))
            return FileOperationResult.Fail($"Cannot move the folder '{name}' into itself.", full);
        var target = Path.Combine(destination, name);
        if (Exists(target))
            return FileOperationResult.Fail($"'{name}' already exists in '{Display(destination)}'.", full);
        return MoveCore(full, target, caseOnly: false);
    }

    /// <summary>
    /// Moves a file or folder (and a file's <c>.meta</c>) to the trash and forgets its UIDs. When the trash cannot take
    /// it, nothing is deleted and the result has <see cref="FileOperationResult.TrashUnavailable"/>.
    /// </summary>
    public FileOperationResult Delete(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!TryExisting(path, out var full, out var error))
            return FileOperationResult.Fail(error);

        var uids = UidsUnder(full);
        if (!_trash.TryMoveToTrash(full, out var trashError))
            return FileOperationResult.Fail(trashError ?? $"Could not move '{Path.GetFileName(full)}' to the trash.", full, trashUnavailable: true);

        string? warning = null;
        var meta = full + AssetDatabase.MetaExtension;
        if (File.Exists(meta) && !_trash.TryMoveToTrash(meta, out var metaError))
            warning = $"'{Path.GetFileName(full)}' is in the trash, but its .meta file is not: {metaError}";
        foreach (var uid in uids)
            _database.Unregister(uid);
        return FileOperationResult.Ok(full, warning: warning);
    }

    /// <summary>Deletes a file or folder (and a file's <c>.meta</c>) permanently. Only after the user confirmed it.</summary>
    public FileOperationResult DeletePermanently(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!TryExisting(path, out var full, out var error))
            return FileOperationResult.Fail(error);

        var uids = UidsUnder(full);
        try
        {
            if (Directory.Exists(full))
            {
                Directory.Delete(full, recursive: true);
            }
            else
            {
                File.Delete(full);
                File.Delete(full + AssetDatabase.MetaExtension);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return FileOperationResult.Fail($"Could not delete '{Path.GetFileName(full)}': {e.Message}", full);
        }

        foreach (var uid in uids)
            _database.Unregister(uid);
        return FileOperationResult.Ok(full);
    }

    // ── Moving ───────────────────────────────────────────────────────────────

    private FileOperationResult MoveCore(string from, string to, bool caseOnly)
    {
        var name = Path.GetFileName(from);
        var isDirectory = Directory.Exists(from);

        // 1. The mapping and the UIDs, before anything moves. 2. The move.
        var moved = new Dictionary<string, string>(StringComparer.Ordinal) { [ProjectPath(from)] = ProjectPath(to) };
        try
        {
            if (isDirectory)
            {
                var uids = new List<(string Uid, string NewPath)>();
                foreach (var entry in Directory.EnumerateFileSystemEntries(from, "*", SearchOption.AllDirectories))
                {
                    if (entry.EndsWith(AssetDatabase.MetaExtension, StringComparison.OrdinalIgnoreCase))
                        continue;
                    var newPath = ProjectPath(Path.Combine(to, Path.GetRelativePath(from, entry)));
                    moved[ProjectPath(entry)] = newPath;
                    if (_database.GetUid(entry) is { } uid)
                        uids.Add((uid, newPath));
                }

                MoveDirectory(from, to, caseOnly);
                foreach (var (uid, newPath) in uids)
                    _database.Register(uid, newPath);
            }
            else if (caseOnly)
            {
                var temp = to + ".renaming";
                _database.Move(from, temp);
                _database.Move(temp, to);
            }
            else
            {
                _database.Move(from, to);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return FileOperationResult.Fail($"Could not move '{name}': {e.Message}", from);
        }

        // 3. The references.
        var updated = ReferenceFixer.Apply(ProjectRoot, _database, moved);
        return FileOperationResult.Ok(to, updated);
    }

    private static void MoveDirectory(string from, string to, bool caseOnly)
    {
        if (!caseOnly)
        {
            Directory.Move(from, to);
            return;
        }

        var temp = to + ".renaming";
        Directory.Move(from, temp);
        Directory.Move(temp, to);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private List<string> UidsUnder(string full)
    {
        var uids = new List<string>();
        if (Directory.Exists(full))
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            foreach (var file in Directory.EnumerateFiles(full, "*", options))
                if (_database.GetUid(file) is { } uid)
                    uids.Add(uid);
        }
        else if (_database.GetUid(full) is { } uid)
        {
            uids.Add(uid);
        }
        else if (EmbeddedUid(full) is { } embedded && _database.GetPath(embedded) == ProjectPath(full))
        {
            uids.Add(embedded);
        }

        return uids;
    }

    private static string? EmbeddedUid(string full)
    {
        if (!full.EndsWith(SceneFormat.SceneExtension, StringComparison.OrdinalIgnoreCase)
            && !full.EndsWith(SceneFormat.ResourceExtension, StringComparison.OrdinalIgnoreCase))
            return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(full), new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("uid", out var uid) && uid.ValueKind == JsonValueKind.String
                ? uid.GetString()
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private bool TryNewTarget(string parentDirectory, string name, out string target, out string error)
    {
        target = "";
        if (!TryFolder(parentDirectory, out var parent, out error))
            return false;
        if (ValidateName(name) is { } invalid)
        {
            error = invalid;
            return false;
        }

        target = Path.Combine(parent, name);
        if (Exists(target))
        {
            error = $"'{name}' already exists in '{Display(parent)}'.";
            return false;
        }

        return true;
    }

    private bool TryFolder(string path, out string full, out string error)
    {
        full = Resolve(path);
        if (Directory.Exists(full) && IsSameOrInside(full, ProjectRoot))
        {
            error = "";
            return true;
        }

        error = $"'{Display(full)}' is not a folder in the project.";
        return false;
    }

    private bool TryExisting(string path, out string full, out string error)
    {
        full = Resolve(path);
        error = "";
        if (string.Equals(full, ProjectRoot, StringComparison.Ordinal))
            error = "The project folder itself cannot be changed here.";
        else if (!IsSameOrInside(full, ProjectRoot))
            error = $"'{full}' is not in the project.";
        else if (!Exists(full))
            error = $"'{Display(full)}' no longer exists.";
        return error.Length == 0;
    }

    private string Resolve(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path, ProjectRoot));

    private string ProjectPath(string full) => Path.GetRelativePath(ProjectRoot, full).Replace('\\', '/');

    /// <summary>A project-relative path for messages (the project folder's name for the root).</summary>
    private string Display(string full) =>
        string.Equals(full, ProjectRoot, StringComparison.Ordinal) ? Path.GetFileName(ProjectRoot) : ProjectPath(full);

    private static bool IsSameOrInside(string path, string folder) =>
        string.Equals(path, folder, StringComparison.Ordinal)
        || path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.Ordinal)
        || path.StartsWith(folder + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);

    private static bool Exists(string full) => File.Exists(full) || Directory.Exists(full);

    private static string WithExtension(string name, string extension) =>
        name.EndsWith(extension, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(name) ? name : name + extension;

    /// <summary>Scene and resource savers use <see cref="AssetDatabase.Current"/>; point it at this project for the call.</summary>
    private void WithDatabase(Action action)
    {
        var previous = AssetDatabase.Current;
        AssetDatabase.Current = _database;
        try
        {
            action();
        }
        finally
        {
            AssetDatabase.Current = previous;
        }
    }
}

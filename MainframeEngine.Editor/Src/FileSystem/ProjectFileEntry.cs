namespace MainframeEngine.Editor;

/// <summary>
/// A file or folder in the FileSystem panel (<see cref="ProjectFileSystem"/>). Entries are kept across
/// <see cref="ProjectFileSystem.Refresh"/>es (same object per path), so the panel may hold on to them; their values
/// change in place. (Named <c>ProjectFileEntry</c> because the file picker already has a <c>FileEntry</c> record.)
/// </summary>
public sealed class ProjectFileEntry
{
    internal static readonly IReadOnlyList<ProjectFileEntry> NoChildren = [];

    internal ProjectFileEntry(string fullPath, string projectPath, bool isDirectory)
    {
        FullPath = fullPath;
        ProjectPath = projectPath;
        IsDirectory = isDirectory;
        Name = Path.GetFileName(fullPath);
        Extension = isDirectory ? "" : Path.GetExtension(fullPath);
        Kind = FileKinds.Of(fullPath, isDirectory);
        Icon = isDirectory ? "folder" : EditorIcons.ForFile(fullPath);
    }

    /// <summary>Absolute path.</summary>
    public string FullPath { get; }

    /// <summary>Project-relative path with <c>/</c> separators (<c>Content/Scenes/Main.mscene</c>); empty for the root.</summary>
    public string ProjectPath { get; }

    public string Name { get; }

    /// <summary>The extension as on disk, with the dot (<c>.png</c>); empty for folders.</summary>
    public string Extension { get; }

    public bool IsDirectory { get; }

    public FileKind Kind { get; }

    /// <summary>Tabler icon name (scenes by root node type, resources by resource type, others by extension).</summary>
    public string Icon { get; internal set; }

    /// <summary>Icon tint class (<c>icon-3d</c>, <c>icon-resource</c>, …) for scenes and resources; null otherwise.</summary>
    public string? Family { get; internal set; }

    /// <summary>Scenes: the root node's <c>type</c> (<c>instance</c> for an inherited scene); resources: their <c>type</c>.</summary>
    public string? RootTypeName { get; internal set; }

    /// <summary>The asset UID (embedded in scenes/resources, else from the asset database), or null.</summary>
    public string? Uid { get; internal set; }

    public FileBadges Badges { get; internal set; }

    /// <summary>Tooltip for <see cref="Badges"/> (one line per problem, e.g. <c>Missing: Content/Textures/wood.png</c>); null when none.</summary>
    public string? BadgeText { get; internal set; }

    /// <summary>File size in bytes (0 for folders).</summary>
    public long Size { get; internal set; }

    public DateTime ModifiedUtc { get; internal set; }

    /// <summary>The containing folder; null for the root.</summary>
    public ProjectFileEntry? Parent { get; internal set; }

    /// <summary>Folders: the visible children, folders first, then by name (ordinal, ignoring case). Empty for files.</summary>
    public IReadOnlyList<ProjectFileEntry> Children { get; internal set; } = NoChildren;

    /// <summary>0 for the root, 1 for its children, …</summary>
    public int Depth { get; internal set; }

    /// <summary>The peeked content the icon/type/UID were derived from (scenes, resources, the project file).</summary>
    internal FilePeek? Peek { get; set; }

    public override string ToString() => ProjectPath.Length == 0 ? Name : ProjectPath;
}

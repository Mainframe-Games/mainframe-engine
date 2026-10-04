using MainframeEngine.Serialization;

namespace MainframeEngine.Editor;

/// <summary>Builds the <see cref="PickerEntry"/> trees of the create dialogs: node types, resource types, project scenes.</summary>
public static class PickerSources
{
    /// <summary>
    /// Every registered node type as an inheritance tree (engine and loaded game types; game types inherit their base's
    /// icon). Abstract types are structure-only rows; placeholders (<see cref="MissingNode"/>) and the tree's own
    /// <see cref="SceneViewport"/> are left out (their subclasses hang under the nearest listed base).
    /// </summary>
    public static IReadOnlyList<PickerEntry> NodeTypes() =>
        TypeEntries(TypeRegistry.All.Where(t => t.IsNode && t.Type != typeof(MissingNode) && t.Type != typeof(SceneViewport) && !IsEditorType(t)));

    /// <summary>
    /// The resource types a slot of type <paramref name="baseType"/> accepts, under <paramref name="baseType"/>'s own
    /// entry (scenes and placeholders are left out).
    /// </summary>
    public static IReadOnlyList<PickerEntry> ResourceTypes(Type baseType)
    {
        ArgumentNullException.ThrowIfNull(baseType);
        return TypeEntries(TypeRegistry.All.Where(t => t.IsResource && baseType.IsAssignableFrom(t.Type) &&
                                                       t.Type != typeof(PackedScene) && t.Type != typeof(MissingResource)));
    }

    // Editor-only tool nodes (the workspace, the viewport controller) never appear in a scene.
    private static bool IsEditorType(NodeTypeInfo info) => info.Type.Assembly == typeof(PickerSources).Assembly;

    private static List<PickerEntry> TypeEntries(IEnumerable<NodeTypeInfo> types)
    {
        var listed = types.ToDictionary(t => t.Type);
        var entries = new List<PickerEntry>(listed.Count);
        foreach (var info in listed.Values)
        {
            // The nearest listed base (skipping bases left out of the set).
            NodeTypeInfo? parent = null;
            for (var t = info.Type.BaseType; t is not null && parent is null; t = t.BaseType)
                parent = listed.GetValueOrDefault(t);
            var description = info.Description ?? "";
            if (info.Type.Assembly != typeof(Node).Assembly)
                description = description.Length == 0 ? $"Game type {info.Type.FullName}." : $"{description} (game type {info.Type.FullName})";
            entries.Add(new PickerEntry(info.Name, info.Name, EditorIcons.Classes(info.Type), parent?.Name, info)
            {
                Description = description,
                Selectable = !info.IsAbstract,
            });
        }

        return entries;
    }

    /// <summary>
    /// The <c>.mscene</c> files under <paramref name="root"/> (a project folder: its <c>Content/</c> when present) as a
    /// folder tree; ids and descriptions are paths relative to <paramref name="root"/>. Hidden and build folders are skipped.
    /// </summary>
    public static IReadOnlyList<PickerEntry> Scenes(string root, int maxFiles = 2000)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        var entries = new List<PickerEntry>();
        if (!Directory.Exists(root))
            return entries;
        var start = Directory.Exists(Path.Combine(root, "Content")) ? Path.Combine(root, "Content") : root;
        var folders = new HashSet<string>(StringComparer.Ordinal);
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(start, "*.mscene", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
            }).Where(f => !IsSkipped(Path.GetRelativePath(root, f))).Take(maxFiles).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"[Editor] Could not list scenes under '{start}': {e.Message}");
            return entries;
        }

        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            var folder = ParentOf(relative);
            AddFolders(entries, folders, folder);
            entries.Add(new PickerEntry(relative, Path.GetFileName(relative), "icon icon-" + EditorIcons.ForFile(file) + " icon-3d", folder, file)
            {
                Description = relative,
            });
        }

        return entries;
    }

    private static bool IsSkipped(string relative)
    {
        foreach (var part in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            if (part.StartsWith('.') || part is "bin" or "obj")
                return true;
        return false;
    }

    private static string? ParentOf(string relative)
    {
        var slash = relative.LastIndexOf('/');
        return slash > 0 ? relative[..slash] : null;
    }

    private static void AddFolders(List<PickerEntry> entries, HashSet<string> folders, string? folder)
    {
        if (folder is null || !folders.Add(folder))
            return;
        var parent = ParentOf(folder);
        AddFolders(entries, folders, parent);
        entries.Add(new PickerEntry(folder, folder[(folder.LastIndexOf('/') + 1)..], "icon icon-folder icon-dir", parent, null)
        {
            Description = folder + "/",
            Selectable = false,
            IsGroup = true,
        });
    }
}

namespace MainframeEngine.Editor;

/// <summary>
/// The resource row's More menu (editor-viewport-tools G7.2): Save as .mres, Make Unique (shallow and deep), Open the
/// file on its own and Show in FileSystem, every slot change undoable. The row's label says where the value lives: a
/// link icon and the file name, "inline", or "inline ×N" when the scene uses the same inline resource in N slots.
/// </summary>
public sealed partial class InspectorPanel
{
    /// <summary>
    /// The resource name shown in a slot: the file name and type for a file, "Type (inline)" for an inline resource, and
    /// "Type (inline ×N)" when the edited scene holds it in N slots.
    /// </summary>
    internal string ResourceLabel(InspectorProperty p, object? value)
    {
        ArgumentNullException.ThrowIfNull(p);
        if (value is Resource { IsExternal: false } inline and not MissingResource)
        {
            var uses = InlineUses(inline);
            return uses > 1 ? $"{inline.GetType().Name} (inline ×{uses})" : p.Format(value);
        }

        return p.Format(value);
    }

    // How many slots of the edited scene hold this inline resource (1 outside a scene: a resource file's inspector).
    private int InlineUses(Resource resource) =>
        EditContext is EditedScene scene ? Math.Max(1, scene.SlotsHolding(resource).Count) : 1;

    /// <summary>The More menu's items for the resource in row <paramref name="row"/> (empty when the slot is empty).</summary>
    public IReadOnlyList<MenuItem> ResourceMenu(int row)
    {
        if ((uint)row >= (uint)_rows.Count || _rows[row].Property.GetValue() is not Resource resource || resource is MissingResource)
            return [];
        var uses = resource.IsExternal ? 0 : InlineUses(resource);
        var canUnique = ResourceActions.CanMakeUnique(resource, uses, out var uniqueReason);
        var deep = canUnique && ResourceActions.HasInlineSubResources(resource);
        var file = resource.ResourcePath is { } path ? Path.GetFileName(path) : null;
        return
        [
            new MenuItem("Save as .mres…", "res-save", null, ResourceActions.CanSaveAsFile(resource) && EditContext is EditedScene, Icon: "device-floppy")
            {
                Tooltip = resource.IsExternal
                    ? $"Already a file: {file}"
                    : "Save as .mres — move the resource to its own file, shared by every slot that uses it",
            },
            new MenuItem("Make Unique", "res-unique", null, canUnique, Icon: "copy") { Tooltip = uniqueReason },
            new MenuItem("Make Unique (Deep)", "res-unique-deep", null, deep, Icon: "copy")
            {
                Tooltip = deep
                    ? "Make Unique (Deep) — an inline copy whose inline sub-resources are copied too"
                    : canUnique ? "The resource has no inline sub-resources: Make Unique copies it all." : uniqueReason,
            },
            MenuItem.Separator,
            new MenuItem("Open", "res-open", null, ResourceActions.IsResourceFile(resource), Icon: "pencil")
            {
                Tooltip = ResourceActions.IsResourceFile(resource)
                    ? $"Open — edit {file} on its own (its own history, Save and custom inspector)"
                    : resource.IsExternal ? $"{file} is imported: there is no .mres to open." : "Inline: Save as .mres first.",
            },
            new MenuItem("Show in FileSystem", "res-show", null, resource.IsExternal, Icon: "folder")
            {
                Tooltip = resource.IsExternal ? $"Show in FileSystem — select {file} in the FileSystem panel" : "Inline: it has no file.",
            },
        ];
    }

    // The More button's menu, under the button.
    private void ShowResourceMenu(int row)
    {
        var items = ResourceMenu(row);
        if (items.Count == 0)
            return;
        var (x, y) = (Workspace.Layout.Inspector.X + Workspace.Layout.Inspector.Width - 230f, 120f);
        if (IsLoaded && Document.GetElementById("inspector-body") is { IsNull: false } body &&
            body.QuerySelector($"[data-row=\"{row}\"][data-action=\"res-more\"]") is { IsNull: false } button)
        {
            var scale = MathF.Max(0.01f, Workspace.Host.PixelScale);
            var b = button.Bounds;
            (x, y) = ((b.X + b.Width) / scale, (b.Y + b.Height) / scale + 2f);
            Workspace.Popup.Show(items, x, y, command => RunAction(row, command), alignRight: true);
            return;
        }

        Workspace.Popup.Show(items, x, y, command => RunAction(row, command));
    }

    private void MakeUnique(InspectorProperty p, bool deep)
    {
        if (p.GetValue() is not Resource resource || EditContext is not { } context)
            return;
        var uses = resource.IsExternal ? 0 : InlineUses(resource);
        if (!ResourceActions.CanMakeUnique(resource, uses, out var reason))
        {
            Log.Warning($"[Editor] {reason}");
            return;
        }

        if (context is EditedScene scene)
            scene.MakeUnique(p.Target, p.Info, deep);
        else
            context.SetProperty(p.Target, p.Info, resource.Duplicate(deep));
        Rebuild();
    }

    private void OpenResourceFile(InspectorProperty p)
    {
        if (p.GetValue() is Resource resource && ResourceActions.IsResourceFile(resource))
            InspectResourceFile(AssetDatabase.Current.ToAbsolutePath(resource.ResourcePath!));
    }

    private void ShowResourceInFileSystem(InspectorProperty p)
    {
        if (p.GetValue() is not Resource { ResourcePath: { } path })
            return;
        Workspace.FileSystem.Select(AssetDatabase.Current.ToAbsolutePath(path)); // expands its folders and selects it
    }
}

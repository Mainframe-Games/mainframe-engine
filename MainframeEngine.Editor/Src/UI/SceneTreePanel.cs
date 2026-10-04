using MainframeEngine.UI.Rml;

namespace MainframeEngine.Editor;

/// <summary>Where a tree drag drops relative to the target row.</summary>
public enum TreeDropPosition
{
    None = 0,
    Inside = 1,
    Before = 2,
    After = 3,
}

/// <summary>
/// The scene tree dock: the active scene's hierarchy (<see cref="SceneTreeModel"/>, data-bound as <c>scene_tree</c>) with
/// expand/collapse, click/Ctrl+click/Shift+click selection, drag to reparent or reorder (drop on the upper or lower edge
/// of a row to insert before/after it, on its middle to make it the parent), double click (F2) to rename and a
/// right-click context menu.
/// </summary>
public sealed class SceneTreePanel : EditorDocument
{
    private const float DragThreshold = 4f;

    private static readonly RmlStructType<SceneTreeRow> RowType = new RmlStructType<SceneTreeRow>()
        .Member("name", static r => r.Name)
        .Member("type", static r => r.TypeName)
        .Member("icon", static r => r.Icon)
        .Member("index", static r => r.Index)
        .Member("indent", static r => r.Indent)
        .Member("has_children", static r => r.HasChildren)
        .Member("expanded", static r => r.Expanded)
        .Member("selected", static r => r.Selected)
        .Member("instance", static r => r.IsInstance)
        .Member("tooltip", static r => r.Tooltip)
        .Member("instance_tooltip", static r => r.InstanceTooltip)
        .Member("has_visible", static r => r.VisibleProperty is not null)
        .Member("shown", static r => r.Shown)
        .Member("eye_icon", static r => r.Shown ? "icon icon-sm icon-eye" : "icon icon-sm icon-eye-off")
        .Member("script", static r => r.Script)
        .Member("script_tooltip", static r => r.ScriptTooltip)
        .Member("warning", static r => r.Warning)
        .Member("drop", static r => r.Drop)
        .Member("drag", static r => r.Dragging);

    private readonly List<SceneTreeRow> _rows = [];
    private RmlDataModel? _model;
    private RmlEventListener? _mouseUpListener;
    private int _dragSource = -1;
    private float _dragStartY;
    private bool _dragging;
    private int _dropRow = -1;
    private TreeDropPosition _dropPosition;

    public SceneTreePanel(EditorWorkspace workspace)
        : base(workspace, "scene_tree.rml")
    {
    }

    /// <summary>The tree model (rows of the active scene).</summary>
    public SceneTreeModel Model { get; } = new();

    protected override void OnReady()
    {
        _model = CreateDataModel("scene_tree")
            .BindList("rows", _rows, RowType)
            .Event("press", e => Press(e.GetArgument(0).GetInt32(), e.Event.GetParameter("button", 0),
                ModifiersOf(e.Event), (float)e.Event.GetParameter("mouse_x", 0.0), (float)e.Event.GetParameter("mouse_y", 0.0)))
            .Event("hover", e => Hover(e.GetArgument(0).GetInt32(), e.Event))
            .Event("release", e =>
            {
                Release(e.GetArgument(0).GetInt32());
                e.Event.StopPropagation();
            })
            .Event("toggle", e =>
            {
                Toggle(e.GetArgument(0).GetInt32());
                e.Event.StopPropagation();
            })
            .Event("toggle_visible", e =>
            {
                ToggleVisible(e.GetArgument(0).GetInt32());
                e.Event.StopPropagation();
            })
            .Event("rename", _ => Workspace.Commands.Rename());
        Refresh();
    }

    /// <summary>Flips the <c>Visible</c> flag of row <paramref name="index"/>'s node through the undo history (the eye toggle).</summary>
    public bool ToggleVisible(int index)
    {
        if (Workspace.Session.Active is not { } scene || (uint)index >= (uint)_rows.Count)
            return false;
        var row = _rows[index];
        if (row.VisibleProperty is not { } property || !scene.IsEditable(row.Node))
            return false;
        scene.SetProperty(row.Node, property, property.GetValue(row.Node) is not true);
        return true;
    }

    protected override void OnAttach(RmlDocument document)
    {
        _mouseUpListener?.Remove();
        _mouseUpListener = document.AsElement().AddEventListener("mouseup", _ => CancelDrag());
    }

    private EditorModifiers ModifiersOf(RmlEvent e)
    {
        var modifiers = EditorModifiers.None;
        if (e.GetParameter("ctrl_key", 0) != 0 || e.GetParameter("meta_key", 0) != 0)
            modifiers |= EditorModifiers.Command;
        if (e.GetParameter("shift_key", 0) != 0)
            modifiers |= EditorModifiers.Shift;
        return modifiers | Workspace.Modifiers;
    }

    /// <summary>Rebuilds the rows from the active scene (structure, names, selection, expand state).</summary>
    public void Refresh()
    {
        var scene = Workspace.Session.Active;
        if (scene?.Selection.Primary is { } primary)
            Model.Reveal(scene.Root, primary);
        Model.Rebuild(scene);
        _rows.Clear();
        _rows.AddRange(Model.Rows);
        ResetDrag();
        _model?.Dirty("rows");
    }

    /// <summary>Expands or collapses row <paramref name="index"/>.</summary>
    public void Toggle(int index)
    {
        if ((uint)index >= (uint)_rows.Count)
            return;
        Model.Toggle(_rows[index].Node);
        Model.Rebuild(Workspace.Session.Active);
        _rows.Clear();
        _rows.AddRange(Model.Rows);
        _model?.Dirty("rows");
    }

    /// <summary>
    /// Mouse down on row <paramref name="index"/>: selects (Command toggles, Shift adds), starts a potential drag; the
    /// right button opens the context menu at (<paramref name="x"/>, <paramref name="y"/>) context pixels.
    /// </summary>
    public void Press(int index, int button, EditorModifiers modifiers, float x = 0, float y = 0)
    {
        if (Workspace.Session.Active is not { } scene || (uint)index >= (uint)_rows.Count)
            return;
        var node = _rows[index].Node;
        var selection = scene.Selection;
        if (button == 1)
        {
            if (!selection.Contains(node))
                selection.Set(node);
            var scale = MathF.Max(0.01f, Workspace.Host.PixelScale);
            ShowContextMenu(x / scale, y / scale);
            return;
        }

        if (button != 0)
            return;
        if ((modifiers & EditorModifiers.Command) != 0)
            selection.Toggle(node);
        else if ((modifiers & EditorModifiers.Shift) != 0)
            selection.Add(node);
        else
            selection.Set(node);

        _dragSource = scene.IsEditable(node) && !ReferenceEquals(node, scene.Root) ? IndexOf(node) : -1;
        _dragStartY = y;
        _dragging = false;
    }

    private int IndexOf(Node node)
    {
        for (var i = 0; i < _rows.Count; i++)
            if (ReferenceEquals(_rows[i].Node, node))
                return i;
        return -1;
    }

    private void Hover(int index, RmlEvent e)
    {
        if (_dragSource < 0 || (uint)index >= (uint)_rows.Count)
            return;
        var y = (float)e.GetParameter("mouse_y", 0.0);
        if (!_dragging)
        {
            if (MathF.Abs(y - _dragStartY) < DragThreshold * MathF.Max(1f, Workspace.Host.PixelScale))
                return;
            _dragging = true;
            _rows[_dragSource].Dragging = true;
        }

        var bounds = e.CurrentElement.Bounds;
        var relative = bounds.Height > 0 ? (y - bounds.Y) / bounds.Height : 0.5f;
        DragOver(index, relative);
    }

    /// <summary>The drag is over row <paramref name="index"/> at <paramref name="relativeY"/> (0 top … 1 bottom of the row).</summary>
    public void DragOver(int index, float relativeY)
    {
        if (_dragSource < 0 || (uint)index >= (uint)_rows.Count)
            return;
        _dragging = true;
        var position = relativeY < 0.25f ? TreeDropPosition.Before : relativeY > 0.75f ? TreeDropPosition.After : TreeDropPosition.Inside;
        if (!CanDrop(_rows[_dragSource].Node, _rows[index].Node, position))
            position = TreeDropPosition.None;
        if (index == _dropRow && position == _dropPosition)
            return;
        if (_dropRow >= 0 && _dropRow < _rows.Count)
            _rows[_dropRow].Drop = 0;
        _dropRow = index;
        _dropPosition = position;
        _rows[index].Drop = (int)position;
        _model?.Dirty("rows");
    }

    /// <summary>Mouse up over row <paramref name="index"/>: completes a drag (reparent/reorder) if one is in progress.</summary>
    public void Release(int index)
    {
        if (!_dragging || _dragSource < 0 || index != _dropRow || _dropPosition == TreeDropPosition.None)
        {
            CancelDrag();
            return;
        }

        var node = _rows[_dragSource].Node;
        var target = _rows[index].Node;
        var position = _dropPosition;
        ResetDrag();
        Drop(node, target, position);
    }

    /// <summary>Moves <paramref name="node"/> relative to <paramref name="target"/> through the undo history (keeps its global transform).</summary>
    public bool Drop(Node node, Node target, TreeDropPosition position)
    {
        if (Workspace.Session.Active is not { } scene || !CanDrop(node, target, position))
            return false;
        return position switch
        {
            TreeDropPosition.Inside => scene.Reparent(node, target),
            TreeDropPosition.Before => scene.Reparent(node, target.Parent!, target.GetIndex()),
            TreeDropPosition.After => scene.Reparent(node, target.Parent!, target.GetIndex() + 1),
            _ => false,
        };
    }

    private bool CanDrop(Node node, Node target, TreeDropPosition position)
    {
        if (Workspace.Session.Active is not { } scene || ReferenceEquals(node, target) || node.IsAncestorOf(target))
            return false;
        if (position == TreeDropPosition.Inside)
            return scene.IsEditable(target) && target.SceneFilePath is null || ReferenceEquals(target, scene.Root);
        return !ReferenceEquals(target, scene.Root) && target.Parent is { } parent && scene.IsEditable(parent);
    }

    private void CancelDrag()
    {
        if (_dragSource < 0 && _dropRow < 0)
            return;
        ResetDrag();
        _model?.Dirty("rows");
    }

    private void ResetDrag()
    {
        if (_dragSource >= 0 && _dragSource < _rows.Count)
            _rows[_dragSource].Dragging = false;
        if (_dropRow >= 0 && _dropRow < _rows.Count)
            _rows[_dropRow].Drop = 0;
        _dragSource = -1;
        _dropRow = -1;
        _dropPosition = TreeDropPosition.None;
        _dragging = false;
    }

    /// <summary>The context menu for the selection at (<paramref name="x"/>, <paramref name="y"/>) dp.</summary>
    public void ShowContextMenu(float x, float y)
    {
        var scene = Workspace.Session.Active;
        var hasSelection = scene?.Selection.Count > 0;
        var isRoot = scene is not null && ReferenceEquals(scene.Selection.Primary, scene.Root);
        Workspace.Popup.Show(
        [
            new MenuItem("Add Child Node…", "node.add", "Ctrl+A", Icon: "circle-plus"),
            new MenuItem("Instance Child Scene…", "scene.instance", "Ctrl+Shift+A", Icon: "link"),
            MenuItem.Separator,
            new MenuItem("Rename", "edit.rename", "F2", hasSelection, Icon: "pencil"),
            new MenuItem("Duplicate", "edit.duplicate", "Ctrl+D", hasSelection && !isRoot, Icon: "copy"),
            new MenuItem("Move Up", "node.move_up", "Ctrl+Up", hasSelection && !isRoot, Icon: "arrow-up"),
            new MenuItem("Move Down", "node.move_down", "Ctrl+Down", hasSelection && !isRoot, Icon: "arrow-down"),
            MenuItem.Separator,
            new MenuItem("Delete", "edit.delete", "Del", hasSelection && !isRoot, Icon: "trash"),
        ], x, y);
    }
}

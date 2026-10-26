using System.Numerics;
using MainframeEngine.Serialization;

namespace MainframeEngine.Editor;

/// <summary>
/// Range handles (editor-viewport-tools G7.3): every selected node with an <see cref="IHandleProvider"/> shows its
/// handles as small squares in the overlay lines, highlighted on hover and while dragged, with a label next to the
/// mouse ("Range 4.20 m"). Handles are hit-tested before the transform gizmo. A drag edits the one node whose handle was
/// grabbed, live through the provider, and commits one history entry on release; Esc or a right click restores the
/// values. Idle frames allocate nothing: the handle lists are reused.
/// </summary>
public sealed partial class ViewportController
{
    private const float HandlePoints = 4f; // half the square's side, in points
    private static readonly Vector4 HandleColor = new(0.95f, 0.95f, 1f, 1f);
    private static readonly Vector4 HollowHandleColor = new(0.7f, 0.72f, 0.78f, 0.9f);
    private static readonly Vector4 HotHandleColor = new(1f, 0.85f, 0.2f, 1f);

    private readonly List<EditorHandle> _handles = [];
    private readonly List<HandleOwner> _handleOwners = [];
    private readonly List<(ExportPropertyInfo Property, object? Before)> _handleBefore = [];
    private int _hoverHandle = -1;
    private Node? _dragHandleNode;
    private IHandleProvider? _dragHandleProvider;
    private EditorHandle _dragHandle;
    private Vector3 _dragHandleOrigin;
    private Vector3 _dragHandleGrab;
    private float _dragHandleAlong;
    private bool _handleLabelShown;

    // The node that owns handles [First, First + Count) of _handles.
    private readonly record struct HandleOwner(Node Node, IHandleProvider Provider, int First, int Count);

    /// <summary>The handles of the selected nodes in the active view, as last collected (tests and QA).</summary>
    public IReadOnlyList<EditorHandle> Handles => _handles;

    /// <summary>True while a handle is being dragged.</summary>
    public bool IsDraggingHandle => _drag == DragKind.Handle;

    /// <summary>The node owning handle <paramref name="index"/> of <see cref="Handles"/>.</summary>
    public Node HandleNode(int index) => _handleOwners[OwnerOf(index)].Node;

    private int OwnerOf(int index)
    {
        for (var i = 0; i < _handleOwners.Count; i++)
            if (index >= _handleOwners[i].First && index < _handleOwners[i].First + _handleOwners[i].Count)
                return i;
        return -1;
    }

    /// <summary>Collects the selected nodes' handles for the active view into <see cref="Handles"/> (no allocation).</summary>
    public void CollectHandles(EditedScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        _handles.Clear();
        _handleOwners.Clear();
        if (!HandlesEnabled(scene))
            return;
        var is2D = scene.Camera.Is2D;
        var pixels = ViewPixels;
        var selected = scene.Selection.Nodes;
        for (var i = 0; i < selected.Count; i++)
        {
            var node = selected[i];
            if (node.IsFreed || !node.IsInsideTree || !scene.IsEditable(node) || EditorLock.IsLocked(node))
                continue;
            if (is2D ? node is not Node2D { } n2 || !n2.IsVisibleInTree() : node is not Node3D { } n3 || !n3.IsVisibleInTree())
                continue;
            if (HandleProviders.Find(node.GetType()) is not { } provider)
                continue;
            var first = _handles.Count;
            provider.GetHandles(node, scene.Camera, pixels, _handles);
            if (_handles.Count > first)
                _handleOwners.Add(new HandleOwner(node, provider, first, _handles.Count - first));
        }
    }

    // Handles show while editing (not during a simulation, G7.5).
    private bool HandlesEnabled(EditedScene scene) => !IsSimulating(scene);

    /// <summary>The handle within the gizmo's grab distance of view pixel <paramref name="pixel"/>, or -1.</summary>
    public int HitTestHandle(EditedScene scene, Vector2 pixel)
    {
        ArgumentNullException.ThrowIfNull(scene);
        CollectHandles(scene);
        var view = ViewPixels;
        var best = -1;
        var bestDistance = TransformGizmo.GrabPixels * ViewScale;
        for (var i = 0; i < _handles.Count; i++)
        {
            if (!scene.Camera.Project(_handles[i].Position, view.X, view.Y, out var at))
                continue;
            var distance = Vector2.Distance(at, pixel);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        return best;
    }

    private bool TryBeginHandle(EditedScene scene)
    {
        var index = HitTestHandle(scene, LocalPixel(_mouse));
        if (index < 0 || OwnerOf(index) is var owner && owner < 0)
            return false;
        var (node, provider, _, _) = _handleOwners[owner];
        var handle = _handles[index];
        _handleBefore.Clear();
        var info = TypeRegistry.GetNearest(node.GetType());
        foreach (var name in provider.Properties(node, handle.Id))
            if (info?.FindProperty(name) is { } property)
                _handleBefore.Add((property, property.GetValue(node)));
        _dragHandleNode = node;
        _dragHandleProvider = provider;
        _dragHandle = handle with { DragAxis = SafeNormalize(handle.DragAxis) };
        _dragHandleOrigin = node is Node2D n2 ? new Vector3(n2.GlobalPosition, 0f) : ((Node3D)node).GlobalPosition;
        _dragHandleAlong = Vector3.Dot(handle.Position - _dragHandleOrigin, _dragHandle.DragAxis);
        _dragHandleGrab = AxisPoint(scene, _dragHandle.Position, _dragHandle.DragAxis);
        _hoverHandle = index;
        _drag = DragKind.Handle;
        _workspace.Gizmo.Hovered = GizmoHandle.None;
        Gizmo2D.Hovered = GizmoHandle.None;
        UpdateHandleLabel(scene);
        return true;
    }

    private static Vector3 SafeNormalize(Vector3 v) => v.LengthSquared() > 1e-12f ? Vector3.Normalize(v) : Vector3.UnitX;

    // The point of the line (point, axis) closest to the mouse ray.
    private Vector3 AxisPoint(EditedScene scene, Vector3 point, Vector3 axis)
    {
        var mouse = LocalPixel(_mouse);
        var view = ViewPixels;
        var (origin, direction) = scene.Camera.Ray(mouse.X, mouse.Y, view.X, view.Y);
        return TransformGizmo.ClosestPointOnAxis(origin, direction, point, axis);
    }

    private void UpdateHandle(EditedScene scene)
    {
        if (_dragHandleNode is not { IsFreed: false } node || _dragHandleProvider is not { } provider)
            return;
        var axis = _dragHandle.DragAxis;
        var point = AxisPoint(scene, _dragHandle.Position, axis);
        var along = _dragHandleAlong + Vector3.Dot(point - _dragHandleGrab, axis);
        var mouse = LocalPixel(_mouse);
        var view = ViewPixels;
        var (rayOrigin, rayDirection) = scene.Camera.Ray(mouse.X, mouse.Y, view.X, view.Y);
        var snap = _workspace.Gizmo.Snap;
        provider.Drag(node, _dragHandle.Id, new HandleDrag
        {
            NodeOrigin = _dragHandleOrigin,
            HandleStart = _dragHandle.Position,
            Axis = axis,
            Along = along,
            Point = _dragHandle.Position + axis * (along - _dragHandleAlong),
            RayOrigin = rayOrigin,
            RayDirection = rayDirection,
            Snap = snap.Enabled,
            SnapStep = snap.Translate,
            AngleStep = snap.RotateDegrees,
            Camera = scene.Camera,
            Is2D = scene.Camera.Is2D,
        });
        _workspace.Inspector.RefreshValues();
        UpdateHandleLabel(scene);
    }

    // One history entry for the drag (the node already shows it), or the values put back on cancel.
    private void EndHandle(EditedScene scene, bool commit)
    {
        var node = _dragHandleNode;
        _dragHandleNode = null;
        _dragHandleProvider = null;
        if (node is null || node.IsFreed)
        {
            _handleBefore.Clear();
            return;
        }

        var actions = new List<IEditorAction>(_handleBefore.Count);
        foreach (var (property, before) in _handleBefore)
        {
            var after = property.GetValue(node);
            if (!Equals(before, after))
                actions.Add(new SetPropertyAction(node, property, before, after));
        }

        _handleBefore.Clear();
        UpdateHandleLabel(scene);
        if (actions.Count == 0)
            return;
        if (!commit)
        {
            for (var i = actions.Count - 1; i >= 0; i--)
                actions[i].Undo();
            _workspace.Inspector.RefreshValues();
            return;
        }

        var first = (SetPropertyAction)actions[0];
        scene.History.Commit(new CompositeAction($"Set {InspectorProperty.Humanize(first.Property.Name)} on {node.Name}", [.. actions]), alreadyApplied: true);
    }

    // Hover highlight and label (mouse moves only, never per frame).
    private bool HoverHandle(EditedScene scene)
    {
        var index = ViewRect.Contains(_mouse.X, _mouse.Y) ? HitTestHandle(scene, LocalPixel(_mouse)) : -1;
        _hoverHandle = index;
        UpdateHandleLabel(scene);
        return index >= 0;
    }

    // The label next to the mouse: the hovered or dragged handle's Describe, else hidden.
    private void UpdateHandleLabel(EditedScene scene)
    {
        string? text = null;
        if (_drag == DragKind.Handle && _dragHandleNode is { IsFreed: false } dragged && _dragHandleProvider is { } provider)
        {
            text = provider.Describe(dragged, _dragHandle.Id);
        }
        else if (_drag == DragKind.None && (uint)_hoverHandle < (uint)_handles.Count && OwnerOf(_hoverHandle) is var owner && owner >= 0)
        {
            var o = _handleOwners[owner];
            text = o.Provider.Describe(o.Node, _handles[_hoverHandle].Id);
        }

        if (text is null && !_handleLabelShown)
            return;
        _handleLabelShown = text is not null;
        var rect = ViewRect;
        _workspace.ViewportPanel.SetHandleLabel(text, _mouse.X - rect.X + 14f, _mouse.Y - rect.Y + 10f);
    }

    private void DrawHandles(EditedScene scene, DebugLines overlay, Vector2 pixels)
    {
        CollectHandles(scene);
        if (_handles.Count == 0)
            return;
        var camera = scene.Camera;
        for (var o = 0; o < _handleOwners.Count; o++)
            _handleOwners[o].Provider.Draw(_handleOwners[o].Node, camera, pixels, overlay);
        for (var i = 0; i < _handles.Count; i++)
        {
            var handle = _handles[i];
            var half = HandlePoints * ViewScale * camera.WorldPerPixel(handle.Position, pixels.Y);
            var hot = _drag == DragKind.Handle
                ? _dragHandleNode is { } n && OwnerOf(i) is var owner && owner >= 0 && ReferenceEquals(_handleOwners[owner].Node, n) && handle.Id == _dragHandle.Id
                : i == _hoverHandle;
            var color = hot ? HotHandleColor : handle.Style == HandleStyle.Hollow ? HollowHandleColor : HandleColor;
            HandleDrawing.Square(overlay, handle.Position, camera.Right, camera.Up, half, color);
            if (handle.Style == HandleStyle.Square)
                HandleDrawing.Square(overlay, handle.Position, camera.Right, camera.Up, half * 0.5f, color);
        }
    }
}

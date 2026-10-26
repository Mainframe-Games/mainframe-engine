using System.Numerics;

namespace MainframeEngine.Editor;

/// <summary>
/// The multi-node gizmo (editor-viewport-tools G7.1): the gizmo moves, rotates and scales every selected 3D (or 2D) node
/// around a shared pivot (<see cref="GizmoPivot"/>), live, and commits one history entry on release; Esc or a right click
/// during the drag puts every node back. The targets are cached by scene and selection version, so idle frames allocate
/// nothing.
/// </summary>
public sealed partial class ViewportController
{
    private static readonly Vector4 PivotCrossColor = new(1f, 0.85f, 0.2f, 0.9f);

    private readonly List<Node3D> _targets3D = [];
    private readonly List<Node2D> _targets2D = [];
    private readonly List<Start3D> _starts3D = [];
    private readonly List<Start2D> _starts2D = [];
    private EditedScene? _targetsScene;
    private int _targetsVersion = -1;
    private int _targetsSelection = -1;
    private Vector3 _dragPivot;
    private Quaternion _dragAxes;
    private Vector2 _dragPivot2D;
    private float _dragAxes2D;
    private Vector2 _dragBase2D;
    private Vector2 _dragBaseScale2D;
    private bool _gizmo2D;

    // A target's global pose and exported transform values at drag start (undo and cancel).
    private readonly record struct Start3D(Node3D Node, GizmoPose3D Global, Vector3 Position, Vector3 RotationDegrees, Vector3 Scale);

    private readonly record struct Start2D(Node2D Node, GizmoPose2D Global, Vector2 Position, float RotationDegrees, Vector2 Scale);

    /// <summary>
    /// The nodes the 3D gizmo moves: the selected <see cref="Node3D"/>s that are editable, inside the tree, visible and
    /// not locked, minus those whose ancestor is also one (it follows its parent). Empty in Select mode.
    /// </summary>
    public IReadOnlyList<Node3D> GizmoTargets(EditedScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        RefreshTargets(scene);
        return _workspace.Gizmo.Mode == GizmoMode.Select ? [] : _targets3D;
    }

    /// <summary>The 2D <see cref="GizmoTargets"/> (selected <see cref="Node2D"/>s).</summary>
    public IReadOnlyList<Node2D> GizmoTargets2D(EditedScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        RefreshTargets(scene);
        return _workspace.Gizmo.Mode == GizmoMode.Select ? [] : _targets2D;
    }

    // Rebuilt when the selection or the scene changes, never per frame.
    private void RefreshTargets(EditedScene scene)
    {
        if (ReferenceEquals(_targetsScene, scene) && _targetsVersion == scene.Version && _targetsSelection == scene.Selection.Version)
            return;
        _targetsScene = scene;
        _targetsVersion = scene.Version;
        _targetsSelection = scene.Selection.Version;
        _targets3D.Clear();
        _targets2D.Clear();
        var selected = scene.Selection.Nodes;
        for (var i = 0; i < selected.Count; i++)
        {
            var node = selected[i];
            if (!IsGizmoCandidate(scene, node))
                continue;
            if (node is Node3D n3)
                _targets3D.Add(n3);
            else if (node is Node2D n2)
                _targets2D.Add(n2);
        }

        DropNested(_targets3D);
        DropNested(_targets2D);
    }

    private static bool IsGizmoCandidate(EditedScene scene, Node node) =>
        !node.IsFreed && node.IsInsideTree && scene.IsEditable(node) && !EditorLock.IsLocked(node) && node switch
        {
            Node3D n3 => n3.IsVisibleInTree(),
            Node2D n2 => n2.IsVisibleInTree(),
            _ => false,
        };

    // A node whose ancestor is also a target follows its parent (Godot, Blender).
    private static void DropNested<T>(List<T> targets) where T : Node
    {
        for (var i = targets.Count - 1; i >= 0; i--)
        {
            for (var j = 0; j < targets.Count; j++)
            {
                if (i != j && targets[j].IsAncestorOf(targets[i]))
                {
                    targets.RemoveAt(i);
                    break;
                }
            }
        }
    }

    // The active target: the primary selection when it is one, else the last target.
    private static int ActiveIndex<T>(List<T> targets, Node? primary) where T : Node
    {
        for (var i = 0; i < targets.Count; i++)
            if (ReferenceEquals(targets[i], primary))
                return i;
        return targets.Count - 1;
    }

    /// <summary>
    /// The 3D gizmo's pose for the current targets: its origin (<see cref="GizmoPivot"/>) and the active node's global
    /// rotation (the handles' axes in Local space and for scale). False when there is nothing to move.
    /// </summary>
    public bool TryGetGizmoPose(EditedScene scene, out Vector3 pivot, out Quaternion axes)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var targets = GizmoTargets(scene);
        pivot = Vector3.Zero;
        axes = Quaternion.Identity;
        if (targets.Count == 0)
            return false;
        var active = ActiveIndex(_targets3D, scene.Selection.Primary);
        axes = _targets3D[active].GlobalRotation;
        if (_workspace.Gizmo.Pivot == GizmoPivot.Active || _targets3D.Count == 1)
        {
            pivot = _targets3D[active].GlobalPosition;
            return true;
        }

        var sum = Vector3.Zero;
        for (var i = 0; i < _targets3D.Count; i++)
            sum += _targets3D[i].GlobalPosition;
        pivot = sum / _targets3D.Count;
        return true;
    }

    /// <summary>The 2D <see cref="TryGetGizmoPose"/> (rotation in radians).</summary>
    public bool TryGetGizmoPose2D(EditedScene scene, out Vector2 pivot, out float axes)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var targets = GizmoTargets2D(scene);
        pivot = Vector2.Zero;
        axes = 0f;
        if (targets.Count == 0)
            return false;
        var active = ActiveIndex(_targets2D, scene.Selection.Primary);
        var global = _targets2D[active].GlobalTransform;
        axes = global.Rotation;
        if (_workspace.Gizmo.Pivot == GizmoPivot.Active || _targets2D.Count == 1)
        {
            pivot = global.Origin;
            return true;
        }

        var sum = Vector2.Zero;
        for (var i = 0; i < _targets2D.Count; i++)
            sum += _targets2D[i].GlobalPosition;
        pivot = sum / _targets2D.Count;
        return true;
    }

    /// <summary>True while the gizmo is being dragged (3D or 2D).</summary>
    public bool IsDraggingGizmo => _drag == DragKind.Gizmo;

    // Drags that Esc or a right click cancel (everything goes back to where it was).
    private bool IsCancellableDrag => _drag is DragKind.Gizmo;

    // ── 3D ───────────────────────────────────────────────────────────────────────────────────────────────────────

    private bool TryBeginGizmo(EditedScene scene)
    {
        if (!TryGetGizmoPose(scene, out var pivot, out var axes))
            return false;
        var gizmo = _workspace.Gizmo;
        var local = LocalPixel(_mouse);
        var handle = gizmo.HitTest(scene.Camera, ViewPixels, pivot, axes, local);
        if (handle == GizmoHandle.None)
            return false;
        _starts3D.Clear();
        foreach (var node in _targets3D)
            _starts3D.Add(new Start3D(node, new GizmoPose3D(node.GlobalPosition, node.GlobalRotation, node.Scale), node.Position, node.RotationDegrees, node.Scale));
        _dragPivot = pivot;
        _dragAxes = axes;
        _gizmo2D = false;
        gizmo.BeginDrag(handle, scene.Camera, ViewPixels, local, pivot, axes, Vector3.One);
        _drag = DragKind.Gizmo;
        return true;
    }

    private void UpdateGizmo(EditedScene scene)
    {
        var gizmo = _workspace.Gizmo;
        var (position, rotation, factor) = gizmo.Drag(scene.Camera, ViewPixels, LocalPixel(_mouse));
        foreach (ref readonly var start in System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_starts3D))
        {
            if (start.Node.IsFreed)
                continue;
            var pose = MultiGizmo.Apply(gizmo.Mode, gizmo.Pivot, gizmo.Active, gizmo.Local, _dragPivot, _dragAxes, position, rotation, factor, start.Global);
            var node = start.Node;
            switch (gizmo.Mode)
            {
                case GizmoMode.Translate:
                    node.GlobalPosition = pose.Position;
                    break;
                case GizmoMode.Rotate:
                    node.GlobalPosition = pose.Position;
                    var parentRotation = node.Parent is Node3D parent ? parent.GlobalRotation : Quaternion.Identity;
                    node.Rotation = Quaternion.Normalize(Quaternion.Inverse(parentRotation) * pose.Rotation);
                    break;
                case GizmoMode.Scale:
                    node.GlobalPosition = pose.Position;
                    node.Scale = pose.Scale;
                    break;
            }
        }

        _workspace.Inspector.RefreshValues(); // the fields follow the drag (committed on release)
    }

    // Commits the drag as one history entry (the nodes already show the result), or restores the start on cancel.
    private void EndGizmo(EditedScene scene, bool commit)
    {
        _workspace.Gizmo.EndDrag();
        var actions = new List<IEditorAction>();
        Node? only = null;
        var moved = 0;
        foreach (var start in _starts3D)
        {
            var node = start.Node;
            if (node.IsFreed)
                continue;
            var count = actions.Count;
            var info = Serialization.TypeRegistry.GetRequired(node.GetType());
            AddChange(actions, node, info, "Position", start.Position, node.Position);
            AddChange(actions, node, info, "RotationDegrees", start.RotationDegrees, node.RotationDegrees);
            AddChange(actions, node, info, "Scale", start.Scale, node.Scale);
            if (actions.Count > count)
            {
                moved++;
                only = node;
            }
        }

        _starts3D.Clear();
        CommitOrRestore(scene, actions, commit, moved, only);
    }

    private void CommitOrRestore(EditedScene scene, List<IEditorAction> actions, bool commit, int moved, Node? only)
    {
        if (actions.Count == 0)
            return;
        if (!commit)
        {
            for (var i = actions.Count - 1; i >= 0; i--)
                actions[i].Undo();
            _workspace.Inspector.RefreshValues();
            return;
        }

        var verb = _workspace.Gizmo.Mode switch { GizmoMode.Rotate => "Rotate", GizmoMode.Scale => "Scale", _ => "Move" };
        var name = moved == 1 && only is not null ? $"{verb} {only.Name}" : $"{verb} {moved} nodes";
        scene.History.Commit(new CompositeAction(name, [.. actions]), alreadyApplied: true);
    }

    private static void AddChange(List<IEditorAction> actions, Node node, Serialization.NodeTypeInfo info, string property, object before, object after)
    {
        if (Equals(before, after) || info.FindProperty(property) is not { } p)
            return;
        actions.Add(new SetPropertyAction(node, p, before, after));
    }

    // ── 2D ───────────────────────────────────────────────────────────────────────────────────────────────────────

    private bool TryBeginGizmo2D(EditedScene scene)
    {
        if (!TryGetGizmoPose2D(scene, out var pivot, out var axes))
            return false;
        var local = LocalPixel(_mouse);
        var handle = Gizmo2D.HitTest(scene.Camera, ViewPixels, pivot, axes, local);
        if (handle == GizmoHandle.None)
            return false;
        _starts2D.Clear();
        foreach (var node in _targets2D)
        {
            var global = node.GlobalTransform;
            _starts2D.Add(new Start2D(node, new GizmoPose2D(global.Origin, global.Rotation, node.Scale), node.Position, node.RotationDegrees, node.Scale));
        }

        _dragPivot2D = pivot;
        _dragAxes2D = axes;
        _gizmo2D = true;
        // One node: the gizmo snaps its global position and scale exactly as before; several: it drags the pivot's delta
        // (from zero) and a scale factor (from one), so snapping keeps the nodes' relative offsets.
        var single = _starts2D.Count == 1;
        _dragBase2D = single ? pivot : Vector2.Zero;
        _dragBaseScale2D = single ? _starts2D[0].Scale : Vector2.One;
        Gizmo2D.BeginDrag(handle, scene.Camera, ViewPixels, local, pivot, axes, _dragBase2D, _dragBaseScale2D);
        _drag = DragKind.Gizmo;
        return true;
    }

    private void UpdateGizmo2D(EditedScene scene)
    {
        var (position, rotation, scale) = Gizmo2D.Drag(scene.Camera, ViewPixels, LocalPixel(_mouse));
        var gizmo = _workspace.Gizmo;
        var single = _starts2D.Count == 1;
        var move = position - _dragBase2D;
        var angle = rotation - _dragAxes2D;
        var factor = single ? SafeRatio(scale, _dragBaseScale2D) : scale;
        foreach (ref readonly var start in System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_starts2D))
        {
            var node = start.Node;
            if (node.IsFreed)
                continue;
            var pose = MultiGizmo.Apply(gizmo.Mode, gizmo.Pivot, Gizmo2D.Active, gizmo.Local, _dragPivot2D, _dragAxes2D, move, angle, factor, start.Global);
            switch (gizmo.Mode)
            {
                case GizmoMode.Translate:
                    node.GlobalPosition = pose.Position;
                    if (Gizmo2D.PixelSnap && !gizmo.Snap.Enabled && node.ParentNode2D is not null)
                        node.Position = new Vector2(MathF.Round(node.Position.X), MathF.Round(node.Position.Y));
                    break;
                case GizmoMode.Rotate:
                    node.GlobalPosition = pose.Position;
                    node.Rotation = pose.Rotation - (node.ParentNode2D?.GlobalRotation ?? 0f);
                    break;
                case GizmoMode.Scale:
                    node.GlobalPosition = pose.Position;
                    node.Scale = single ? scale : pose.Scale;
                    break;
            }
        }

        _workspace.Inspector.RefreshValues();
    }

    private static Vector2 SafeRatio(Vector2 value, Vector2 start) =>
        new(MathF.Abs(start.X) < 1e-6f ? 1f : value.X / start.X, MathF.Abs(start.Y) < 1e-6f ? 1f : value.Y / start.Y);

    private void EndGizmo2D(EditedScene scene, bool commit)
    {
        Gizmo2D.EndDrag();
        var actions = new List<IEditorAction>();
        Node? only = null;
        var moved = 0;
        foreach (var start in _starts2D)
        {
            var node = start.Node;
            if (node.IsFreed)
                continue;
            var count = actions.Count;
            var info = Serialization.TypeRegistry.GetRequired(node.GetType());
            AddChange(actions, node, info, "Position", start.Position, node.Position);
            AddChange(actions, node, info, "RotationDegrees", start.RotationDegrees, node.RotationDegrees);
            AddChange(actions, node, info, "Scale", start.Scale, node.Scale);
            if (actions.Count > count)
            {
                moved++;
                only = node;
            }
        }

        _starts2D.Clear();
        CommitOrRestore(scene, actions, commit, moved, only);
    }

    // Ends a gizmo drag of either view (cancel: every node back where it started).
    private void EndAnyGizmo(EditedScene scene, bool commit)
    {
        if (_gizmo2D)
            EndGizmo2D(scene, commit);
        else
            EndGizmo(scene, commit);
    }

    // ── Drawing ──────────────────────────────────────────────────────────────────────────────────────────────────

    private void DrawGizmo3D(EditedScene scene, DebugLines overlay, Vector2 pixels, float displayHeight)
    {
        // Drawn at the live pose: rotating or scaling about the median, the active origin or each origin leaves the
        // pivot where it is, and the active node's rotation turns the local axes with it, as for one node.
        if (!TryGetGizmoPose(scene, out var pivot, out var axes))
            return;
        _workspace.Gizmo.Draw(overlay, scene.Camera, pixels, pivot, axes);
        if (_workspace.Gizmo.Pivot != GizmoPivot.Individual || _targets3D.Count < 2)
            return;
        for (var i = 0; i < _targets3D.Count; i++)
        {
            var p = _targets3D[i].GlobalPosition;
            var r = scene.Camera.WorldPerPixel(p, displayHeight) * 5f;
            overlay.AddLine(p - scene.Camera.Right * r, p + scene.Camera.Right * r, PivotCrossColor);
            overlay.AddLine(p - scene.Camera.Up * r, p + scene.Camera.Up * r, PivotCrossColor);
        }
    }

    private void DrawGizmo2D(EditedScene scene, DebugLines overlay, Vector2 pixels)
    {
        if (!TryGetGizmoPose2D(scene, out var pivot, out var axes))
            return;
        Gizmo2D.Draw(overlay, scene.Camera, pixels, pivot, axes);
        if (_workspace.Gizmo.Pivot != GizmoPivot.Individual || _targets2D.Count < 2)
            return;
        var r = 5f * ViewScale / scene.Camera.Zoom2D;
        for (var i = 0; i < _targets2D.Count; i++)
        {
            var p = new Vector3(_targets2D[i].GlobalPosition, 0f);
            overlay.AddLine(p - new Vector3(r, 0, 0), p + new Vector3(r, 0, 0), PivotCrossColor);
            overlay.AddLine(p - new Vector3(0, r, 0), p + new Vector3(0, r, 0), PivotCrossColor);
        }
    }
}

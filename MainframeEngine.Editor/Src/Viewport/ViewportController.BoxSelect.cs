using System.Numerics;

namespace MainframeEngine.Editor;

/// <summary>How a box selection combines with the current selection (the modifier held at release).</summary>
public enum BoxSelectMode
{
    /// <summary>No modifier: the box's nodes become the selection.</summary>
    Replace,

    /// <summary>Shift: the box's nodes are added.</summary>
    Add,

    /// <summary>Ctrl/Cmd: each of the box's nodes is toggled.</summary>
    Toggle,
}

/// <summary>
/// Box selection (editor-viewport-tools G7.4): a left drag from empty space past the click distance draws a rectangle
/// (an RmlUi element over the view); on release the 3D view selects what is visible in it — the meshes of a region read
/// back from the object-ID target, and the icon nodes (lights, cameras, audio) whose origin is inside — and the 2D view
/// the nodes whose bounds are fully inside. Hits go through <see cref="EditedScene.SelectableFor"/>; the scene root, locked,
/// hidden and duplicate nodes are dropped, and the result is in tree order (the last is the active node).
/// </summary>
public sealed partial class ViewportController
{
    private readonly List<Node> _boxNodes = [];
    private readonly HashSet<Node> _boxSet = [];
    private PickHandle _boxPick;
    private EditedScene? _boxScene;
    private BoxSelectMode _boxMode;

    /// <summary>True while a 3D box selection waits for the GPU's region readback.</summary>
    public bool IsBoxSelecting => _boxPick.IsValid;

    /// <summary>True while the mouse draws a selection rectangle.</summary>
    public bool IsDrawingBox => _drag == DragKind.Box;

    private BoxSelectMode ModeFromModifiers()
    {
        var modifiers = _workspace.Modifiers;
        if ((modifiers & EditorModifiers.Command) != 0)
            return BoxSelectMode.Toggle;
        return (modifiers & EditorModifiers.Shift) != 0 ? BoxSelectMode.Add : BoxSelectMode.Replace;
    }

    // The rectangle in dp from the view's corner, as the RmlUi element shows it.
    private void ShowBox()
    {
        var rect = ViewRect;
        var a = _pressAt - new Vector2(rect.X, rect.Y);
        var b = _mouse - new Vector2(rect.X, rect.Y);
        var min = Vector2.Clamp(Vector2.Min(a, b), Vector2.Zero, new Vector2(rect.Width, rect.Height));
        var max = Vector2.Clamp(Vector2.Max(a, b), Vector2.Zero, new Vector2(rect.Width, rect.Height));
        _workspace.ViewportPanel.SetBoxSelect(new LayoutRect(min.X, min.Y, max.X - min.X, max.Y - min.Y));
    }

    private void HideBox() => _workspace.ViewportPanel.SetBoxSelect(null);

    /// <summary>
    /// Box-selects between window points <paramref name="from"/> and <paramref name="to"/> (what a drag does on release;
    /// also for tests and QA). 3D results land a couple of frames later (<see cref="IsBoxSelecting"/>).
    /// </summary>
    public void BoxSelect(EditedScene scene, Vector2 from, Vector2 to, BoxSelectMode mode)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var a = LocalPixel(from);
        var b = LocalPixel(to);
        var min = Vector2.Min(a, b);
        var max = Vector2.Max(a, b);
        _boxNodes.Clear();
        if (scene.Camera.Is2D)
        {
            CollectBox2D(scene, min, max);
            ApplyBox(scene, mode);
            return;
        }

        // Icons first (nodes without geometry): their projected origin inside the rectangle.
        RefreshIcons(scene);
        var view = ViewPixels;
        foreach (var node in _iconNodes)
        {
            if (!node.IsInsideTree || !node.IsVisibleInTree() || !scene.Camera.Project(node.GlobalPosition, view.X, view.Y, out var at))
                continue;
            if (at.X >= min.X && at.X <= max.X && at.Y >= min.Y && at.Y <= max.Y)
                _boxNodes.Add(node);
        }

        // Then the meshes the view shows in it, read back from the object-ID target.
        _boxPick = scene.Viewport.RequestPickRegion((int)MathF.Floor(min.X), (int)MathF.Floor(min.Y),
            (int)MathF.Ceiling(max.X - min.X) + 1, (int)MathF.Ceiling(max.Y - min.Y) + 1);
        _boxScene = scene;
        _boxMode = mode;
        if (!_boxPick.IsValid)
            ApplyBox(scene, mode); // no GPU (headless): the icons alone
    }

    private void PollBox(EditedScene scene)
    {
        if (!_boxPick.IsValid)
            return;
        if (!ReferenceEquals(_boxScene, scene))
        {
            _boxPick = default;
            _boxNodes.Clear();
            return;
        }

        if (!scene.Viewport.TryGetPickRegionResult(_boxPick, _boxNodes))
            return;
        _boxPick = default;
        ApplyBox(scene, _boxMode);
    }

    // Maps the hits to selectable, unlocked, visible nodes in tree order and applies them.
    private void ApplyBox(EditedScene scene, BoxSelectMode mode)
    {
        _boxSet.Clear();
        foreach (var hit in _boxNodes)
            if (scene.SelectableFor(hit) is { } node && !ReferenceEquals(node, scene.Root) && IsBoxSelectable(node))
                _boxSet.Add(node);
        _boxNodes.Clear();
        if (_boxSet.Count > 0)
            InTreeOrder(scene.Root, _boxSet, _boxNodes);
        _boxSet.Clear();
        switch (mode)
        {
            case BoxSelectMode.Add:
                scene.Selection.AddRange(_boxNodes);
                break;
            case BoxSelectMode.Toggle:
                scene.Selection.ToggleRange(_boxNodes);
                break;
            default:
                if (_boxNodes.Count == 0)
                    scene.Selection.Clear();
                else
                    scene.Selection.Set(_boxNodes);
                break;
        }

        _boxNodes.Clear();
    }

    private static bool IsBoxSelectable(Node node) => !node.IsFreed && node.IsInsideTree && !EditorLock.IsLocked(node) && node switch
    {
        Node3D n3 => n3.IsVisibleInTree(),
        Node2D n2 => n2.IsVisibleInTree(),
        _ => true,
    };

    private static void InTreeOrder(Node node, HashSet<Node> set, List<Node> result)
    {
        if (set.Contains(node))
            result.Add(node);
        var children = node.Children;
        for (var i = 0; i < children.Count; i++)
            InTreeOrder(children[i], set, result);
    }

    // ── 2D: bounds on the CPU ────────────────────────────────────────────────────────────────────────────────────

    private void CollectBox2D(EditedScene scene, Vector2 min, Vector2 max)
    {
        RefreshNodes2D(scene);
        var view = ViewPixels;
        var a = scene.Camera.ScreenToWorld2D(min, view);
        var b = scene.Camera.ScreenToWorld2D(max, view);
        var worldMin = Vector2.Min(a, b);
        var worldMax = Vector2.Max(a, b);
        foreach (var node in _nodes2D)
        {
            if (!node.IsInsideTree || !node.IsVisibleInTree())
                continue;
            var (lo, hi) = Bounds2D(node);
            if (lo.X >= worldMin.X && lo.Y >= worldMin.Y && hi.X <= worldMax.X && hi.Y <= worldMax.Y)
                _boxNodes.Add(node);
        }
    }

    /// <summary>
    /// The world bounds a 2D box selection must enclose: collision rectangles and circles, sprites' rectangles and
    /// camera frames (transformed); other nodes are their origin.
    /// </summary>
    internal (Vector2 Min, Vector2 Max) Bounds2D(Node2D node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var transform = node.GlobalTransform;
        switch (node)
        {
            case CollisionShape2D { Shape: RectangleShape2D rect }:
                return TransformedBounds(transform, -rect.Size * 0.5f, rect.Size * 0.5f);
            case CollisionShape2D { Shape: CircleShape2D circle }:
                {
                    var scale = MathF.Max(transform.X.Length(), transform.Y.Length());
                    var r = new Vector2(circle.Radius * scale);
                    return (transform.Origin - r, transform.Origin + r);
                }

            case Sprite2D sprite when sprite.GetRect() is { Size: { X: > 0, Y: > 0 } } local:
                return TransformedBounds(transform, local.Position, local.End);
            case Camera2D camera:
                {
                    var window = _workspace.Session.Project?.Window;
                    var size = new Vector2(window?.Width ?? WindowSettings.DefaultWidth, window?.Height ?? WindowSettings.DefaultHeight) / camera.Zoom;
                    return (camera.GlobalPosition - size * 0.5f, camera.GlobalPosition + size * 0.5f);
                }

            default:
                return (transform.Origin, transform.Origin);
        }
    }

    private static (Vector2 Min, Vector2 Max) TransformedBounds(Transform2D transform, Vector2 min, Vector2 max)
    {
        var p0 = transform.TransformPoint(min);
        var p1 = transform.TransformPoint(new Vector2(max.X, min.Y));
        var p2 = transform.TransformPoint(max);
        var p3 = transform.TransformPoint(new Vector2(min.X, max.Y));
        return (Vector2.Min(Vector2.Min(p0, p1), Vector2.Min(p2, p3)), Vector2.Max(Vector2.Max(p0, p1), Vector2.Max(p2, p3)));
    }
}

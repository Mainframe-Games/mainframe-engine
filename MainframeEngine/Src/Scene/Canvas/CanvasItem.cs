using System.Numerics;

namespace MainframeEngine;

/// <summary>How an item's children are clipped to its own pixels (Godot's <c>CanvasItem.ClipChildrenMode</c>).</summary>
public enum ClipChildrenMode : byte
{
    Disabled,

    /// <summary>Children are drawn only where this item has alpha; the item itself is not drawn.</summary>
    Only,

    /// <summary>The item is drawn and its children are clipped to it.</summary>
    AndDraw,
}

/// <summary>Texture filtering of a canvas item (Godot's <c>CanvasItem.TextureFilterEnum</c>; mipmapped variants omitted).</summary>
public enum CanvasTextureFilter : byte
{
    /// <summary>The parent item's filter (the canvas default, linear, at the root).</summary>
    ParentNode,
    Nearest,
    Linear,
}

/// <summary>Texture repeat of a canvas item (Godot's <c>CanvasItem.TextureRepeatEnum</c>).</summary>
public enum CanvasTextureRepeat : byte
{
    /// <summary>The parent item's mode (disabled at the root).</summary>
    ParentNode,
    Disabled,
    Enabled,
    Mirror,
}

/// <summary>
/// Base of everything drawn on a 2D canvas (Godot's <c>CanvasItem</c>; in this engine <see cref="Node2D"/> is the only
/// subclass family — the UI is RmlUi). Holds visibility, <see cref="Modulate"/>/<see cref="SelfModulate"/>, draw order
/// (<see cref="ZIndex"/>, <see cref="YSortEnabled"/>, <see cref="ShowBehindParent"/>, <see cref="TopLevel"/>), the
/// material and the item's recorded draw commands. Override <see cref="OnDraw"/> (Godot's <c>_draw</c>) and call the
/// <c>Draw*</c> methods inside it; call <see cref="QueueRedraw"/> to have it run again. The canvas server culls and orders
/// items exactly like Godot's <c>RendererCanvasCull</c> (docs/design/canvas.md, ADR 0111).
/// </summary>
[EditorIcon("brush", Family = EditorIconFamily.Space2D)]
public abstract class CanvasItem : Node
{
    private static readonly Vector4 White = Vector4.One;

    private readonly CanvasDrawList _drawList = new();
    private bool _visible = true;
    private bool _redrawQueued;
    private bool _drawing;
    private bool _topLevel;
    private int _zIndex;
    private Canvas? _rootOf;   // the canvas this item is registered in as a root (parent not a canvas item, or top level)

    /// <summary>Hidden items and their children are not drawn (Godot's <c>visible</c>).</summary>
    [Export]
    public bool Visible
    {
        get => _visible;
        set
        {
            if (_visible == value)
                return;
            _visible = value;
            if (value)
                QueueRedraw();
            VisibilityChanged?.Invoke();
        }
    }

    /// <summary>Colour multiplied into this item and its children (gamma-space RGBA; values above 1 brighten).</summary>
    [Export]
    public Vector4 Modulate { get; set; } = White;

    /// <summary>Colour multiplied into this item only.</summary>
    [Export]
    public Vector4 SelfModulate { get; set; } = White;

    /// <summary>Draw order: higher draws on top (−4096 … 4096).</summary>
    [Export(Range = "-4096,4096,1")]
    public int ZIndex
    {
        get => _zIndex;
        set => _zIndex = Math.Clamp(value, CanvasCuller.ZMin, CanvasCuller.ZMax);
    }

    /// <summary>Whether <see cref="ZIndex"/> adds to the parent's z (true, Godot's default) or replaces it.</summary>
    [Export]
    public bool ZAsRelative { get; set; } = true;

    /// <summary>Draws the children (and their y-sorted descendants) ordered by their Y position.</summary>
    [Export]
    public bool YSortEnabled { get; set; }

    /// <summary>Draws this item before (below) its parent.</summary>
    [Export]
    public bool ShowBehindParent { get; set; }

    /// <summary>
    /// Ignores the parent's transform, modulate and order: the item is drawn as a root of its canvas and its transform is
    /// global (Godot's <c>top_level</c>).
    /// </summary>
    [Export]
    public bool TopLevel
    {
        get => _topLevel;
        set
        {
            if (_topLevel == value)
                return;
            _topLevel = value;
            OnTopLevelChanged();
            RefreshCanvasRegistration();
        }
    }

    /// <summary>Clips the children to this item's pixels.</summary>
    [Export]
    public ClipChildrenMode ClipChildren { get; set; }

    /// <summary>The canvas server's handle on this item's clip-children group target.</summary>
    internal Texture2D? ClipGroupTexture { get; set; }

    /// <summary>The material this item draws with (<see cref="CanvasItemMaterial"/> or <see cref="ShaderMaterial"/>; null = default).</summary>
    [Export]
    public Material? Material { get; set; }

    /// <summary>Uses the parent's material instead of <see cref="Material"/>.</summary>
    [Export]
    public bool UseParentMaterial { get; set; }

    /// <summary>Lights whose item cull mask shares a bit with this mask light the item.</summary>
    [Export]
    public uint LightMask { get; set; } = 1;

    /// <summary>Viewports whose canvas cull mask shares a bit with this mask draw the item.</summary>
    [Export]
    public uint VisibilityLayer { get; set; } = 1;

    [Export]
    public CanvasTextureFilter TextureFilter { get; set; }

    [Export]
    public CanvasTextureRepeat TextureRepeat { get; set; }

    /// <summary>Raised when <see cref="Visible"/> changes.</summary>
    [Signal]
    public event Action? VisibilityChanged;

    /// <summary>Raised when the item is about to draw (after the built-in drawing, before <see cref="OnDraw"/>).</summary>
    [Signal]
    public event Action? Draw;

    /// <summary>The commands recorded by the last draw.</summary>
    public CanvasDrawList DrawList => _drawList;

    /// <summary>The canvas this item is registered in as a root, or null when it hangs under another canvas item.</summary>
    internal Canvas? RootCanvas => _rootOf;

    /// <summary>The canvas item the canvas server treats as this item's parent, or null for a root.</summary>
    public CanvasItem? CanvasParent => _topLevel ? null : Parent as CanvasItem;

    /// <summary>True when a redraw is queued.</summary>
    public bool IsRedrawQueued => _redrawQueued;

    public void Show() => Visible = true;

    public void Hide() => Visible = false;

    /// <summary>
    /// True when this item and every canvas-item ancestor up to the canvas are visible (and its canvas layer is).
    /// </summary>
    public bool IsVisibleInTree()
    {
        if (!IsInsideTree)
            return false;
        for (CanvasItem? n = this; n is not null; n = n.CanvasParent)
        {
            if (!n._visible)
                return false;
            if (n.CanvasParent is null)
                return n._rootOf?.Layer?.Visible ?? true;
        }

        return true;
    }

    /// <summary>The item's transform relative to its canvas parent (global for roots and top-level items).</summary>
    public abstract Transform2D GetTransform();

    /// <summary>The item's transform in canvas space (without the canvas/camera transform).</summary>
    public abstract Transform2D GetGlobalTransform();

    /// <summary>The canvas transform of this item's canvas (camera or layer transform).</summary>
    public Transform2D GetCanvasTransform() => GetCanvas()?.Transform ?? Transform2D.Identity;

    /// <summary>Global transform followed by the canvas transform: canvas space → viewport pixels (before stretch).</summary>
    public Transform2D GetGlobalTransformWithCanvas() => GetCanvasTransform() * GetGlobalTransform();

    /// <summary>The pointer in canvas (world) coordinates (Godot's <c>get_global_mouse_position</c>).</summary>
    public Vector2 GetGlobalMousePosition() =>
        GetCanvasTransform().AffineInverse().TransformPoint(GetViewport()?.GetMousePosition() ?? Input.MousePosition);

    /// <summary>The pointer in this item's local coordinates (Godot's <c>get_local_mouse_position</c>).</summary>
    public Vector2 GetLocalMousePosition() => GetGlobalTransform().AffineInverse().TransformPoint(GetGlobalMousePosition());

    /// <summary>The local bounds of what the last draw recorded.</summary>
    public Rect2 GetItemRect() => _drawList.Bounds;

    // ── Drawing ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Schedules <see cref="OnDraw"/> for the end of the frame (Godot's <c>queue_redraw</c>).</summary>
    public void QueueRedraw()
    {
        if (_redrawQueued || !IsInsideTree)
            return;
        _redrawQueued = true;
        Tree!.QueueCanvasRedraw(this);
    }

    /// <summary>Records this item's commands (Godot's <c>_draw</c>); only valid while drawing.</summary>
    protected virtual void OnDraw()
    {
    }

    /// <summary>Built-in drawing of engine node types (Sprite2D draws its texture here), before <see cref="OnDraw"/>.</summary>
    private protected virtual void DrawBuiltin()
    {
    }

    /// <summary>Runs the queued draw now: clears the commands, then the built-in draw, the signal and <see cref="OnDraw"/>.</summary>
    internal void RunDraw()
    {
        _redrawQueued = false;
        _drawList.Clear();
        if (!IsInsideTree)
            return;
        _drawing = true;
        try
        {
            DrawBuiltin();
            // Edit mode (the editor): a script type's own drawing does not run unless it is [Tool] (Godot's editor rule).
            if (!SkipsScriptCallbacks)
            {
                Draw?.Invoke();
                OnDraw();
            }
        }
        finally
        {
            _drawing = false;
            _drawList.DrawTransform = Transform2D.Identity;
        }
    }

    private void EnsureDrawing(string method)
    {
        if (!_drawing)
            throw new InvalidOperationException($"{method} is only valid inside OnDraw (or a Draw signal handler); call QueueRedraw() to redraw.");
    }

    /// <summary>Sets the transform of the commands drawn after it (Godot's <c>draw_set_transform</c>).</summary>
    public void DrawSetTransform(Vector2 position, float rotation = 0f, Vector2? scale = null)
    {
        EnsureDrawing(nameof(DrawSetTransform));
        _drawList.DrawTransform = Transform2D.FromTrs(position, rotation, scale ?? Vector2.One);
    }

    /// <summary>Sets the transform of the commands drawn after it (Godot's <c>draw_set_transform_matrix</c>).</summary>
    public void DrawSetTransformMatrix(Transform2D transform)
    {
        EnsureDrawing(nameof(DrawSetTransformMatrix));
        _drawList.DrawTransform = transform;
    }

    /// <summary>A filled or outlined rectangle (Godot's <c>draw_rect</c>; width −1 = a thin line outline).</summary>
    public void DrawRect(Rect2 rect, Vector4 color, bool filled = true, float width = -1f, bool antialiased = false)
    {
        EnsureDrawing(nameof(DrawRect));
        CanvasPrimitives.Rect(_drawList, rect, color, filled, width, antialiased);
    }

    /// <summary>A filled or outlined circle (Godot's <c>draw_circle</c>: 64 segments).</summary>
    public void DrawCircle(Vector2 position, float radius, Vector4 color, bool filled = true, float width = -1f, bool antialiased = false)
    {
        EnsureDrawing(nameof(DrawCircle));
        CanvasPrimitives.Ellipse(_drawList, position, radius, radius, color, filled, width, antialiased);
    }

    /// <summary>A line (Godot's <c>draw_line</c>; width −1 = one pixel regardless of scale).</summary>
    public void DrawLine(Vector2 from, Vector2 to, Vector4 color, float width = -1f, bool antialiased = false)
    {
        EnsureDrawing(nameof(DrawLine));
        CanvasPrimitives.Line(_drawList, from, to, color, width, antialiased);
    }

    /// <summary>Connected line segments (Godot's <c>draw_polyline</c>).</summary>
    public void DrawPolyline(ReadOnlySpan<Vector2> points, Vector4 color, float width = -1f, bool antialiased = false)
    {
        EnsureDrawing(nameof(DrawPolyline));
        CanvasPrimitives.Polyline(_drawList, points, [color], width, antialiased);
    }

    /// <summary>Connected line segments with a colour per point (Godot's <c>draw_polyline_colors</c>).</summary>
    public void DrawPolylineColors(ReadOnlySpan<Vector2> points, ReadOnlySpan<Vector4> colors, float width = -1f, bool antialiased = false)
    {
        EnsureDrawing(nameof(DrawPolylineColors));
        CanvasPrimitives.Polyline(_drawList, points, colors, width, antialiased);
    }

    /// <summary>An arc (Godot's <c>draw_arc</c>): <paramref name="pointCount"/> points from the start to the end angle.</summary>
    public void DrawArc(Vector2 center, float radius, float startAngle, float endAngle, int pointCount, Vector4 color, float width = -1f, bool antialiased = false)
    {
        EnsureDrawing(nameof(DrawArc));
        CanvasPrimitives.Arc(_drawList, center, radius, radius, startAngle, endAngle, pointCount, color, width, antialiased);
    }

    /// <summary>A filled polygon in one colour (Godot's <c>draw_colored_polygon</c>).</summary>
    public void DrawColoredPolygon(ReadOnlySpan<Vector2> points, Vector4 color, ReadOnlySpan<Vector2> uvs = default, Texture2D? texture = null)
    {
        EnsureDrawing(nameof(DrawColoredPolygon));
        CanvasPrimitives.Polygon(_drawList, points, [color], uvs, texture);
    }

    /// <summary>A filled polygon with a colour per point (Godot's <c>draw_polygon</c>).</summary>
    public void DrawPolygon(ReadOnlySpan<Vector2> points, ReadOnlySpan<Vector4> colors, ReadOnlySpan<Vector2> uvs = default, Texture2D? texture = null)
    {
        EnsureDrawing(nameof(DrawPolygon));
        CanvasPrimitives.Polygon(_drawList, points, colors, uvs, texture);
    }

    /// <summary>Triangles with explicit indices (Godot's <c>RenderingServer.canvas_item_add_triangle_array</c>).</summary>
    public void DrawTriangles(ReadOnlySpan<Vector2> points, ReadOnlySpan<int> indices, ReadOnlySpan<Vector4> colors, ReadOnlySpan<Vector2> uvs = default, Texture2D? texture = null)
    {
        EnsureDrawing(nameof(DrawTriangles));
        CanvasPrimitives.Triangles(_drawList, points, indices, colors, uvs, texture);
    }

    /// <summary>A texture at its size with its top-left corner at <paramref name="position"/> (Godot's <c>draw_texture</c>).</summary>
    public void DrawTexture(Texture2D texture, Vector2 position, Vector4? modulate = null)
    {
        EnsureDrawing(nameof(DrawTexture));
        ArgumentNullException.ThrowIfNull(texture);
        CanvasPrimitives.TextureRect(_drawList, texture, new Rect2(position, new Vector2(texture.Width, texture.Height)), modulate ?? White, false);
    }

    /// <summary>A texture stretched (or tiled) over a rectangle (Godot's <c>draw_texture_rect</c>).</summary>
    public void DrawTextureRect(Texture2D texture, Rect2 rect, bool tile = false, Vector4? modulate = null, bool transpose = false)
    {
        EnsureDrawing(nameof(DrawTextureRect));
        ArgumentNullException.ThrowIfNull(texture);
        CanvasPrimitives.TextureRect(_drawList, texture, rect, modulate ?? White, tile, transpose);
    }

    /// <summary>A region of a texture over a rectangle (Godot's <c>draw_texture_rect_region</c>).</summary>
    public void DrawTextureRectRegion(Texture2D texture, Rect2 rect, Rect2 sourceRect, Vector4? modulate = null, bool transpose = false, bool clipUv = true)
    {
        EnsureDrawing(nameof(DrawTextureRectRegion));
        ArgumentNullException.ThrowIfNull(texture);
        CanvasPrimitives.TextureRectRegion(_drawList, texture, rect, sourceRect, modulate ?? White, transpose);
    }

    /// <summary>
    /// One line of text with its baseline starting at <paramref name="position"/> (Godot's <c>draw_string</c>): aligned in
    /// <paramref name="width"/> when it is positive.
    /// </summary>
    public void DrawString(Font font, Vector2 position, string text, HorizontalAlignment alignment = HorizontalAlignment.Left,
        float width = -1f, int fontSize = 16, Vector4? modulate = null)
    {
        EnsureDrawing(nameof(DrawString));
        ArgumentNullException.ThrowIfNull(font);
        font.Draw(this, position, text, alignment, width, fontSize, 0, modulate ?? White);
    }

    /// <summary>The stroke around a line of text (Godot's <c>draw_string_outline</c>); draw the text over it.</summary>
    public void DrawStringOutline(Font font, Vector2 position, string text, HorizontalAlignment alignment = HorizontalAlignment.Left,
        float width = -1f, int fontSize = 16, int size = 1, Vector4? modulate = null)
    {
        EnsureDrawing(nameof(DrawStringOutline));
        ArgumentNullException.ThrowIfNull(font);
        if (size > 0)
            font.Draw(this, position, text, alignment, width, fontSize, size, modulate ?? White);
    }

    // ── Canvas registration ──────────────────────────────────────────────────────────────────────────────────

    protected override void OnEnterTree()
    {
        base.OnEnterTree();
        RefreshCanvasRegistration();
        QueueRedraw();
    }

    protected override void OnExitTree()
    {
        Unregister();
        _redrawQueued = false;
        base.OnExitTree();
    }

    private protected override void OnParentChanged()
    {
        base.OnParentChanged();
        if (IsInsideTree)
            RefreshCanvasRegistration();
    }

    /// <summary>Called when <see cref="TopLevel"/> changes, before re-registration (Node2D invalidates its global transform).</summary>
    private protected virtual void OnTopLevelChanged()
    {
    }

    private void RefreshCanvasRegistration()
    {
        if (!IsInsideTree)
            return;
        var canvas = CanvasParent is null ? FindCanvas() : null;
        if (ReferenceEquals(canvas, _rootOf))
            return;
        Unregister();
        if (canvas is not null)
        {
            _rootOf = canvas;
            canvas.AddRoot(this);
        }
    }

    private void Unregister()
    {
        _rootOf?.RemoveRoot(this);
        _rootOf = null;
    }

    /// <summary>The canvas this item draws on (its root ancestor's canvas).</summary>
    public Canvas? GetCanvas()
    {
        for (CanvasItem? n = this; n is not null; n = n.CanvasParent)
            if (n.CanvasParent is null)
                return n._rootOf ?? n.FindCanvas();
        return null;
    }

    /// <summary>The canvas of the nearest <see cref="CanvasLayer"/> ancestor in this viewport, else the viewport's root canvas.</summary>
    internal Canvas? FindCanvas()
    {
        for (var n = Parent; n is not null; n = n.Parent)
        {
            if (n is CanvasLayer layer)
                return layer.Canvas;
            if (n is SceneViewport viewport)
                return viewport.RootCanvas;
        }

        return GetViewport()?.RootCanvas;
    }
}

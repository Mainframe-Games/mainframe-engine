using System.Globalization;
using System.Numerics;
using System.Text;
using Silk.NET.Input;

namespace MainframeEngine.Editor;

/// <summary>
/// Tile map editing in a 2D tab (G2b, docs/design/tile-maps.md#editor): while a <see cref="TileMapLayer"/> with a
/// <see cref="TileSet"/> is the selection, the paint bar shows over the view and the viewport's left and right buttons
/// paint with the active tool (paint, line, rect, bucket, picker, eraser; select with cut/copy/paste), Shift draws a
/// line and Ctrl/Cmd+Shift a rectangle, Ctrl/Cmd+click picks and a right drag erases. A ghost of the brush follows the
/// mouse (<see cref="TilePaintPreview"/>). Every stroke is one <see cref="PaintTilesAction"/>. The TileMap and TileSet
/// bottom tabs (<see cref="TileMapPanel"/>) pick the brush and edit the tile set.
/// </summary>
public sealed class TileMapEditor
{
    private static readonly Vector4 GridColor = new(1f, 1f, 1f, 0.12f);
    private static readonly Vector4 ChunkColor = new(1f, 0.75f, 0.25f, 0.35f);
    private static readonly Vector4 EraseColor = new(1f, 0.3f, 0.25f, 0.9f);
    private static readonly Vector4 SelectColor = new(0.35f, 0.65f, 1f, 1f);
    private static readonly Vector4 HoverColor = new(1f, 1f, 1f, 0.5f);
    private static readonly Vector4 Dimmed = new(1f, 1f, 1f, 0.3f);

    private readonly EditorWorkspace _workspace;
    private readonly Dictionary<Vector2I, TileCell> _before = [];   // the stroke's cells as they were (undo)
    private readonly Dictionary<Vector2I, TileCell> _pending = [];  // what one input step writes
    private readonly List<TileMapLayer> _dimmed = [];
    private TilePaintPreview? _preview;
    private TilePaintTool _tool = TilePaintTool.Paint;
    private Stroke _stroke;
    private bool _strokeErase;
    private TileMapLayer? _strokeLayer;
    private Vector2I _anchor;
    private Vector2I _last;
    private Vector2I? _hover;
    private Rect2I? _cellSelection;
    private bool _brushFromClipboard;
    private TileMapLayer? _activeLayer;
    private bool _highlight;

    private enum Stroke
    {
        None,
        Free,    // paint or erase under the mouse, live
        Line,    // previewed, applied on release
        Rect,
        Bucket,  // applied on press, committed on release
        Select,
    }

    public TileMapEditor(EditorWorkspace workspace)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
    }

    /// <summary>Changes whenever the bar's or palette's state changes (the UI rebuilds on it).</summary>
    public int Version { get; private set; }

    public TilePaintTool Tool
    {
        get => _tool;
        set
        {
            if (_tool == value)
                return;
            CancelStroke();
            _tool = value;
            Touch();
        }
    }

    /// <summary>The cells the paint tools put down.</summary>
    public TileBrush Brush { get; } = new();

    /// <summary>The fraction of cells a random paint leaves untouched (0…1).</summary>
    public float Scattering { get; set; }

    /// <summary>The bucket fills the connected region (on) or every equal cell in the used rectangle (off).</summary>
    public bool Contiguous { get; set; } = true;

    /// <summary>Shows the cell grid and chunk borders over the edited layer.</summary>
    public bool ShowGrid { get; set; } = true;

    /// <summary>Dims the other tile map layers while one is edited.</summary>
    public bool Highlight
    {
        get => _highlight;
        set
        {
            _highlight = value;
            RefreshHighlight();
            Touch();
        }
    }

    /// <summary>The random stream of random paints (tests seed it).</summary>
    public Random Rng { get; set; } = new();

    /// <summary>The palette's source id.</summary>
    public int SourceId { get; private set; }

    /// <summary>The palette's picked tiles (atlas coordinates, in pick order).</summary>
    public List<Vector2I> SelectedTiles { get; } = [];

    /// <summary>The palette's alternative (0 = the base tile).</summary>
    public int Alternative { get; private set; }

    /// <summary>The Select tool's rectangle of cells, or null.</summary>
    public Rect2I? CellSelection => _cellSelection;

    /// <summary>Cells copied by Copy or Cut, pasted by Paste (a pattern brush).</summary>
    public TileBrush? Clipboard { get; private set; }

    /// <summary>The layer being edited: the 2D tab's selected tile map layer with a tile set, or null.</summary>
    public TileMapLayer? Layer
    {
        get
        {
            var scene = _workspace.Session.Active;
            if (scene is null || !scene.Camera.Is2D || _workspace.Mode != EditorMode.Game || _workspace.Simulation.IsSimulating(scene))
                return null;
            return scene.Selection.Primary is TileMapLayer { TileSet: not null, IsInsideTree: true } layer && scene.IsEditable(layer) ? layer : null;
        }
    }

    /// <summary>True while the paint bar shows.</summary>
    public bool IsActive => Layer is not null;

    /// <summary>True when the mouse paints (the paint bar shows and a tool other than Select is active, or a stroke runs).</summary>
    public bool IsPainting => _stroke != Stroke.None;

    /// <summary>The tile set the TileSet tab edits: the edited layer's, else a TileSet resource file open in the inspector.</summary>
    public TileSet? EditedTileSet =>
        (_workspace.Session.Active?.Selection.Primary as TileMapLayer)?.TileSet ?? _workspace.Inspector?.InspectedResource?.Resource as TileSet;

    /// <summary>The history TileSet edits go to: the inspected resource file's, else the active scene's.</summary>
    public IInspectorContext? TileSetContext =>
        _workspace.Inspector?.InspectedResource is { Resource: TileSet } resource ? resource : _workspace.Session.Active;

    private void Touch() => Version++;

    // ── Palette ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Picks palette tiles: the brush becomes their pattern (atlas layout) with the current alternative.</summary>
    public void SelectTiles(int sourceId, IEnumerable<Vector2I> tiles, int alternative = 0)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        SourceId = sourceId;
        Alternative = alternative & TileSetAtlasSource.AlternativeMask;
        SelectedTiles.Clear();
        SelectedTiles.AddRange(tiles.Distinct());
        var random = Brush.Random;
        Brush.Set(SelectedTiles.Select(t => (t, new TileCell(sourceId, t, Alternative))));
        Brush.Random = random;
        _brushFromClipboard = false;
        if (_tool is TilePaintTool.Select or TilePaintTool.Picker or TilePaintTool.Eraser)
            _tool = TilePaintTool.Paint;
        Touch();
    }

    /// <summary>Ctrl/Cmd+click in the atlas: adds or removes one tile of the random set.</summary>
    public void ToggleTile(int sourceId, Vector2I tile)
    {
        var tiles = sourceId == SourceId ? new List<Vector2I>(SelectedTiles) : [];
        if (!tiles.Remove(tile))
            tiles.Add(tile);
        SelectTiles(sourceId, tiles, Alternative);
    }

    /// <summary>The picker: the brush becomes this cell (and the palette shows it).</summary>
    public void PickCell(TileCell cell)
    {
        if (cell.IsEmpty)
            return;
        SelectTiles(cell.SourceId, [cell.AtlasCoords], cell.BaseAlternative);
        Brush.Set([(Vector2I.Zero, cell)]); // keep the cell's flips
        Touch();
    }

    // ── Viewport input ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A mouse press over the view while <see cref="IsActive"/>: starts the tool's stroke (or the picker / the erase of a
    /// right drag). False when the press is not the paint tools' (middle button, Alt, or the Select tool off the layer).
    /// </summary>
    public bool Press(EditedScene scene, Vector2 world, MouseButton button, EditorModifiers modifiers)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (Layer is not { } layer || (modifiers & EditorModifiers.Alt) != 0 || button is not (MouseButton.Left or MouseButton.Right))
            return false;
        var cell = layer.LocalToMap(layer.ToLocal(world));
        var command = (modifiers & EditorModifiers.Command) != 0;
        var shift = (modifiers & EditorModifiers.Shift) != 0;
        if (button == MouseButton.Left && (command && !shift || _tool == TilePaintTool.Picker))
        {
            PickCell(layer.GetCell(cell));
            return true;
        }

        var erase = button == MouseButton.Right || _tool == TilePaintTool.Eraser;
        if (_tool == TilePaintTool.Select && button == MouseButton.Left)
        {
            Begin(layer, Stroke.Select, cell, erase: false);
            _cellSelection = new Rect2I(cell.X, cell.Y, 1, 1);
            Touch();
            return true;
        }

        if (_tool == TilePaintTool.Select && button == MouseButton.Right)
            return false; // the Select tool leaves the right button to panning
        if (!erase && Brush.IsEmpty)
            return true; // nothing picked yet: swallow the click (the palette says so)
        var kind = _tool switch
        {
            TilePaintTool.Line => Stroke.Line,
            TilePaintTool.Rect => Stroke.Rect,
            TilePaintTool.Bucket => Stroke.Bucket,
            _ => shift && command ? Stroke.Rect : shift ? Stroke.Line : Stroke.Free,
        };
        Begin(layer, kind, cell, erase);
        var options = Options(layer, erase);
        switch (kind)
        {
            case Stroke.Free:
                TilePaint.Stamp(cell, options, _pending);
                ApplyPending(layer);
                break;
            case Stroke.Bucket:
                TilePaint.Bucket(layer, cell, Contiguous, options, _pending);
                ApplyPending(layer);
                break;
            default:
                UpdateShapePreview(layer, cell);
                break;
        }

        return true;
    }

    /// <summary>The mouse moved (world position): extends the stroke, or moves the hover ghost.</summary>
    public void Move(Vector2 world)
    {
        var layer = _stroke != Stroke.None ? _strokeLayer : Layer;
        if (layer is null || !layer.IsInsideTree)
            return;
        var cell = layer.LocalToMap(layer.ToLocal(world));
        switch (_stroke)
        {
            case Stroke.Free when cell != _last:
                TilePaint.StampLine(_last, cell, Options(layer, _strokeErase), _pending);
                ApplyPending(layer);
                _last = cell;
                break;
            case Stroke.Line or Stroke.Rect when cell != _last:
                _last = cell;
                UpdateShapePreview(layer, cell);
                break;
            case Stroke.Select when cell != _last:
                _last = cell;
                _cellSelection = RectOf(_anchor, cell);
                Touch();
                break;
            case Stroke.None when _hover != cell:
                _hover = cell;
                UpdateHoverPreview(layer, cell);
                break;
        }
    }

    /// <summary>The stroke's button was released: applies a line or rectangle and commits the stroke (one undo entry).</summary>
    public void Release(Vector2 world)
    {
        if (_stroke == Stroke.None || _strokeLayer is not { } layer)
            return;
        var cell = layer.LocalToMap(layer.ToLocal(world));
        var options = Options(layer, _strokeErase);
        if (_stroke == Stroke.Line)
            TilePaint.StampLine(_anchor, cell, options, _pending);
        else if (_stroke == Stroke.Rect)
            TilePaint.Rect(_anchor, cell, options, _pending);
        if (_stroke is Stroke.Line or Stroke.Rect)
            ApplyPending(layer);
        var name = _stroke == Stroke.Select ? null
            : _strokeErase ? "Erase Tiles"
            : _stroke == Stroke.Bucket ? "Fill Tiles"
            : _brushFromClipboard ? "Paste Tiles"
            : "Paint Tiles";
        if (name is not null)
            Commit(layer, name);
        EndStroke();
        _hover = null;
        UpdateHoverPreview(layer, cell);
    }

    /// <summary>Esc or a right click during a stroke: every cell goes back.</summary>
    public void CancelStroke()
    {
        if (_stroke == Stroke.None)
            return;
        if (_strokeLayer is { } layer)
            foreach (var (coords, before) in _before)
                layer.SetCellData(coords, before);
        if (_stroke == Stroke.Select)
            _cellSelection = null;
        EndStroke();
        Touch();
    }

    private void Begin(TileMapLayer layer, Stroke kind, Vector2I cell, bool erase)
    {
        _stroke = kind;
        _strokeLayer = layer;
        _strokeErase = erase;
        _anchor = _last = cell;
        _before.Clear();
        _pending.Clear();
        _preview?.Clear();
    }

    private void EndStroke()
    {
        _stroke = Stroke.None;
        _strokeLayer = null;
        _before.Clear();
        _pending.Clear();
        _preview?.Clear();
    }

    private TilePaint.Options Options(TileMapLayer layer, bool erase) => new(Brush, erase, Brush.Random ? Scattering : 0f, Rng, layer.TileSet);

    // Writes the pending cells live, remembering each cell's first value for undo.
    private void ApplyPending(TileMapLayer layer)
    {
        foreach (var (coords, value) in _pending)
        {
            if (coords.X is < short.MinValue or > short.MaxValue || coords.Y is < short.MinValue or > short.MaxValue)
                continue;
            var old = layer.GetCell(coords);
            if (old == value)
                continue;
            _before.TryAdd(coords, old);
            layer.SetCellData(coords, value);
        }

        _pending.Clear();
    }

    private void Commit(TileMapLayer layer, string name)
    {
        if (_before.Count == 0 || _workspace.Session.Active is not { } scene)
            return;
        var cells = new List<(Vector2I, TileCell, TileCell)>(_before.Count);
        foreach (var (coords, before) in _before)
        {
            var after = layer.GetCell(coords);
            if (after != before)
                cells.Add((coords, before, after));
        }

        if (cells.Count > 0)
            scene.History.Commit(new PaintTilesAction(name, layer, cells), alreadyApplied: true);
    }

    private static Rect2I RectOf(Vector2I a, Vector2I b)
    {
        var min = new Vector2I(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y));
        return new Rect2I(min.X, min.Y, Math.Abs(a.X - b.X) + 1, Math.Abs(a.Y - b.Y) + 1);
    }

    // ── Preview ───────────────────────────────────────────────────────────────────────────────────────────────

    private TilePaintPreview Preview(TileMapLayer layer)
    {
        _preview ??= new TilePaintPreview();
        _preview.Attach(layer);
        return _preview;
    }

    private void UpdateShapePreview(TileMapLayer layer, Vector2I cell)
    {
        _pending.Clear();
        var options = Options(layer, _strokeErase);
        if (_stroke == Stroke.Line)
            TilePaint.StampLine(_anchor, cell, options, _pending);
        else
            TilePaint.Rect(_anchor, cell, options, _pending);
        Preview(layer).Show(_pending, _strokeErase);
        _pending.Clear();
    }

    private void UpdateHoverPreview(TileMapLayer layer, Vector2I cell)
    {
        if (_tool is TilePaintTool.Select or TilePaintTool.Picker or TilePaintTool.Bucket || Brush.IsEmpty && _tool != TilePaintTool.Eraser)
        {
            _preview?.Clear();
            return;
        }

        _pending.Clear();
        if (Brush.Random)
        {
            if (_tool != TilePaintTool.Eraser)
                _pending[cell] = Brush.Cells[0].Cell;
            else
                _pending[cell] = TileCell.Empty;
        }
        else
        {
            TilePaint.Stamp(cell, new TilePaint.Options(Brush, _tool == TilePaintTool.Eraser), _pending);
        }

        Preview(layer).Show(_pending, _tool == TilePaintTool.Eraser);
        _pending.Clear();
    }

    /// <summary>Per frame (from the viewport controller): the preview's layer, the highlight and the tool state when the selection changes.</summary>
    public void Tick()
    {
        var layer = Layer;
        if (!ReferenceEquals(layer, _activeLayer))
        {
            if (_stroke != Stroke.None && !ReferenceEquals(_strokeLayer, layer))
                CancelStroke();
            _activeLayer = layer;
            _cellSelection = null;
            _hover = null;
            if (layer is null)
                _preview?.Detach();
            RefreshHighlight();
            Touch();
        }

        if (layer is not null)
            _preview?.Sync(layer);
    }

    private void RefreshHighlight()
    {
        foreach (var other in _dimmed)
            other.EditorModulate = Vector4.One;
        _dimmed.Clear();
        if (!_highlight || _activeLayer is not { Parent: { } parent } active)
            return;
        foreach (var sibling in parent.Children)
            if (sibling is TileMapLayer other && !ReferenceEquals(other, active))
            {
                other.EditorModulate = Dimmed;
                _dimmed.Add(other);
            }
    }

    /// <summary>Drops every reference into the edited scenes (tab closed, code reload).</summary>
    public void Release()
    {
        CancelStroke();
        foreach (var other in _dimmed)
            other.EditorModulate = Vector4.One;
        _dimmed.Clear();
        _preview?.Dispose();
        _preview = null;
        _activeLayer = null;
    }

    /// <summary>The grid, chunk borders, the cell selection and the erase / hover outlines (debug lines of the 2D view).</summary>
    public void Draw(EditedScene scene, DebugLines lines, Vector2 viewPixels)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(lines);
        if (Layer is not { } layer)
            return;
        var transform = layer.GlobalTransform;
        var tile = layer.TileSize;
        var size = new Vector2(tile.X, tile.Y);
        var min = layer.ToLocal(scene.Camera.ScreenToWorld2D(Vector2.Zero, viewPixels));
        var max = layer.ToLocal(scene.Camera.ScreenToWorld2D(viewPixels, viewPixels));
        var a = layer.LocalToMap(Vector2.Min(min, max));
        var b = layer.LocalToMap(Vector2.Max(min, max)) + Vector2I.One;
        var quadrant = layer.RenderingQuadrantSize;
        var cellPixels = tile.X * scene.Camera.Zoom2D * transform.Scale.X;
        if (ShowGrid && (b.X - a.X) * (long)(b.Y - a.Y) < 1_000_000)
        {
            for (var x = a.X; x <= b.X; x++)
            {
                var chunk = x % quadrant == 0;
                if (!chunk && cellPixels < 6)
                    continue;
                lines.AddLine2D(transform, new Vector2(x * size.X, a.Y * size.Y), new Vector2(x * size.X, b.Y * size.Y), chunk ? ChunkColor : GridColor);
            }

            for (var y = a.Y; y <= b.Y; y++)
            {
                var chunk = y % quadrant == 0;
                if (!chunk && cellPixels < 6)
                    continue;
                lines.AddLine2D(transform, new Vector2(a.X * size.X, y * size.Y), new Vector2(b.X * size.X, y * size.Y), chunk ? ChunkColor : GridColor);
            }
        }

        if (_cellSelection is { } selection)
            Outline(lines, transform, size, selection, SelectColor);
        if (_preview is { } preview)
            foreach (var cell in preview.ErasedCells)
                Outline(lines, transform, size, new Rect2I(cell.X, cell.Y, 1, 1), EraseColor);
        if (_hover is { } hover && _stroke == Stroke.None && _tool is TilePaintTool.Bucket or TilePaintTool.Picker or TilePaintTool.Select)
            Outline(lines, transform, size, new Rect2I(hover.X, hover.Y, 1, 1), HoverColor);
    }

    private static void Outline(DebugLines lines, in Transform2D transform, Vector2 size, Rect2I r, Vector4 color)
    {
        var p0 = new Vector2(r.Position.X * size.X, r.Position.Y * size.Y);
        var p1 = new Vector2((r.Position.X + r.Size.X) * size.X, (r.Position.Y + r.Size.Y) * size.Y);
        Span<Vector2> corners = [p0, new(p1.X, p0.Y), p1, new(p0.X, p1.Y)];
        lines.AddPolygon2D(transform, corners, color);
    }

    // ── Commands ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Runs a <c>tilemap.*</c> command (the paint bar, the palette and the shortcuts); false when unknown.</summary>
    public bool RunCommand(string id)
    {
        switch (id)
        {
            case "tilemap.tool.select": Tool = TilePaintTool.Select; return true;
            case "tilemap.tool.paint": Tool = TilePaintTool.Paint; return true;
            case "tilemap.tool.line": Tool = TilePaintTool.Line; return true;
            case "tilemap.tool.rect": Tool = TilePaintTool.Rect; return true;
            case "tilemap.tool.bucket": Tool = TilePaintTool.Bucket; return true;
            case "tilemap.tool.picker": Tool = TilePaintTool.Picker; return true;
            case "tilemap.tool.eraser": Tool = TilePaintTool.Eraser; return true;
            case "tilemap.random":
                Brush.Random = !Brush.Random;
                Touch();
                return true;
            case "tilemap.scattering.more":
                Scattering = MathF.Min(1f, MathF.Round((Scattering + 0.1f) * 10f) / 10f);
                Touch();
                return true;
            case "tilemap.scattering.less":
                Scattering = MathF.Max(0f, MathF.Round((Scattering - 0.1f) * 10f) / 10f);
                Touch();
                return true;
            case "tilemap.flip_h": TransformBrush(TileBrushTransform.FlipH); return true;
            case "tilemap.flip_v": TransformBrush(TileBrushTransform.FlipV); return true;
            case "tilemap.rotate_right": TransformBrush(TileBrushTransform.RotateRight); return true;
            case "tilemap.rotate_left": TransformBrush(TileBrushTransform.RotateLeft); return true;
            case "tilemap.contiguous":
                Contiguous = !Contiguous;
                Touch();
                return true;
            case "tilemap.grid":
                ShowGrid = !ShowGrid;
                Touch();
                return true;
            case "tilemap.highlight": Highlight = !Highlight; return true;
            case "tilemap.layer.prev": SwitchLayer(-1); return true;
            case "tilemap.layer.next": SwitchLayer(1); return true;
            case "tilemap.copy": Copy(); return true;
            case "tilemap.cut":
                Copy();
                DeleteSelection("Cut Tiles");
                return true;
            case "tilemap.paste": Paste(); return true;
            case "tilemap.delete": DeleteSelection("Erase Tiles"); return true;
            case "tilemap.deselect":
                _cellSelection = null;
                Touch();
                return true;
            case "tilemap.edit_tileset":
                _workspace.ShowBottomTab(BottomDockTab.TileSet);
                return true;
            case "tilemap.palette":
                _workspace.ShowBottomTab(BottomDockTab.TileMap);
                return true;
            default:
                return false;
        }
    }

    private void TransformBrush(TileBrushTransform transform)
    {
        Brush.Transform(transform);
        if (_activeLayer is { } layer && _hover is { } hover)
            UpdateHoverPreview(layer, hover);
        Touch();
    }

    private void SwitchLayer(int delta)
    {
        if (_workspace.Session.Active is not { } scene || scene.Selection.Primary is not TileMapLayer { Parent: { } parent } current)
            return;
        var layers = parent.Children.OfType<TileMapLayer>().Where(scene.IsEditable).ToList();
        var index = layers.IndexOf(current);
        if (layers.Count < 2 || index < 0)
            return;
        scene.Selection.Set(layers[(index + delta + layers.Count) % layers.Count]);
        Touch();
    }

    private void Copy()
    {
        if (Layer is not { } layer || _cellSelection is not { } r)
            return;
        var cells = new List<(Vector2I, TileCell)>();
        for (var y = r.Position.Y; y < r.Position.Y + r.Size.Y; y++)
            for (var x = r.Position.X; x < r.Position.X + r.Size.X; x++)
                if (layer.GetCell(new Vector2I(x, y)) is { IsEmpty: false } cell)
                    cells.Add((new Vector2I(x, y) - r.Position, cell));
        if (cells.Count == 0)
            return;
        Clipboard = new TileBrush();
        Clipboard.Set(cells);
        Touch();
    }

    private void Paste()
    {
        if (Clipboard is not { IsEmpty: false } clipboard)
            return;
        Brush.Set(clipboard.Cells);
        Brush.Random = false;
        _brushFromClipboard = true;
        _tool = TilePaintTool.Paint;
        Touch();
    }

    private void DeleteSelection(string name)
    {
        if (Layer is not { } layer || _cellSelection is not { } r)
            return;
        _before.Clear();
        for (var y = r.Position.Y; y < r.Position.Y + r.Size.Y; y++)
            for (var x = r.Position.X; x < r.Position.X + r.Size.X; x++)
                _pending[new Vector2I(x, y)] = TileCell.Empty;
        ApplyPending(layer);
        Commit(layer, name);
        _before.Clear();
        Touch();
    }

    /// <summary>
    /// The tile map's command for a key while the paint bar shows, or null (the key goes to the usual shortcuts). Godot's
    /// tile keys: S select, D paint, L line, R rect, B bucket, P picker, E eraser, C/V flip, X/Z rotate. E and R are also
    /// the gizmo's Rotate/Scale keys: the tile keys win only while a paint tool other than Select is active (as in
    /// Godot's editor); Q (gizmo Select) always leaves painting. With a cell selection: Delete, Esc, Cmd+C/X/V.
    /// </summary>
    public static string? ShortcutFor(Key key, EditorModifiers modifiers, TilePaintTool tool, bool hasCellSelection, bool hasClipboard)
    {
        var command = (modifiers & EditorModifiers.Command) != 0;
        if ((modifiers & EditorModifiers.Alt) != 0)
            return null;
        if (command)
        {
            return key switch
            {
                Key.C when hasCellSelection => "tilemap.copy",
                Key.X when hasCellSelection => "tilemap.cut",
                Key.V when hasClipboard => "tilemap.paste",
                _ => null,
            };
        }

        var painting = tool != TilePaintTool.Select;
        return key switch
        {
            Key.S => "tilemap.tool.select",
            Key.D => "tilemap.tool.paint",
            Key.L => "tilemap.tool.line",
            Key.B => "tilemap.tool.bucket",
            Key.P => "tilemap.tool.picker",
            Key.R when painting => "tilemap.tool.rect",
            Key.E when painting => "tilemap.tool.eraser",
            Key.C => "tilemap.flip_h",
            Key.V => "tilemap.flip_v",
            Key.X => "tilemap.rotate_right",
            Key.Z => "tilemap.rotate_left",
            Key.Q when painting => "tilemap.tool.select",
            Key.Delete or Key.Backspace when hasCellSelection => "tilemap.delete",
            Key.Escape when hasCellSelection => "tilemap.deselect",
            _ => null,
        };
    }

    // ── Paint bar ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The paint bar's markup: icon buttons with tooltips ("Title (Shortcut) — what it does").</summary>
    public string PaintBarRml()
    {
        var rml = new StringBuilder(4096);
        Button(rml, "tilemap.tool.select", "pointer", _tool == TilePaintTool.Select, "Select (S) — drag a rectangle of cells; Cmd/Ctrl+C, X, V, Delete act on it; Esc deselects");
        Button(rml, "tilemap.tool.paint", "pencil", _tool == TilePaintTool.Paint, "Paint (D) — paint the palette's tiles; Shift+drag draws a line, Cmd/Ctrl+Shift+drag a rectangle; right drag erases");
        Button(rml, "tilemap.tool.line", "line", _tool == TilePaintTool.Line, "Line (L) — drag a line of cells");
        Button(rml, "tilemap.tool.rect", "square", _tool == TilePaintTool.Rect, "Rect (R) — drag a filled rectangle");
        Button(rml, "tilemap.tool.bucket", "bucket-droplet", _tool == TilePaintTool.Bucket, "Bucket (B) — flood fill the region of equal cells");
        Button(rml, "tilemap.tool.picker", "color-picker", _tool == TilePaintTool.Picker, "Picker (P) — click a cell to pick its tile; Cmd/Ctrl+click picks from any tool");
        Button(rml, "tilemap.tool.eraser", "eraser", _tool == TilePaintTool.Eraser, "Eraser (E) — erase with the tool's shape; a right drag erases from any tool");
        rml.Append("<span class=\"paint-sep\"></span>");
        Button(rml, "tilemap.random", "dice-5", Brush.Random, "Random Tile — paint one of the picked tiles at random, weighted by their probability");
        if (Brush.Random)
        {
            Button(rml, "tilemap.scattering.less", "minus", false, "Less Scattering — fewer cells left empty");
            rml.Append("<span class=\"paint-value mono\" data-tooltip=\"Scattering — the fraction of cells a random paint leaves untouched\">")
                .Append(Scattering.ToString("0.0", CultureInfo.InvariantCulture)).Append("</span>");
            Button(rml, "tilemap.scattering.more", "plus", false, "More Scattering — more cells left empty");
        }

        if (_tool == TilePaintTool.Bucket)
            Button(rml, "tilemap.contiguous", "link", Contiguous, "Contiguous — fill only the connected region (off: every equal cell of the layer)");
        rml.Append("<span class=\"paint-sep\"></span>");
        Button(rml, "tilemap.flip_h", "flip-vertical", false, "Flip Horizontally (C) — mirror the brush left to right");
        Button(rml, "tilemap.flip_v", "flip-horizontal", false, "Flip Vertically (V) — mirror the brush top to bottom");
        Button(rml, "tilemap.rotate_left", "rotate", false, "Rotate Left (Z) — turn the brush a quarter counter-clockwise");
        Button(rml, "tilemap.rotate_right", "rotate-clockwise", false, "Rotate Right (X) — turn the brush a quarter clockwise");
        rml.Append("<span class=\"paint-sep\"></span>");
        Button(rml, "tilemap.layer.prev", "chevron-left", false, "Previous Layer — edit the sibling TileMapLayer before this one");
        rml.Append("<span class=\"paint-chip\" data-tooltip=\"The edited layer\"><span class=\"icon icon-sm icon-stack-2\"></span><span>")
            .Append(RmlText.Escape(_activeLayer?.Name ?? Layer?.Name ?? "")).Append("</span></span>");
        Button(rml, "tilemap.layer.next", "chevron-right", false, "Next Layer — edit the sibling TileMapLayer after this one");
        Button(rml, "tilemap.highlight", "highlight", _highlight, "Highlight — dim the other layers while this one is edited");
        Button(rml, "tilemap.grid", "grid-3x3", ShowGrid, "Grid — show the cell grid and chunk borders");
        rml.Append("<span class=\"paint-sep\"></span>");
        Button(rml, "tilemap.palette", "layout-grid", false, "TileMap Palette — pick tiles in the bottom panel's TileMap tab");
        Button(rml, "tilemap.edit_tileset", "adjustments", false, "Edit TileSet — sources, tiles, tile data and collision in the TileSet tab");
        return rml.ToString();
    }

    private static void Button(StringBuilder rml, string command, string icon, bool active, string tooltip) =>
        rml.Append("<button class=\"tool-button").Append(active ? " active" : "").Append("\" data-command=\"").Append(command)
            .Append("\" data-tooltip=\"").Append(RmlText.Escape(tooltip)).Append("\"><span class=\"icon icon-").Append(icon).Append("\"></span></button>");
}

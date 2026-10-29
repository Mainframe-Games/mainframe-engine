using System.Globalization;
using System.Numerics;
using System.Text;
using MainframeEngine.UI.Rml;

namespace MainframeEngine.Editor;

/// <summary>What the TileSet tab's atlas clicks do.</summary>
public enum TileSetEditMode
{
    /// <summary>The grid fields; a click creates or removes a tile.</summary>
    Setup,

    /// <summary>A click selects a tile; its data is edited beside the atlas.</summary>
    Select,

    /// <summary>A click selects a tile; its collision polygons are edited beside the atlas.</summary>
    Collision,
}

/// <summary>
/// The bottom dock's TileMap and TileSet tabs (G2b, <c>tilemap.rml</c>, ADR 0226). <b>TileMap</b>: the source list, the
/// atlas (created tiles bright, others dimmed; click picks, drag picks a rectangle pattern, Ctrl/Cmd+click toggles a
/// tile of the random set; zoom buttons), the selected tile's alternatives and Edit TileSet. <b>TileSet</b>: add a source
/// from an image (asking whether to create tiles in its opaque regions), remove it, change its id; Setup (margins,
/// separation, region size, tile size; a click creates or removes a tile), Select (texture origin, modulate, z index,
/// y-sort origin, probability and every custom data layer) and Collision (per physics layer: full tile, new polygon,
/// clear, snap ½ / ¼ / 1 px, points placed and moved by clicking snap points, Delete removes the selected point). Every
/// TileSet edit is one undo entry (<see cref="TileSetEditing"/>).
/// </summary>
public sealed class TileMapPanel : EditorDocument
{
    private const float TileViewSize = 112f; // dp, the collision editor's tile

    private readonly List<RmlEventListener> _listeners = [];
    private int _shownVersion = -1;
    private int _ownVersion;
    private float _zoom = 4f;
    private bool _dragging;
    private Vector2I _dragAnchor;
    private TileCollisionPolygon? _drawingPolygon;

    public TileMapPanel(EditorWorkspace workspace)
        : base(workspace, "tilemap.rml")
    {
    }

    /// <summary>Which tab the panel shows (the workspace's bottom tab: TileMap or TileSet).</summary>
    public bool ShowsTileSet => Workspace.BottomTab == BottomDockTab.TileSet;

    public TileSetEditMode Mode { get; private set; } = TileSetEditMode.Setup;

    /// <summary>The TileSet tab's source id and tile.</summary>
    public int EditedSourceId { get; private set; }

    public Vector2I? EditedTile { get; private set; }

    /// <summary>The collision editor's physics layer.</summary>
    public int PhysicsLayer { get; private set; }

    /// <summary>The collision snap step in tile pixels (0.5 tile, 0.25 tile or 1 px).</summary>
    public float SnapStep { get; private set; } = 4f;

    /// <summary>The polygon and point the collision editor has selected (-1: none).</summary>
    public (int Polygon, int Point) SelectedPoint { get; private set; } = (-1, -1);

    /// <summary>True while a new polygon is being placed point by point.</summary>
    public bool Drawing { get; private set; }

    private TileMapEditor Editor => Workspace.TileMap;

    private TileSet? Set => ShowsTileSet ? Editor.EditedTileSet : Editor.Layer?.TileSet ?? Editor.EditedTileSet;

    private void Touch() => _ownVersion++;

    /// <summary>Per frame: re-renders when the tile set, the paint state or the panel's own state changed.</summary>
    public void Tick()
    {
        if (!Visible || !IsLoaded)
            return;
        var set = Set;
        var version = HashCode.Combine(_ownVersion, Editor.Version, set?.Version ?? 0, set, ShowsTileSet, Workspace.Session.Active?.Selection.Version ?? 0);
        if (version == _shownVersion)
            return;
        _shownVersion = version;
        Render();
    }

    protected override void OnAttach(RmlDocument document)
    {
        foreach (var listener in _listeners)
            listener.Remove();
        _listeners.Clear();
        var root = document.AsElement();
        Add(root.AddEventListener("mousedown", OnMouseDown));
        Add(root.AddEventListener("mouseover", OnMouseOver));
        Add(root.AddEventListener("mouseup", OnMouseUp));
        Add(root.AddEventListener("change", e =>
        {
            if (e.GetParameter("linebreak", false))
                CommitField(e.Target);
        }));
        Add(root.AddEventListener("blur", e => CommitField(e.Target), inCapturePhase: true));
        Add(root.AddEventListener("keydown", OnKeyDown));
        _shownVersion = -1;

        void Add(RmlEventListener? listener)
        {
            if (listener is not null)
                _listeners.Add(listener);
        }
    }

    // ── Input ─────────────────────────────────────────────────────────────────────────────────────────────────

    protected override void OnClickElement(RmlEvent e)
    {
        if (FindAttribute(e.Target, "data-tab") is { } tab && int.TryParse(tab, CultureInfo.InvariantCulture, out var index))
        {
            Workspace.ShowBottomTab((BottomDockTab)index);
            return;
        }

        if (FindAttribute(e.Target, "data-source") is { } source && int.TryParse(source, CultureInfo.InvariantCulture, out var id))
        {
            SelectSource(id);
            return;
        }

        if (FindAttribute(e.Target, "data-alt") is { } alt && int.TryParse(alt, CultureInfo.InvariantCulture, out var alternative))
        {
            Editor.SelectTiles(Editor.SourceId, Editor.SelectedTiles.Count > 0 ? [Editor.SelectedTiles[0]] : [], alternative);
            return;
        }

        if (FindAttribute(e.Target, "data-point") is { } point && TryPair(point, out var p))
        {
            SelectedPoint = SelectedPoint == (p.X, p.Y) ? (-1, -1) : (p.X, p.Y);
            Drawing = false;
            Touch();
            return;
        }

        if (FindAttribute(e.Target, "data-snap") is { } snap && TryPair(snap, out var s))
        {
            ClickSnap(new Vector2(s.X, s.Y) * (SnapStep / 2f)); // snap points are stored in half steps
            return;
        }

        if (FindAttribute(e.Target, "data-op") is { } op)
            RunOp(op);
    }

    private void OnMouseDown(RmlEvent e)
    {
        if (e.GetParameter("button", 0) != 0 || FindAttribute(e.Target, "data-cell") is not { } text || !TryPair(text, out var cell))
            return;
        var command = e.GetParameter("ctrl_key", 0) != 0 || e.GetParameter("meta_key", 0) != 0;
        var set = Set;
        if (ShowsTileSet)
        {
            if (set?.GetSource(EditedSourceId) is not { } source)
                return;
            if (Mode == TileSetEditMode.Setup)
            {
                EditTileSet("Toggle Tile", _ => TileSetEditing.ToggleTile(source, cell));
                return;
            }

            if (source.HasTile(cell))
            {
                EditedTile = cell;
                SelectedPoint = (-1, -1);
                Drawing = false;
                Touch();
            }

            return;
        }

        if (set?.GetSource(Editor.SourceId) is not { } palette || !palette.HasTile(cell))
            return;
        if (command)
        {
            Editor.ToggleTile(Editor.SourceId, cell);
            return;
        }

        _dragging = true;
        _dragAnchor = cell;
        Editor.SelectTiles(Editor.SourceId, [cell], Editor.Alternative);
    }

    private void OnMouseOver(RmlEvent e)
    {
        if (!_dragging || ShowsTileSet || FindAttribute(e.Target, "data-cell") is not { } text || !TryPair(text, out var cell))
            return;
        if (Set?.GetSource(Editor.SourceId) is not { } source)
            return;
        // A rectangle pattern: every created tile between the anchor and this cell, at its atlas offset.
        var tiles = new List<Vector2I>();
        for (var y = Math.Min(_dragAnchor.Y, cell.Y); y <= Math.Max(_dragAnchor.Y, cell.Y); y++)
            for (var x = Math.Min(_dragAnchor.X, cell.X); x <= Math.Max(_dragAnchor.X, cell.X); x++)
                if (source.HasTile(new Vector2I(x, y)))
                    tiles.Add(new Vector2I(x, y));
        if (tiles.Count > 0)
            Editor.SelectTiles(Editor.SourceId, tiles, Editor.Alternative);
    }

    private void OnMouseUp(RmlEvent e) => _dragging = false;

    private void OnKeyDown(RmlEvent e)
    {
        var key = (RmlKey)e.GetParameter("key_identifier", 0);
        if (key is RmlKey.Delete or RmlKey.Back && ShowsTileSet && Mode == TileSetEditMode.Collision && e.Target.TagName != "input")
            DeleteSelectedPoint();
    }

    private void CommitField(RmlElement target)
    {
        if (target.GetAttribute("data-field") is not { } field)
            return;
        var text = target.GetAttribute("value") ?? "";
        Span<char> buffer = stackalloc char[256];
        var n = target.GetValue(buffer);
        if (n >= 0 && n <= buffer.Length)
            text = new string(buffer[..n]);
        ApplyField(field, text.Trim());
    }

    // ── Operations (public for tests and QA scripts) ──────────────────────────────────────────────────────────

    /// <summary>Selects the palette's (TileMap tab) or the TileSet tab's source.</summary>
    public void SelectSource(int id)
    {
        if (ShowsTileSet)
        {
            EditedSourceId = id;
            EditedTile = null;
            SelectedPoint = (-1, -1);
        }
        else
        {
            Editor.SelectTiles(id, []);
        }

        Touch();
    }

    public void SetMode(TileSetEditMode mode)
    {
        Mode = mode;
        Drawing = false;
        _drawingPolygon = null;
        SelectedPoint = (-1, -1);
        Touch();
    }

    /// <summary>Selects the TileSet tab's tile (Select and Collision modes).</summary>
    public void SelectTile(int sourceId, Vector2I tile)
    {
        EditedSourceId = sourceId;
        EditedTile = tile;
        SelectedPoint = (-1, -1);
        Touch();
    }

    /// <summary>Runs a panel operation (<c>data-op</c>).</summary>
    public void RunOp(string op)
    {
        var set = Set;
        var source = set?.GetSource(ShowsTileSet ? EditedSourceId : Editor.SourceId);
        switch (op)
        {
            case "zoom-in":
                _zoom = MathF.Min(16f, _zoom * 2f);
                break;
            case "zoom-out":
                _zoom = MathF.Max(0.25f, _zoom / 2f);
                break;
            case "mode-setup":
                SetMode(TileSetEditMode.Setup);
                return;
            case "mode-select":
                SetMode(TileSetEditMode.Select);
                return;
            case "mode-collision":
                SetMode(TileSetEditMode.Collision);
                return;
            case "add-source":
                PickSourceImage();
                return;
            case "remove-source" when set is not null && source is not null:
                EditTileSet("Remove Atlas Source", s => s.RemoveSource(source.Id));
                EditedSourceId = set.Sources.FirstOrDefault()?.Id ?? 0;
                break;
            case "opaque" when source is not null:
                EditTileSet("Create Tiles in Opaque Regions", _ => source.CreateTilesFromOpaqueRegions());
                break;
            case "add-physics" when set is not null:
                EditTileSet("Add Physics Layer", s => s.AddPhysicsLayer());
                PhysicsLayer = set.PhysicsLayers.Count - 1;
                break;
            case "add-custom" when set is not null:
                PromptCustomLayer();
                return;
            case "physics-prev" when set is not null:
                PhysicsLayer = Math.Max(0, PhysicsLayer - 1);
                break;
            case "physics-next" when set is not null:
                PhysicsLayer = Math.Min(Math.Max(0, set.PhysicsLayers.Count - 1), PhysicsLayer + 1);
                break;
            case "full-tile" when set is not null && source is not null && EditedTile is { } full:
                EnsurePhysicsLayer(set);
                EditTileSet("Full Tile Collision", s => TileSetEditing.SetFullTile(s, source, full, PhysicsLayer));
                break;
            case "add-polygon" when EditedTile is not null:
                Drawing = true;
                _drawingPolygon = null;
                SelectedPoint = (-1, -1);
                break;
            case "clear-collision" when source is not null && EditedTile is { } clear:
                EditTileSet("Clear Collision", _ => TileSetEditing.ClearCollision(source, clear, PhysicsLayer));
                SelectedPoint = (-1, -1);
                break;
            case "snap":
                SnapStep = SnapStep switch { 8f => 4f, 4f => 1f, _ => 8f };
                break;
            case "delete-point":
                DeleteSelectedPoint();
                return;
            case "new-alternative" when source is not null && Editor.SelectedTiles.Count > 0 && source.HasTile(Editor.SelectedTiles[0]):
                {
                    var tile = Editor.SelectedTiles[0];
                    var created = 0;
                    EditTileSet("New Alternative Tile", _ => created = source.CreateAlternativeTile(tile));
                    Editor.SelectTiles(source.Id, [tile], created);
                    break;
                }

            default:
                return;
        }

        Touch();
    }

    /// <summary>Applies a field (<c>data-field</c>): grid, tile set and the selected tile's data.</summary>
    public void ApplyField(string field, string text)
    {
        var set = Set;
        if (set is null)
            return;
        var source = set.GetSource(EditedSourceId);
        var inv = CultureInfo.InvariantCulture;
        int.TryParse(text, NumberStyles.Integer, inv, out var number);
        switch (field)
        {
            case "tile-x": EditTileSet("Set Tile Size", s => s.TileSize = new Vector2I(number, s.TileSize.Y)); break;
            case "tile-y": EditTileSet("Set Tile Size", s => s.TileSize = new Vector2I(s.TileSize.X, number)); break;
            case "source-id" when source is not null && number >= 0 && !set.HasSource(number):
                EditTileSet("Set Source Id", _ => source.Id = number);
                EditedSourceId = number;
                break;
            case "margin-x" when source is not null: EditTileSet("Set Margins", _ => source.Margins = new Vector2I(number, source.Margins.Y)); break;
            case "margin-y" when source is not null: EditTileSet("Set Margins", _ => source.Margins = new Vector2I(source.Margins.X, number)); break;
            case "sep-x" when source is not null: EditTileSet("Set Separation", _ => source.Separation = new Vector2I(number, source.Separation.Y)); break;
            case "sep-y" when source is not null: EditTileSet("Set Separation", _ => source.Separation = new Vector2I(source.Separation.X, number)); break;
            case "size-x" when source is not null: EditTileSet("Set Region Size", _ => source.TextureRegionSize = new Vector2I(number, source.TextureRegionSize.Y)); break;
            case "size-y" when source is not null: EditTileSet("Set Region Size", _ => source.TextureRegionSize = new Vector2I(source.TextureRegionSize.X, number)); break;
            default:
                if (source is not null && EditedTile is { } tile)
                    ApplyTileField(source, tile, field, text);
                break;
        }

        Touch();
    }

    private void ApplyTileField(TileSetAtlasSource source, Vector2I tile, string field, string text)
    {
        var inv = CultureInfo.InvariantCulture;
        int.TryParse(text, NumberStyles.Integer, inv, out var number);
        float.TryParse(text, NumberStyles.Float, inv, out var real);
        switch (field)
        {
            case "origin-x": EditTile("Set Texture Origin", d => d.TextureOrigin = new Vector2I(number, d.TextureOrigin.Y)); break;
            case "origin-y": EditTile("Set Texture Origin", d => d.TextureOrigin = new Vector2I(d.TextureOrigin.X, number)); break;
            case "z": EditTile("Set Z Index", d => d.ZIndex = number); break;
            case "ysort": EditTile("Set Y Sort Origin", d => d.YSortOrigin = number); break;
            case "probability": EditTile("Set Probability", d => d.Probability = real); break;
            case "modulate":
                {
                    Span<float> c = [1, 1, 1, 1];
                    var i = 0;
                    foreach (var range in text.AsSpan().Split(','))
                        if (i < 4 && float.TryParse(text.AsSpan()[range].Trim(), NumberStyles.Float, inv, out var v))
                            c[i++] = v;
                    var modulate = new Vector4(c[0], c[1], c[2], c[3]);
                    EditTile("Set Modulate", d => d.Modulate = modulate);
                    break;
                }

            default:
                if (field.StartsWith("custom-", StringComparison.Ordinal) && int.TryParse(field.AsSpan(7), NumberStyles.Integer, inv, out var layer))
                    EditTile("Set Custom Data", d => d.SetCustomData(layer, text));
                break;
        }

        void EditTile(string name, Action<TileData> change) => EditTileSet(name, _ => change(source.GetOrCreateTileData(tile)));
    }

    private void ClickSnap(Vector2 point)
    {
        if (Set is not { } set || set.GetSource(EditedSourceId) is not { } source || EditedTile is not { } tile)
            return;
        point = TileSetEditing.Snap(point, set.TileSize, 0f);
        EnsurePhysicsLayer(set);
        var data = source.GetTileData(tile);
        var polygons = data.Collision.Where(p => p.PhysicsLayer == PhysicsLayer).ToList();
        var (pi, pt) = SelectedPoint;
        if (pi >= 0 && pi < polygons.Count && pt >= 0 && pt < polygons[pi].Points.Length)
        {
            var target = polygons[pi];
            EditTileSet("Move Collision Point", _ =>
            {
                var points = (Vector2[])target.Points.Clone();
                points[pt] = point;
                target.Points = points;
            });
            SelectedPoint = (-1, -1);
        }
        else if (Drawing)
        {
            if (_drawingPolygon is { } target && source.GetTileData(tile).Collision.Contains(target))
            {
                EditTileSet("Add Collision Point", _ => target.Points = [.. target.Points, point]);
            }
            else
            {
                // The first point of a new polygon; the next clicks add to it.
                EditTileSet("Add Collision Polygon", _ => _drawingPolygon = source.GetOrCreateTileData(tile).AddCollisionPolygon(PhysicsLayer, [point]));
            }
        }

        Touch();
    }

    /// <summary>Removes the selected collision point (a polygon left with fewer than three points is removed).</summary>
    public void DeleteSelectedPoint()
    {
        if (Set is not { } set || set.GetSource(EditedSourceId) is not { } source || EditedTile is not { } tile || SelectedPoint.Polygon < 0)
            return;
        var polygons = source.GetTileData(tile).Collision.Where(p => p.PhysicsLayer == PhysicsLayer).ToList();
        var (pi, pt) = SelectedPoint;
        if (pi >= polygons.Count || pt >= polygons[pi].Points.Length)
            return;
        var target = polygons[pi];
        EditTileSet("Remove Collision Point", _ =>
        {
            var data = source.GetOrCreateTileData(tile);
            if (target.Points.Length <= 3)
                data.Collision = [.. data.Collision.Where(p => !ReferenceEquals(p, target))];
            else
                target.Points = [.. target.Points.Where((_, i) => i != pt)];
        });
        SelectedPoint = (-1, -1);
        Touch();
    }

    // Collision needs a physics layer: the first edit adds one.
    private void EnsurePhysicsLayer(TileSet set)
    {
        if (set.PhysicsLayers.Count == 0)
            EditTileSet("Add Physics Layer", s => s.AddPhysicsLayer());
        PhysicsLayer = Math.Clamp(PhysicsLayer, 0, Math.Max(0, set.PhysicsLayers.Count - 1));
    }

    private bool EditTileSet(string name, Action<TileSet> change)
    {
        if (Set is not { } set || Editor.TileSetContext is not { } context)
            return false;
        return TileSetEditing.Edit(context, set, name, change);
    }

    private void PickSourceImage()
    {
        if (Set is not { } set)
            return;
        var start = Workspace.Session.ProjectRoot is { } root && Directory.Exists(Path.Combine(root, "Assets"))
            ? Path.Combine(root, "Assets")
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var model = new FilePickerModel(FilePickerMode.Open, start, ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp", "*.tga", "*.svg"]);
        Workspace.FilePicker.Show(model, "Add Atlas Source", "Open", path =>
        {
            Texture2D texture;
            try
            {
                texture = ResourceLoader.Load<Texture2D>(AssetDatabase.Current.ToProjectPath(path));
            }
            catch (Exception e) when (EditorCommands.IsRecoverable(e))
            {
                Workspace.Commands.ReportError($"Could not load {Path.GetFileName(path)}", e);
                return;
            }

            AddSource(set, texture);
        });
    }

    /// <summary>Adds an atlas source for <paramref name="texture"/> and asks Godot's question about its opaque regions.</summary>
    public void AddSource(TileSet set, Texture2D texture, bool? createTiles = null)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(texture);
        var source = new TileSetAtlasSource { Texture = texture, TextureRegionSize = set.TileSize };
        var id = 0;
        EditTileSet("Add Atlas Source", s => id = s.AddSource(source));
        EditedSourceId = id;
        Touch();
        if (createTiles is { } now)
        {
            if (now)
                EditTileSet("Create Tiles in Opaque Regions", _ => source.CreateTilesFromOpaqueRegions());
            return;
        }

        Workspace.Message.Show(new MessageRequest
        {
            Title = "Atlas source",
            Message = "Create tiles in the texture's non-transparent regions?",
            Buttons = ["Yes", "No"],
            Icon = "layout-grid",
            Callback = (button, _) =>
            {
                if (button == 0)
                    EditTileSet("Create Tiles in Opaque Regions", _ => source.CreateTilesFromOpaqueRegions());
                Touch();
            },
        });
    }

    private void PromptCustomLayer()
    {
        Workspace.Message.Show(new MessageRequest
        {
            Title = "Add custom data layer",
            Message = "Name and type (bool, int, float, string, vector2 or color), e.g. \"surface:string\".",
            Input = "name:string",
            Buttons = ["Add", "Cancel"],
            Icon = "database",
            Callback = (button, text) =>
            {
                if (button != 0 || ParseCustomLayer(text) is not { } layer)
                    return;
                EditTileSet("Add Custom Data Layer", s => s.AddCustomDataLayer(layer.Name, layer.Type));
                Touch();
            },
        });
    }

    /// <summary>Parses "name:type" (type: bool, int, float, string, vector2, color; default string).</summary>
    public static (string Name, CustomDataType Type)? ParseCustomLayer(string text)
    {
        var parts = (text ?? "").Split(':', 2, StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || parts[0].Length == 0)
            return null;
        var type = parts.Length < 2 ? CustomDataType.String : parts[1].ToLowerInvariant() switch
        {
            "bool" => CustomDataType.Bool,
            "int" => CustomDataType.Int,
            "float" => CustomDataType.Float,
            "vector2" => CustomDataType.Vector2,
            "color" => CustomDataType.Color,
            _ => CustomDataType.String,
        };
        return (parts[0], type);
    }

    private static bool TryPair(string text, out Vector2I value)
    {
        value = default;
        var comma = text.IndexOf(',', StringComparison.Ordinal);
        if (comma < 0 || !int.TryParse(text.AsSpan(0, comma), NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) ||
            !int.TryParse(text.AsSpan(comma + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var y))
            return false;
        value = new Vector2I(x, y);
        return true;
    }

    // ── Rendering ─────────────────────────────────────────────────────────────────────────────────────────────

    private void Render()
    {
        if (!EnsureLoaded())
            return;
        var tileSet = ShowsTileSet;
        SetElementClass("tm-tab-map", "active", !tileSet);
        SetElementClass("tm-tab-set", "active", tileSet);
        SetElementClass("tm-tab-map", "hidden", !Editor.IsActive);
        SetElementClass("tm-tab-set", "hidden", Editor.EditedTileSet is null);
        var set = Set;
        SetInner("tm-tools", ToolsRml(set));
        SetInner("tm-sources", SourcesRml(set));
        SetInner("tm-atlas", AtlasRml(set));
        SetInner("tm-side", SideRml(set));
    }

    private void SetInner(string id, string rml)
    {
        var element = Document.GetElementById(id);
        if (!element.IsNull)
            element.SetInnerRml(rml);
    }

    private static string Num(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Num(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static void Button(StringBuilder rml, string op, string icon, string tooltip, bool active = false) =>
        rml.Append("<button class=\"tool-button small").Append(active ? " active" : "").Append("\" data-op=\"").Append(op)
            .Append("\" data-tooltip=\"").Append(RmlText.Escape(tooltip)).Append("\"><span class=\"icon icon-sm icon-").Append(icon).Append("\"></span></button>");

    private static void Field(StringBuilder rml, string field, string value, string tooltip) =>
        rml.Append("<input type=\"text\" class=\"text num\" data-field=\"").Append(field).Append("\" value=\"").Append(RmlText.Escape(value))
            .Append("\" data-tooltip=\"").Append(RmlText.Escape(tooltip)).Append("\"/>");

    internal string ToolsRml(TileSet? set)
    {
        var rml = new StringBuilder(2048);
        if (set is null)
            return "";
        if (ShowsTileSet)
        {
            Button(rml, "mode-setup", "settings", "Setup — the atlas grid; click a cell to create or remove its tile", Mode == TileSetEditMode.Setup);
            Button(rml, "mode-select", "pointer", "Select — click a tile to edit its data", Mode == TileSetEditMode.Select);
            Button(rml, "mode-collision", "polygon", "Collision — click a tile to edit its collision polygons", Mode == TileSetEditMode.Collision);
            rml.Append("<span class=\"sep\"></span><span class=\"caption\">Tile</span>");
            Field(rml, "tile-x", Num(set.TileSize.X), "Tile Size X — one cell of the tile set in pixels");
            Field(rml, "tile-y", Num(set.TileSize.Y), "Tile Size Y — one cell of the tile set in pixels");
            rml.Append("<span class=\"sep\"></span>");
            Button(rml, "add-physics", "atom-2", $"Add Physics Layer — tiles collide on it (layers now: {set.PhysicsLayers.Count})");
            Button(rml, "add-custom", "database", $"Add Custom Data Layer — a named, typed value per tile (layers now: {set.CustomDataLayers.Count})");
        }
        else
        {
            Button(rml, "new-alternative", "copy-plus", "New Alternative — an alternative of the picked tile with its own data (another modulate, no collision…)");
            rml.Append("<button class=\"tool-button small\" data-command=\"tilemap.edit_tileset\" data-tooltip=\"Edit TileSet — sources, tiles, tile data and collision in the TileSet tab\"><span class=\"icon icon-sm icon-adjustments\"></span></button>");
        }

        rml.Append("<span class=\"sep\"></span>");
        Button(rml, "zoom-out", "zoom-out", "Zoom Out — show the atlas smaller");
        rml.Append("<span class=\"caption mono\">").Append(Num(_zoom)).Append("×</span>");
        Button(rml, "zoom-in", "zoom-in", "Zoom In — show the atlas larger");
        return rml.ToString();
    }

    internal string SourcesRml(TileSet? set)
    {
        if (set is null)
            return "<div class=\"tm-empty\">Select a TileMapLayer with a TileSet.</div>";
        var rml = new StringBuilder(1024);
        var current = ShowsTileSet ? EditedSourceId : Editor.SourceId;
        foreach (var source in set.Sources)
        {
            if (source is null)
                continue;
            rml.Append("<div class=\"tm-source").Append(source.Id == current ? " active" : "").Append("\" data-source=\"").Append(Num(source.Id))
                .Append("\" data-tooltip=\"").Append(RmlText.Escape(SourceName(source))).Append("\">");
            if (ImagePath(source.Texture) is { } path)
                rml.Append("<img src=\"").Append(RmlText.Escape(path)).Append("\"/>");
            rml.Append("<span class=\"id\">#").Append(Num(source.Id)).Append("</span><span>").Append(RmlText.Escape(SourceName(source))).Append("</span></div>");
        }

        if (ShowsTileSet)
        {
            rml.Append("<div class=\"tm-source-tools\">");
            Button(rml, "add-source", "photo-plus", "Add Atlas Source — pick an image; asks whether to create tiles in its non-transparent regions");
            Button(rml, "remove-source", "trash", "Remove Atlas Source — remove the selected source (cells using it stay, undrawn)");
            Button(rml, "opaque", "wand", "Create Tiles in Opaque Regions — a tile in every grid cell with a visible pixel");
            rml.Append("</div>");
        }

        return rml.ToString();
    }

    private static string SourceName(TileSetAtlasSource source) =>
        source.Texture?.ResourcePath is { } path ? Path.GetFileName(path) : source.Texture?.ResourceName is { Length: > 0 } name ? name : "(no texture)";

    private static string? ImagePath(Texture2D? texture) =>
        texture?.ResourcePath is { } path ? AssetDatabase.Current.ToAbsolutePath(path) : null;

    internal string AtlasRml(TileSet? set)
    {
        var source = set?.GetSource(ShowsTileSet ? EditedSourceId : Editor.SourceId) ?? set?.Sources.FirstOrDefault();
        if (set is null || source?.Texture is not { } texture)
            return set is null ? "" : "<div class=\"tm-empty\">No atlas source: add one in the TileSet tab (photo-plus).</div>";
        var width = texture.Width * _zoom;
        var height = texture.Height * _zoom;
        var rml = new StringBuilder(512 + 200 * 64);
        rml.Append("<div class=\"tm-sheet\" style=\"width: ").Append(RmlText.Dp(width)).Append("; height: ").Append(RmlText.Dp(height)).Append(";\">");
        if (ImagePath(texture) is { } path)
            rml.Append("<img class=\"tm-image\" src=\"").Append(RmlText.Escape(path)).Append("\" style=\"width: ").Append(RmlText.Dp(width))
                .Append("; height: ").Append(RmlText.Dp(height)).Append(";\"/>");
        var grid = source.Grid;
        var size = source.GetAtlasGridSize();
        var picked = ShowsTileSet ? (EditedTile is { } t ? [t] : []) : Editor.SourceId == source.Id ? Editor.SelectedTiles : [];
        for (var y = 0; y < size.Y; y++)
            for (var x = 0; x < size.X; x++)
            {
                var cell = new Vector2I(x, y);
                var r = grid.CellRect(cell);
                var created = source.HasTile(cell);
                var solid = created && source.GetTileData(cell).Collision.Count > 0;
                rml.Append("<div class=\"tm-cell").Append(created ? "" : " missing").Append(picked.Contains(cell) ? " picked" : "").Append(solid ? " solid" : "")
                    .Append("\" data-cell=\"").Append(Num(x)).Append(',').Append(Num(y)).Append("\" style=\"left: ").Append(RmlText.Dp(r.Position.X * _zoom))
                    .Append("; top: ").Append(RmlText.Dp(r.Position.Y * _zoom)).Append("; width: ").Append(RmlText.Dp(r.Size.X * _zoom))
                    .Append("; height: ").Append(RmlText.Dp(r.Size.Y * _zoom)).Append(";\"></div>");
            }

        return rml.Append("</div>").ToString();
    }

    internal string SideRml(TileSet? set)
    {
        if (set is null)
            return "";
        var rml = new StringBuilder(2048);
        if (!ShowsTileSet)
        {
            var source = set.GetSource(Editor.SourceId);
            if (source is null || Editor.SelectedTiles.Count == 0)
                return "<div class=\"tm-empty\">Pick a tile in the atlas: click, drag for a pattern, Cmd/Ctrl+click for a random set.</div>";
            rml.Append("<div class=\"heading\">").Append(Editor.SelectedTiles.Count == 1 ? "Alternatives" : $"{Editor.SelectedTiles.Count} tiles").Append("</div>");
            if (Editor.SelectedTiles.Count == 1)
            {
                var alternatives = new List<int>();
                source.GetAlternativeTiles(Editor.SelectedTiles[0], alternatives);
                var region = source.GetTileTextureRegion(Editor.SelectedTiles[0]);
                rml.Append("<div class=\"tm-alts\">");
                foreach (var alt in alternatives)
                {
                    rml.Append("<div class=\"tm-alt").Append(alt == Editor.Alternative ? " active" : "").Append("\" data-alt=\"").Append(Num(alt))
                        .Append("\" data-tooltip=\"").Append(alt == 0 ? "Base tile" : $"Alternative {alt}").Append("\">");
                    if (ImagePath(source.Texture) is { } path)
                        rml.Append("<img src=\"").Append(RmlText.Escape(path)).Append("\" rect=\"").Append(Num(region.Position.X)).Append(' ')
                            .Append(Num(region.Position.Y)).Append(' ').Append(Num(region.Size.X)).Append(' ').Append(Num(region.Size.Y)).Append("\"/>");
                    rml.Append("</div>");
                }

                rml.Append("</div>");
            }

            rml.Append("<div class=\"heading\">Brush</div><div>").Append(Editor.Brush.Random ? "Random tile" : "Pattern").Append(' ')
                .Append(Num(Editor.Brush.Size.X)).Append('×').Append(Num(Editor.Brush.Size.Y)).Append("</div>");
            return rml.ToString();
        }

        var edited = set.GetSource(EditedSourceId);
        if (edited is null)
            return "<div class=\"tm-empty\">Add an atlas source (photo-plus).</div>";
        if (Mode == TileSetEditMode.Setup)
        {
            rml.Append("<div class=\"heading\">Atlas source</div>");
            Row(rml, "Id", "source-id", Num(edited.Id), "Source Id — the number cells store; unique in the tile set");
            Row(rml, "Margin X", "margin-x", Num(edited.Margins.X), "Margins — pixels before the first column");
            Row(rml, "Margin Y", "margin-y", Num(edited.Margins.Y), "Margins — pixels before the first row");
            Row(rml, "Separation X", "sep-x", Num(edited.Separation.X), "Separation — pixels between columns");
            Row(rml, "Separation Y", "sep-y", Num(edited.Separation.Y), "Separation — pixels between rows");
            Row(rml, "Region W", "size-x", Num(edited.TextureRegionSize.X), "Texture Region Size — one tile's width in the texture");
            Row(rml, "Region H", "size-y", Num(edited.TextureRegionSize.Y), "Texture Region Size — one tile's height in the texture");
            rml.Append("<div>").Append(Num(edited.Tiles.Length)).Append(" tiles · click a cell to create or remove one</div>");
            return rml.ToString();
        }

        if (EditedTile is not { } tile || !edited.HasTile(tile))
            return "<div class=\"tm-empty\">Click a tile in the atlas.</div>";
        var data = edited.GetTileData(tile);
        rml.Append("<div class=\"heading\">Tile ").Append(Num(tile.X)).Append(", ").Append(Num(tile.Y)).Append("</div>");
        if (Mode == TileSetEditMode.Select)
        {
            Row(rml, "Origin X", "origin-x", Num(data.TextureOrigin.X), "Texture Origin — shifts the drawn texture (pixels, positive moves it up and left)");
            Row(rml, "Origin Y", "origin-y", Num(data.TextureOrigin.Y), "Texture Origin — shifts the drawn texture (pixels, positive moves it up and left)");
            var m = data.Modulate;
            Row(rml, "Modulate", "modulate", $"{Num(m.X)}, {Num(m.Y)}, {Num(m.Z)}, {Num(m.W)}", "Modulate — r, g, b, a multiplied into the tile");
            Row(rml, "Z Index", "z", Num(data.ZIndex), "Z Index — added to the layer's z (the tile draws in its own chunk sub-item)");
            Row(rml, "Y Sort Origin", "ysort", Num(data.YSortOrigin), "Y Sort Origin — added to the cell's y when the layer is y-sorted");
            Row(rml, "Probability", "probability", Num(data.Probability), "Probability — the tile's weight in random paints");
            for (var i = 0; i < set.CustomDataLayers.Count; i++)
            {
                var layer = set.CustomDataLayers[i];
                Row(rml, layer.Name, "custom-" + Num(i), data.GetCustomString(i), $"Custom data '{layer.Name}' ({layer.Type}) — invariant text");
            }

            return rml.ToString();
        }

        // Collision.
        rml.Append("<div class=\"tm-row\">");
        Button(rml, "physics-prev", "chevron-left", "Previous Physics Layer");
        rml.Append("<span class=\"label\" style=\"flex: 1 1 auto; text-align: center;\">Physics layer ").Append(Num(PhysicsLayer))
            .Append(set.PhysicsLayers.Count == 0 ? " (none yet)" : "").Append("</span>");
        Button(rml, "physics-next", "chevron-right", "Next Physics Layer");
        rml.Append("</div><div class=\"tm-row\">");
        Button(rml, "full-tile", "square", "Full Tile — one square covering the tile");
        Button(rml, "add-polygon", "polygon", "Add Polygon — click snap points to place its points (three or more)", Drawing);
        Button(rml, "clear-collision", "trash", "Clear — remove the tile's polygons on this physics layer");
        Button(rml, "snap", "magnet", $"Snap — {(SnapStep >= 8f ? "half a tile" : SnapStep >= 4f ? "a quarter tile" : "one pixel")} (click to change)");
        Button(rml, "delete-point", "x", "Delete Point (Delete) — remove the selected point");
        rml.Append("</div>");
        rml.Append(CollisionViewRml(set, edited, tile, data));
        return rml.ToString();
    }

    private static void Row(StringBuilder rml, string label, string field, string value, string tooltip)
    {
        rml.Append("<div class=\"tm-row\" data-tooltip=\"").Append(RmlText.Escape(tooltip)).Append("\"><span class=\"label\">").Append(RmlText.Escape(label))
            .Append("</span><input type=\"text\" class=\"text\" data-field=\"").Append(field).Append("\" value=\"").Append(RmlText.Escape(value)).Append("\"/></div>");
    }

    // The tile magnified, snap points every half step (so a point can sit between two), the polygons' edges and points.
    private string CollisionViewRml(TileSet set, TileSetAtlasSource source, Vector2I tile, TileData data)
    {
        var size = set.TileSize;
        var scale = TileViewSize / Math.Max(size.X, size.Y);
        var half = new Vector2(size.X, size.Y) * 0.5f;
        var rml = new StringBuilder(8192);
        rml.Append("<div class=\"tm-tile\" style=\"width: ").Append(RmlText.Dp(size.X * scale)).Append("; height: ").Append(RmlText.Dp(size.Y * scale)).Append(";\">");
        var region = source.GetTileTextureRegion(tile);
        if (ImagePath(source.Texture) is { } path)
            rml.Append("<img src=\"").Append(RmlText.Escape(path)).Append("\" rect=\"").Append(Num(region.Position.X)).Append(' ').Append(Num(region.Position.Y))
                .Append(' ').Append(Num(region.Size.X)).Append(' ').Append(Num(region.Size.Y)).Append("\" style=\"width: ").Append(RmlText.Dp(size.X * scale))
                .Append("; height: ").Append(RmlText.Dp(size.Y * scale)).Append(";\"/>");
        Vector2 Screen(Vector2 p) => (p + half) * scale;
        var step = SnapStep / 2f;
        var nx = (int)MathF.Round(half.X / step);
        var ny = (int)MathF.Round(half.Y / step);
        for (var j = -ny; j <= ny; j++)
            for (var i = -nx; i <= nx; i++)
            {
                if (((i | j) & 1) != 0 && SnapStep > 1f)
                    continue; // whole steps only, except at pixel snapping
                var at = Screen(new Vector2(i, j) * step);
                rml.Append("<div class=\"tm-snap\" data-snap=\"").Append(Num(i)).Append(',').Append(Num(j)).Append("\" style=\"left: ").Append(RmlText.Dp(at.X))
                    .Append("; top: ").Append(RmlText.Dp(at.Y)).Append(";\"></div>");
            }

        var index = 0;
        foreach (var polygon in data.Collision)
        {
            if (polygon.PhysicsLayer != PhysicsLayer)
                continue;
            var points = polygon.Points;
            for (var k = 0; k < points.Length && points.Length > 1; k++)
            {
                var a = Screen(points[k]);
                var b = Screen(points[(k + 1) % points.Length]);
                var length = Vector2.Distance(a, b);
                var angle = MathF.Atan2(b.Y - a.Y, b.X - a.X) * 180f / MathF.PI;
                rml.Append("<div class=\"tm-edge\" style=\"left: ").Append(RmlText.Dp(a.X)).Append("; top: ").Append(RmlText.Dp(a.Y - 1)).Append("; width: ")
                    .Append(RmlText.Dp(length)).Append("; transform: rotate(").Append(angle.ToString("0.##", CultureInfo.InvariantCulture)).Append("deg);\"></div>");
            }

            for (var k = 0; k < points.Length; k++)
            {
                var at = Screen(points[k]);
                rml.Append("<div class=\"tm-point").Append(SelectedPoint == (index, k) ? " active" : "").Append("\" data-point=\"").Append(Num(index)).Append(',')
                    .Append(Num(k)).Append("\" style=\"left: ").Append(RmlText.Dp(at.X)).Append("; top: ").Append(RmlText.Dp(at.Y)).Append(";\"></div>");
            }

            index++;
        }

        return rml.Append("</div>").ToString();
    }
}

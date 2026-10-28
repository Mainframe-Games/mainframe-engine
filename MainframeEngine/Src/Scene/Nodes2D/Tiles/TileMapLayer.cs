using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// One layer of a tile map (Godot 4.3's <c>TileMapLayer</c>; there is no multi-layer <c>TileMap</c> node): cells that
/// show tiles of a <see cref="TileSet"/>, stored, drawn and collided in square chunks of
/// <see cref="RenderingQuadrantSize"/> cells. Chunks draw through the canvas batcher as sub-items of the layer (one
/// batch per atlas when nothing else interleaves), only rebuild when one of their cells changes, and get one static
/// Box2D body per physics layer. With <see cref="CanvasItem.YSortEnabled"/> every chunk row is its own sub-item, sorted
/// with the layer's children. Cells are saved as one base64 line per chunk (<see cref="TileMapData"/>).
/// </summary>
/// <remarks>
/// Layers are ordinary canvas items: z index, modulate, material, visibility and y-sort come from
/// <see cref="CanvasItem"/>, and several layers are siblings. Painting allocates only when a chunk is created or a
/// draw list grows; an unchanged layer costs a bounds test per chunk per frame. See docs/design/tile-maps.md.
/// </remarks>
[EditorIcon("grid-pattern", Family = EditorIconFamily.Space2D)]
public partial class TileMapLayer : Node2D
{
    private const int CoordMin = short.MinValue, CoordMax = short.MaxValue;

    private readonly Dictionary<Vector2I, TileChunk> _chunks = [];
    private readonly List<TileChunk> _ordered = [];
    private readonly List<TileChunk> _pendingRender = [];
    private readonly List<CanvasSubItem> _subs = [];
    private bool _orderDirty;
    private bool _subsDirty;
    private bool _enabled = true;
    private TileSet? _tileSet;
    private int _ySortOrigin;
    private bool _xReversed;
    private int _quadrantSize = 16;
    private bool _collisionEnabled = true;
    private DebugVisibilityMode _collisionVisibility;
    private TileBodies2D? _bodies;
    private int _usedCount;

    public TileMapLayer()
    {
        SubItems = _subs;
    }

    /// <summary>Draws the layer and creates its bodies (Godot's <c>enabled</c>).</summary>
    [Export]
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value)
                return;
            _enabled = value;
            MarkAllDirty();
        }
    }

    /// <summary>The tiles the cells refer to.</summary>
    [Export]
    public TileSet? TileSet
    {
        get => _tileSet;
        set
        {
            if (ReferenceEquals(_tileSet, value))
                return;
            if (_tileSet is not null)
                _tileSet.Changed -= OnTileSetChanged;
            _tileSet = value;
            if (_tileSet is not null)
                _tileSet.Changed += OnTileSetChanged;
            MarkAllDirty();
            RefreshBodies();
        }
    }

    /// <summary>Added to every cell's y-sort position (pixels), when the layer is y-sorted.</summary>
    [Export]
    public int YSortOrigin
    {
        get => _ySortOrigin;
        set
        {
            if (_ySortOrigin == value)
                return;
            _ySortOrigin = value;
            MarkAllDirty(physics: false);
        }
    }

    /// <summary>Draws each row from right to left (Godot's <c>x_draw_order_reversed</c>).</summary>
    [Export]
    public bool XDrawOrderReversed
    {
        get => _xReversed;
        set
        {
            if (_xReversed == value)
                return;
            _xReversed = value;
            _orderDirty = true;
            MarkAllDirty(physics: false);
        }
    }

    /// <summary>The chunk edge in cells: drawing, culling and bodies are per chunk (Godot's rendering quadrant size).</summary>
    [Export(Range = "1,64,1")]
    public int RenderingQuadrantSize
    {
        get => _quadrantSize;
        set
        {
            value = Math.Clamp(value, 1, 64);
            if (_quadrantSize == value)
                return;
            Rechunk(value);
        }
    }

    /// <summary>Creates static bodies for the tiles' collision polygons.</summary>
    [Export]
    public bool CollisionEnabled
    {
        get => _collisionEnabled;
        set
        {
            if (_collisionEnabled == value)
                return;
            _collisionEnabled = value;
            RefreshBodies();
        }
    }

    /// <summary>When the tiles' collision shapes are drawn (Godot's <c>collision_visibility_mode</c>).</summary>
    [Export]
    public DebugVisibilityMode CollisionVisibilityMode
    {
        get => _collisionVisibility;
        set => _collisionVisibility = value;
    }

    /// <summary>The cells, one base64 line per chunk (see <see cref="TileMapDataCodec"/>); saved, never shown.</summary>
    [Export(Storage = true)]
    internal string[] TileMapData
    {
        get => TileMapDataCodec.Encode(this);
        set => TileMapDataCodec.Decode(this, value);
    }

    /// <summary>Raised when cells, properties or the tile set change (Godot's <c>changed</c>).</summary>
    [Signal]
    public event Action? Changed;

    /// <summary>The number of non-empty cells.</summary>
    public int UsedCellCount => _usedCount;

    /// <summary>The number of chunks holding cells.</summary>
    public int ChunkCount => _chunks.Count;

    /// <summary>The cell size in pixels (the tile set's, else 16 × 16).</summary>
    public Vector2I TileSize => _tileSet?.TileSize ?? new Vector2I(16, 16);

    internal IReadOnlyList<TileChunk> Chunks
    {
        get
        {
            EnsureOrder();
            return _ordered;
        }
    }

    // ── Cells ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Sets the cell at <paramref name="coords"/> (Godot's <c>set_cell</c>): a source id of -1 or no atlas
    /// coordinates erase it. <paramref name="alternative"/> may carry the transform flags.
    /// </summary>
    public void SetCell(Vector2I coords, int sourceId = -1, Vector2I? atlasCoords = null, int alternative = 0)
    {
        if (sourceId < 0 || atlasCoords is not { X: >= 0, Y: >= 0 } atlas)
        {
            SetCellData(coords, TileCell.Empty);
            return;
        }

        if (sourceId > short.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(sourceId), sourceId, "Source ids must be 0…32767.");
        if (atlas.X > ushort.MaxValue - 1 || atlas.Y > ushort.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(atlasCoords), atlas, "Atlas coordinates must be 0…65534.");
        SetCellData(coords, new TileCell(sourceId, atlas, alternative & 0xFFFF));
    }

    /// <summary>Erases the cell at <paramref name="coords"/>.</summary>
    public void EraseCell(Vector2I coords) => SetCellData(coords, TileCell.Empty);

    /// <summary>The cell at <paramref name="coords"/> (<see cref="TileCell.Empty"/> when none); no allocation.</summary>
    public TileCell GetCell(Vector2I coords)
    {
        if (!_chunks.TryGetValue(ChunkOf(coords), out var chunk))
            return TileCell.Empty;
        return chunk.Cells[chunk.IndexOf(coords)];
    }

    /// <summary>Writes one cell (the editor's undo writes back exact cells through this).</summary>
    public void SetCellData(Vector2I coords, TileCell cell)
    {
        if (coords.X < CoordMin || coords.X > CoordMax || coords.Y < CoordMin || coords.Y > CoordMax)
            throw new ArgumentOutOfRangeException(nameof(coords), coords, "Cell coordinates must fit in 16 bits (−32768…32767).");
        if (cell.IsEmpty)
            cell = TileCell.Empty;
        var key = ChunkOf(coords);
        if (!_chunks.TryGetValue(key, out var chunk))
        {
            if (cell.IsEmpty)
                return;
            chunk = new TileChunk(key, _quadrantSize);
            _chunks.Add(key, chunk);
            _orderDirty = true;
        }

        var index = chunk.IndexOf(coords);
        var old = chunk.Cells[index];
        if (old == cell)
            return;
        chunk.Cells[index] = cell;
        if (old.IsEmpty)
        {
            chunk.Used++;
            _usedCount++;
        }
        else if (cell.IsEmpty)
        {
            chunk.Used--;
            _usedCount--;
        }

        if (chunk.Used == 0)
            RemoveChunk(chunk);
        else
            MarkChunkDirty(chunk);
        Changed?.Invoke();
    }

    /// <summary>Removes every cell (Godot's <c>clear</c>).</summary>
    public void Clear()
    {
        if (_chunks.Count == 0)
            return;
        foreach (var chunk in _chunks.Values)
            ReleaseChunk(chunk);
        _chunks.Clear();
        _ordered.Clear();
        _pendingRender.Clear();
        _subs.Clear();
        _usedCount = 0;
        _orderDirty = false;
        QueueRedraw();
        Changed?.Invoke();
    }

    /// <summary>The cell's source id, or -1 when it is empty.</summary>
    public int GetCellSourceId(Vector2I coords) => GetCell(coords).SourceId;

    /// <summary>The cell's atlas coordinates, or (-1, -1) when it is empty.</summary>
    public Vector2I GetCellAtlasCoords(Vector2I coords) => GetCell(coords) is { IsEmpty: false } cell ? cell.AtlasCoords : new Vector2I(-1, -1);

    /// <summary>The cell's alternative id with its transform flags, or -1 when it is empty.</summary>
    public int GetCellAlternativeTile(Vector2I coords) => GetCell(coords) is { IsEmpty: false } cell ? cell.Alternative : -1;

    /// <summary>The data of the cell's tile, or null when it is empty or names a missing source or tile.</summary>
    public TileData? GetCellTileData(Vector2I coords) =>
        GetCell(coords) is { IsEmpty: false } cell && _tileSet is { } set ? set.GetTileData(cell) : null;

    public bool IsCellFlippedH(Vector2I coords) => GetCell(coords) is { IsEmpty: false, FlipH: true };

    public bool IsCellFlippedV(Vector2I coords) => GetCell(coords) is { IsEmpty: false, FlipV: true };

    public bool IsCellTransposed(Vector2I coords) => GetCell(coords) is { IsEmpty: false, Transpose: true };

    /// <summary>Appends the coordinates of every non-empty cell (chunk by chunk) to <paramref name="results"/>; returns the count.</summary>
    public int GetUsedCells(List<Vector2I> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        return GetUsedCellsById(results);
    }

    /// <summary>
    /// Appends the cells that match every given filter (Godot's <c>get_used_cells_by_id</c>): a source id (-1: any),
    /// atlas coordinates (null: any) and an alternative id with its flags (-1: any).
    /// </summary>
    public int GetUsedCellsById(List<Vector2I> results, int sourceId = -1, Vector2I? atlasCoords = null, int alternative = -1)
    {
        ArgumentNullException.ThrowIfNull(results);
        var start = results.Count;
        EnsureOrder();
        foreach (var chunk in _ordered)
        {
            var origin = chunk.Origin;
            var size = chunk.Size;
            var cells = chunk.Cells;
            for (var i = 0; i < cells.Length; i++)
            {
                var cell = cells[i];
                if (cell.IsEmpty || (sourceId >= 0 && cell.SourceId != sourceId) ||
                    (atlasCoords is { } atlas && cell.AtlasCoords != atlas) || (alternative >= 0 && cell.Alternative != alternative))
                    continue;
                results.Add(new Vector2I(origin.X + i % size, origin.Y + i / size));
            }
        }

        return results.Count - start;
    }

    /// <summary>The smallest rectangle (cells) holding every non-empty cell; empty when there are none.</summary>
    public Rect2I GetUsedRect()
    {
        if (_usedCount == 0)
            return default;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        foreach (var chunk in _chunks.Values)
        {
            var origin = chunk.Origin;
            var size = chunk.Size;
            var cells = chunk.Cells;
            for (var i = 0; i < cells.Length; i++)
            {
                if (cells[i].IsEmpty)
                    continue;
                var x = origin.X + i % size;
                var y = origin.Y + i / size;
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
        }

        return new Rect2I(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    // ── Coordinates ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The cell containing a point in the layer's local space (Godot's <c>local_to_map</c>).</summary>
    public Vector2I LocalToMap(Vector2 localPosition)
    {
        var size = TileSize;
        return new Vector2I((int)MathF.Floor(localPosition.X / size.X), (int)MathF.Floor(localPosition.Y / size.Y));
    }

    /// <summary>The centre of a cell in the layer's local space (Godot's <c>map_to_local</c>).</summary>
    public Vector2 MapToLocal(Vector2I mapPosition)
    {
        var size = TileSize;
        return new Vector2((mapPosition.X + 0.5f) * size.X, (mapPosition.Y + 0.5f) * size.Y);
    }

    /// <summary>The neighbouring cell on a side or corner (square tiles; other neighbours return <paramref name="coords"/>).</summary>
    public static Vector2I GetNeighborCell(Vector2I coords, TileCellNeighbor neighbor) => neighbor switch
    {
        TileCellNeighbor.RightSide => coords + new Vector2I(1, 0),
        TileCellNeighbor.BottomRightCorner => coords + new Vector2I(1, 1),
        TileCellNeighbor.BottomSide => coords + new Vector2I(0, 1),
        TileCellNeighbor.BottomLeftCorner => coords + new Vector2I(-1, 1),
        TileCellNeighbor.LeftSide => coords + new Vector2I(-1, 0),
        TileCellNeighbor.TopLeftCorner => coords + new Vector2I(-1, -1),
        TileCellNeighbor.TopSide => coords + new Vector2I(0, -1),
        TileCellNeighbor.TopRightCorner => coords + new Vector2I(1, -1),
        _ => coords,
    };

    /// <summary>
    /// The cell a collision at <paramref name="globalPosition"/> with surface <paramref name="normal"/> (pointing away
    /// from the tile) hit: the point is pushed half a pixel into the surface, Godot's usual idiom. False when that cell
    /// is empty.
    /// </summary>
    public bool GetCoordsForCollision(Vector2 globalPosition, Vector2 normal, out Vector2I coords)
    {
        var n = normal.LengthSquared() > 1e-12f ? Vector2.Normalize(normal) : Vector2.Zero;
        coords = LocalToMap(ToLocal(globalPosition - n * 0.5f));
        return !GetCell(coords).IsEmpty;
    }

    /// <summary>Applies pending chunk rebuilds now, drawing and bodies (Godot's <c>update_internals</c>).</summary>
    public void UpdateInternals()
    {
        RebuildPendingChunks();
        _bodies?.FlushNow();
    }

    // ── Chunks ────────────────────────────────────────────────────────────────────────────────────────────────

    internal Vector2I ChunkOf(Vector2I cell) => new(FloorDiv(cell.X, _quadrantSize), FloorDiv(cell.Y, _quadrantSize));

    private static int FloorDiv(int a, int b)
    {
        var q = a / b;
        return (a % b != 0 && (a < 0) != (b < 0)) ? q - 1 : q;
    }

    internal bool TryGetChunk(Vector2I chunkCoords, out TileChunk chunk) => _chunks.TryGetValue(chunkCoords, out chunk!);

    private void MarkChunkDirty(TileChunk chunk, bool physics = true)
    {
        if (!chunk.RenderDirty)
        {
            chunk.RenderDirty = true;
            _pendingRender.Add(chunk);
        }

        if (physics)
            _bodies?.MarkChunkDirty(chunk);
        QueueRedraw();
    }

    private void MarkAllDirty(bool physics = true)
    {
        foreach (var chunk in _chunks.Values)
            MarkChunkDirty(chunk, physics);
        _subsDirty = true;
        QueueRedraw();
    }

    private void RemoveChunk(TileChunk chunk)
    {
        ReleaseChunk(chunk);
        _chunks.Remove(chunk.Coords);
        _orderDirty = true;
        _subsDirty = true;
        if (chunk.RenderDirty)
        {
            chunk.RenderDirty = false;
            _pendingRender.Remove(chunk);
        }

        QueueRedraw();
    }

    private void ReleaseChunk(TileChunk chunk)
    {
        _bodies?.RemoveChunk(chunk);
        foreach (var sub in chunk.SubItems)
            sub.DrawList.Clear();
        chunk.SubItems.Clear();
        chunk.SubKeys.Clear();
    }

    private void Rechunk(int size)
    {
        var cells = new List<(Vector2I, TileCell)>(_usedCount);
        foreach (var chunk in _chunks.Values)
        {
            var origin = chunk.Origin;
            for (var i = 0; i < chunk.Cells.Length; i++)
                if (!chunk.Cells[i].IsEmpty)
                    cells.Add((new Vector2I(origin.X + i % chunk.Size, origin.Y + i / chunk.Size), chunk.Cells[i]));
        }

        var changed = Changed;
        Changed = null; // one notification for the whole move
        Clear();
        _quadrantSize = size;
        foreach (var (coords, cell) in cells)
            SetCellData(coords, cell);
        Changed = changed;
        Changed?.Invoke();
    }

    private void EnsureOrder()
    {
        if (!_orderDirty)
            return;
        _ordered.Clear();
        foreach (var chunk in _chunks.Values)
            _ordered.Add(chunk);
        _ordered.Sort(_xReversed ? ChunkOrder.Reversed : ChunkOrder.Forward);
        _orderDirty = false;
        _subsDirty = true;
    }

    private void OnTileSetChanged()
    {
        MarkAllDirty();
        RefreshBodies();
        Changed?.Invoke();
    }

    // Godot's draw order of quadrants: by y, then x (reversed with XDrawOrderReversed).
    private sealed class ChunkOrder(bool reversed) : IComparer<TileChunk>
    {
        public static readonly ChunkOrder Forward = new(false);
        public static readonly ChunkOrder Reversed = new(true);

        public int Compare(TileChunk? a, TileChunk? b)
        {
            if (a!.Coords.Y != b!.Coords.Y)
                return a.Coords.Y.CompareTo(b.Coords.Y);
            return reversed ? b.Coords.X.CompareTo(a.Coords.X) : a.Coords.X.CompareTo(b.Coords.X);
        }
    }

    // ── Tree ──────────────────────────────────────────────────────────────────────────────────────────────────

    protected override void OnEnterTree()
    {
        base.OnEnterTree();
        TrackGlobalTransformChanges(true);
        RefreshBodies();
    }

    protected override void OnExitTree()
    {
        _bodies?.Dispose();
        _bodies = null;
        TrackGlobalTransformChanges(false);
        base.OnExitTree();
    }

    private protected override void OnGlobalTransformInvalidated() => _bodies?.MarkTransformDirty();

    private protected override void OnYSortEnabledChanged() => MarkAllDirty(physics: false);

    // Bodies exist while the layer is in the tree with collision enabled and a tile set with physics layers (a purely
    // visual layer never creates a physics space); a disabled layer has no shapes.
    private void RefreshBodies()
    {
        if (!IsInsideTree)
            return;
        var wanted = _collisionEnabled && _tileSet is { PhysicsLayers.Count: > 0 };
        if (wanted && _bodies is null)
        {
            _bodies = PhysicsServer2D.For(Tree!).AddTileLayer(this);
            foreach (var chunk in _chunks.Values)
                _bodies.MarkChunkDirty(chunk);
        }
        else if (!wanted && _bodies is not null)
        {
            _bodies.Dispose();
            _bodies = null;
        }
    }

    internal TileBodies2D? Bodies => _bodies;
}

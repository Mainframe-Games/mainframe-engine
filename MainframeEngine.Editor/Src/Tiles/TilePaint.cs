namespace MainframeEngine.Editor;

/// <summary>The paint bar's tools (docs/design/tile-maps.md#paint-bar-2d-view).</summary>
public enum TilePaintTool
{
    Select,
    Paint,
    Line,
    Rect,
    Bucket,
    Picker,
    Eraser,
}

/// <summary>A transform of the brush (Godot's tile editor buttons).</summary>
public enum TileBrushTransform
{
    FlipH,
    FlipV,
    RotateRight,
    RotateLeft,
}

/// <summary>
/// What the paint tools put down: a pattern of cells at offsets from its top-left corner (one cell for a single tile, a
/// rectangle picked in the atlas or copied from the map), or — with <see cref="Random"/> — one of its cells per painted
/// cell, weighted by the tiles' <see cref="TileData.Probability"/>.
/// </summary>
public sealed class TileBrush
{
    private readonly List<(Vector2I Offset, TileCell Cell)> _cells = [];

    public IReadOnlyList<(Vector2I Offset, TileCell Cell)> Cells => _cells;

    public bool IsEmpty => _cells.Count == 0;

    /// <summary>The pattern's size in cells.</summary>
    public Vector2I Size { get; private set; }

    /// <summary>Paint one of the cells at random per painted cell (the dice button) instead of the pattern.</summary>
    public bool Random { get; set; }

    public static TileBrush OfCell(TileCell cell)
    {
        var brush = new TileBrush();
        brush.Set([(Vector2I.Zero, cell)]);
        return brush;
    }

    /// <summary>Replaces the cells (offsets are normalised so the pattern starts at (0, 0)).</summary>
    public void Set(IEnumerable<(Vector2I Offset, TileCell Cell)> cells)
    {
        ArgumentNullException.ThrowIfNull(cells);
        _cells.Clear();
        foreach (var c in cells)
            if (!c.Cell.IsEmpty)
                _cells.Add(c);
        Normalise();
    }

    /// <summary>The cell the pattern puts at <paramref name="offset"/> (wrapped), or <see cref="TileCell.Empty"/>.</summary>
    public TileCell At(Vector2I offset)
    {
        if (Size.X == 0 || Size.Y == 0)
            return TileCell.Empty;
        var o = new Vector2I(Mod(offset.X, Size.X), Mod(offset.Y, Size.Y));
        foreach (var (at, cell) in _cells)
            if (at == o)
                return cell;
        return TileCell.Empty;
    }

    /// <summary>
    /// Applies a flip or quarter turn to every cell's transform flags and to the pattern's layout (Godot's
    /// <c>_get_transformed_alternative</c>; a quarter turn cycles the four rotations and leaves mirrored states alone).
    /// </summary>
    public void Transform(TileBrushTransform transform)
    {
        var w = Size.X;
        var h = Size.Y;
        for (var i = 0; i < _cells.Count; i++)
        {
            var (o, cell) = _cells[i];
            var offset = transform switch
            {
                TileBrushTransform.FlipH => new Vector2I(w - 1 - o.X, o.Y),
                TileBrushTransform.FlipV => new Vector2I(o.X, h - 1 - o.Y),
                TileBrushTransform.RotateRight => new Vector2I(h - 1 - o.Y, o.X),
                _ => new Vector2I(o.Y, w - 1 - o.X),
            };
            _cells[i] = (offset, cell with { Alternative = (ushort)TransformAlternative(cell.Alternative, transform) });
        }

        Normalise();
    }

    /// <summary>Godot's <c>TileMapLayerEditorTilesPlugin::_get_transformed_alternative</c>.</summary>
    public static int TransformAlternative(int alternative, TileBrushTransform transform)
    {
        var flipH = (alternative & TileSetAtlasSource.TransformFlipH) != 0;
        var flipV = (alternative & TileSetAtlasSource.TransformFlipV) != 0;
        var transpose = (alternative & TileSetAtlasSource.TransformTranspose) != 0;
        switch (transform)
        {
            case TileBrushTransform.FlipH:
                flipH = !flipH;
                break;
            case TileBrushTransform.FlipV:
                flipV = !flipV;
                break;
            default:
                // Every rotation in the order a right turn visits them: (h, v, t).
                ReadOnlySpan<(bool H, bool V, bool T)> rotations = [(false, false, false), (false, true, true), (true, true, false), (true, false, true)];
                for (var i = 0; i < 4; i++)
                {
                    if (rotations[i] != (flipH, flipV, transpose))
                        continue;
                    var next = transform == TileBrushTransform.RotateRight ? (i + 1) % 4 : (i + 3) % 4;
                    (flipH, flipV, transpose) = rotations[next];
                    break;
                }

                break;
        }

        return (alternative & TileSetAtlasSource.AlternativeMask) | (flipH ? TileSetAtlasSource.TransformFlipH : 0) |
               (flipV ? TileSetAtlasSource.TransformFlipV : 0) | (transpose ? TileSetAtlasSource.TransformTranspose : 0);
    }

    /// <summary>One of the brush's cells for a random paint, weighted by tile probability.</summary>
    public TileCell PickRandom(Random rng, TileSet? set)
    {
        ArgumentNullException.ThrowIfNull(rng);
        if (_cells.Count == 0)
            return TileCell.Empty;
        var total = 0.0;
        foreach (var (_, cell) in _cells)
            total += Weight(cell, set);
        if (total <= 0)
            return _cells[rng.Next(_cells.Count)].Cell;
        var pick = rng.NextDouble() * total;
        foreach (var (_, cell) in _cells)
        {
            pick -= Weight(cell, set);
            if (pick < 0)
                return cell;
        }

        return _cells[^1].Cell;
    }

    private static double Weight(TileCell cell, TileSet? set) => set?.GetTileData(cell)?.Probability ?? 1f;

    private void Normalise()
    {
        if (_cells.Count == 0)
        {
            Size = Vector2I.Zero;
            return;
        }

        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        foreach (var (o, _) in _cells)
        {
            minX = Math.Min(minX, o.X);
            minY = Math.Min(minY, o.Y);
            maxX = Math.Max(maxX, o.X);
            maxY = Math.Max(maxY, o.Y);
        }

        for (var i = 0; i < _cells.Count; i++)
            _cells[i] = (_cells[i].Offset - new Vector2I(minX, minY), _cells[i].Cell);
        Size = new Vector2I(maxX - minX + 1, maxY - minY + 1);
    }

    private static int Mod(int a, int b) => ((a % b) + b) % b;
}

/// <summary>
/// The cells each paint tool writes (no editor state, no allocation beyond the output): stamps, Bresenham lines,
/// rectangles, flood fills. Every function adds <c>cell → value</c> pairs to an output dictionary; the caller applies
/// them (live for a paint stroke, on release for a line or rectangle) and records them for undo.
/// </summary>
public static class TilePaint
{
    /// <summary>Settings that decide what one painted cell gets.</summary>
    public readonly record struct Options(TileBrush Brush, bool Erase = false, float Scattering = 0f, Random? Rng = null, TileSet? TileSet = null);

    /// <summary>The brush at <paramref name="at"/>: the whole pattern (top-left at the cell), or one random cell.</summary>
    public static void Stamp(Vector2I at, in Options o, IDictionary<Vector2I, TileCell> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (o.Erase)
        {
            if (o.Brush.Random || o.Brush.Size.X <= 1 && o.Brush.Size.Y <= 1)
                output[at] = TileCell.Empty;
            else
                foreach (var (offset, _) in o.Brush.Cells)
                    output[at + offset] = TileCell.Empty;
            return;
        }

        if (o.Brush.Random)
        {
            if (Skip(o))
                return;
            output[at] = o.Brush.PickRandom(o.Rng ?? Random.Shared, o.TileSet);
            return;
        }

        foreach (var (offset, cell) in o.Brush.Cells)
            output[at + offset] = cell;
    }

    /// <summary>The cells of a Bresenham line from <paramref name="from"/> to <paramref name="to"/>, both included.</summary>
    public static void Line(Vector2I from, Vector2I to, List<Vector2I> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        int x0 = from.X, y0 = from.Y, x1 = to.X, y1 = to.Y;
        int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
        var err = dx + dy;
        while (true)
        {
            output.Add(new Vector2I(x0, y0));
            if (x0 == x1 && y0 == y1)
                break;
            var e2 = 2 * err;
            if (e2 >= dy)
            {
                err += dy;
                x0 += sx;
            }

            if (e2 <= dx)
            {
                err += dx;
                y0 += sy;
            }
        }
    }

    /// <summary>A line of stamps (the line tool, and the gaps of a fast paint stroke).</summary>
    public static void StampLine(Vector2I from, Vector2I to, in Options o, IDictionary<Vector2I, TileCell> output)
    {
        var cells = new List<Vector2I>();
        Line(from, to, cells);
        foreach (var cell in cells)
            Stamp(cell, o, output);
    }

    /// <summary>
    /// The rectangle between two corner cells: the pattern repeated from <paramref name="from"/>'s corner, one random cell
    /// per cell, or empty cells for the eraser.
    /// </summary>
    public static void Rect(Vector2I from, Vector2I to, in Options o, IDictionary<Vector2I, TileCell> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var min = new Vector2I(Math.Min(from.X, to.X), Math.Min(from.Y, to.Y));
        var max = new Vector2I(Math.Max(from.X, to.X), Math.Max(from.Y, to.Y));
        for (var y = min.Y; y <= max.Y; y++)
            for (var x = min.X; x <= max.X; x++)
                Fill(new Vector2I(x, y), min, o, output);
    }

    /// <summary>
    /// The bucket: with <paramref name="contiguous"/>, the 4-connected region of cells equal to the clicked one; else every
    /// equal cell in the used rectangle. Filling empty space is bounded by the used rectangle grown by one chunk (or the
    /// clicked chunk's area on an empty layer).
    /// </summary>
    public static void Bucket(TileMapLayer layer, Vector2I start, bool contiguous, in Options o, IDictionary<Vector2I, TileCell> output)
    {
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(output);
        var target = layer.GetCell(start);
        if (!o.Erase && !o.Brush.Random && o.Brush.Size == Vector2I.One && o.Brush.Cells[0].Cell == target)
            return; // filling a region with what it already is
        if (o.Erase && target.IsEmpty)
            return;
        var bounds = FillBounds(layer, start, target.IsEmpty);
        if (!contiguous)
        {
            for (var y = bounds.Position.Y; y < bounds.Position.Y + bounds.Size.Y; y++)
                for (var x = bounds.Position.X; x < bounds.Position.X + bounds.Size.X; x++)
                {
                    var cell = new Vector2I(x, y);
                    if (layer.GetCell(cell) == target)
                        Fill(cell, start, o, output);
                }

            return;
        }

        var seen = new HashSet<Vector2I> { start };
        var queue = new Queue<Vector2I>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            var cell = queue.Dequeue();
            Fill(cell, start, o, output);
            foreach (var step in (ReadOnlySpan<Vector2I>)[new(1, 0), new(-1, 0), new(0, 1), new(0, -1)])
            {
                var next = cell + step;
                if (!Contains(bounds, next) || !seen.Add(next) || layer.GetCell(next) != target)
                    continue;
                queue.Enqueue(next);
            }
        }
    }

    /// <summary>The area a bucket fill may reach (cells).</summary>
    public static Rect2I FillBounds(TileMapLayer layer, Vector2I start, bool fillingEmpty)
    {
        ArgumentNullException.ThrowIfNull(layer);
        var used = layer.GetUsedRect();
        if (!fillingEmpty)
            return used;
        var grow = layer.RenderingQuadrantSize;
        if (used.Size.X == 0 || used.Size.Y == 0)
            used = new Rect2I(start.X, start.Y, 1, 1);
        var min = new Vector2I(Math.Min(used.Position.X, start.X) - grow, Math.Min(used.Position.Y, start.Y) - grow);
        var max = new Vector2I(Math.Max(used.Position.X + used.Size.X, start.X + 1) + grow, Math.Max(used.Position.Y + used.Size.Y, start.Y + 1) + grow);
        return new Rect2I(min.X, min.Y, max.X - min.X, max.Y - min.Y);
    }

    private static bool Contains(Rect2I r, Vector2I p) =>
        p.X >= r.Position.X && p.Y >= r.Position.Y && p.X < r.Position.X + r.Size.X && p.Y < r.Position.Y + r.Size.Y;

    // One cell of an area fill: the pattern cell for its offset from `origin`, a random cell, or empty.
    private static void Fill(Vector2I cell, Vector2I origin, in Options o, IDictionary<Vector2I, TileCell> output)
    {
        if (o.Erase)
        {
            output[cell] = TileCell.Empty;
            return;
        }

        if (o.Brush.Random)
        {
            if (!Skip(o))
                output[cell] = o.Brush.PickRandom(o.Rng ?? Random.Shared, o.TileSet);
            return;
        }

        var value = o.Brush.At(cell - origin);
        if (!value.IsEmpty)
            output[cell] = value;
    }

    private static bool Skip(in Options o) => o.Scattering > 0f && (o.Rng ?? Random.Shared).NextDouble() < o.Scattering;
}

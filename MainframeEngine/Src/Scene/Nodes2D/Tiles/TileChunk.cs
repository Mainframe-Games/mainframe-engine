namespace MainframeEngine;

/// <summary>
/// A square block of a <see cref="TileMapLayer"/>'s cells (<see cref="TileMapLayer.RenderingQuadrantSize"/> on a side,
/// Godot's rendering quadrant): its cells, its draw sub-items (one per z, or one per row and y-sort origin when the
/// layer is y-sorted) and its static bodies (one per physics layer with shapes).
/// </summary>
internal sealed class TileChunk
{
    public TileChunk(Vector2I coords, int size)
    {
        Coords = coords;
        Size = size;
        Cells = new TileCell[size * size];
        Array.Fill(Cells, TileCell.Empty);
    }

    /// <summary>The chunk's coordinates: floor(cell / size).</summary>
    public readonly Vector2I Coords;

    /// <summary>Cells on a side.</summary>
    public readonly int Size;

    /// <summary>Row-major cells (<see cref="TileCell.Empty"/> where none).</summary>
    public readonly TileCell[] Cells;

    /// <summary>Non-empty cells.</summary>
    public int Used;

    /// <summary>The cell at the top-left corner of the chunk.</summary>
    public Vector2I Origin => new(Coords.X * Size, Coords.Y * Size);

    /// <summary>Queued for a draw rebuild (in the layer's pending list).</summary>
    public bool RenderDirty;

    /// <summary>The sub-items drawn this rebuild, and the keys they were made for.</summary>
    public readonly List<CanvasSubItem> SubItems = [];

    public readonly List<(int Row, int YOrigin, int Z)> SubKeys = [];

    /// <summary>Sub-items kept for the next rebuild (no allocation while painting).</summary>
    public readonly List<CanvasSubItem> SparePool = [];

    /// <summary>The chunk's static bodies (Src/Physics/2D, <c>TileBodies2D</c>); null without collision.</summary>
    public List<BodyRecord2D?>? Bodies;

    /// <summary>Queued for a body rebuild.</summary>
    public bool PhysicsDirty;

    public int IndexOf(Vector2I cell) => (cell.Y - Coords.Y * Size) * Size + (cell.X - Coords.X * Size);
}

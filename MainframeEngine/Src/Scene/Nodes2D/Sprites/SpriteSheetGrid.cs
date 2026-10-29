using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// A uniform grid over an image (ADR 0223): Godot's "Select Frames" dialog layout and its <c>TileSetAtlasSource</c>
/// layout. Cells are <see cref="CellSize"/> pixels, start <see cref="Margins"/> pixels from the image's top-left corner
/// (Godot's sheet offset / atlas margins: the top and left edges only) and are <see cref="Separation"/> pixels apart.
/// </summary>
/// <param name="CellSize">One cell's size in texture pixels.</param>
/// <param name="Margins">The gap before the first column and row, in texture pixels.</param>
/// <param name="Separation">The gap between neighbouring cells, in texture pixels.</param>
public readonly record struct SpriteSheetGrid(Vector2I CellSize, Vector2I Margins = default, Vector2I Separation = default)
{
    /// <summary>
    /// The grid that splits <paramref name="imageSize"/> into <paramref name="columns"/> × <paramref name="rows"/> cells
    /// (Godot's "Horizontal/Vertical" fields): each cell is the image less the margins and the separations between
    /// cells, divided by the count (integer division, as Godot).
    /// </summary>
    public static SpriteSheetGrid FromCounts(Vector2I imageSize, int columns, int rows, Vector2I margins = default, Vector2I separation = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(columns);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rows);
        var free = imageSize - margins - new Vector2I(separation.X * (columns - 1), separation.Y * (rows - 1));
        return new SpriteSheetGrid(new Vector2I(Math.Max(0, free.X / columns), Math.Max(0, free.Y / rows)), margins, separation);
    }

    /// <summary>How many whole cells fit in <paramref name="imageSize"/> (columns, rows); (0, 0) for an empty cell size.</summary>
    public Vector2I GridSize(Vector2I imageSize)
    {
        if (CellSize.X <= 0 || CellSize.Y <= 0)
            return Vector2I.Zero;
        var area = imageSize - Margins + Separation;
        return new Vector2I(Math.Max(0, area.X / (CellSize.X + Separation.X)), Math.Max(0, area.Y / (CellSize.Y + Separation.Y)));
    }

    /// <summary>The texture-pixel rectangle of the cell at <paramref name="cell"/> (column, row).</summary>
    public Rect2I CellRect(Vector2I cell) =>
        new(Margins.X + cell.X * (CellSize.X + Separation.X), Margins.Y + cell.Y * (CellSize.Y + Separation.Y), CellSize.X, CellSize.Y);

    /// <summary>The <see cref="CellRect"/> as a float <see cref="Rect2"/> (a <see cref="SpriteFrame.Region"/>).</summary>
    public Rect2 CellRegion(Vector2I cell)
    {
        var r = CellRect(cell);
        return new Rect2(r.Position.X, r.Position.Y, r.Size.X, r.Size.Y);
    }

    /// <summary>
    /// The cell containing <paramref name="texturePixel"/>, or (-1, -1) above or left of the grid, in a separation gap,
    /// or for an empty cell size. The grid is unbounded to the right and bottom (compare with <see cref="GridSize"/>).
    /// </summary>
    public Vector2I CellAt(Vector2 texturePixel)
    {
        if (CellSize.X <= 0 || CellSize.Y <= 0)
            return new Vector2I(-1, -1);
        var x = Axis(texturePixel.X - Margins.X, CellSize.X, Separation.X);
        var y = Axis(texturePixel.Y - Margins.Y, CellSize.Y, Separation.Y);
        return x < 0 || y < 0 ? new Vector2I(-1, -1) : new Vector2I(x, y);
    }

    private static int Axis(float p, int size, int separation)
    {
        if (p < 0)
            return -1;
        var stride = size + Math.Max(0, separation);
        var index = (int)MathF.Floor(p / stride);
        return p - index * stride < size ? index : -1;
    }
}

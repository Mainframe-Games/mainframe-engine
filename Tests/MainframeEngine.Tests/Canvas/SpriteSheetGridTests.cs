using System.Numerics;

namespace MainframeEngine.Tests.Canvas;

/// <summary>ADR 0223: the grid maths of Godot's "Select Frames" dialog (and later the tile set atlas layout).</summary>
public sealed class SpriteSheetGridTests
{
    [Fact]
    public void CountsAndSizesDescribeTheSameGrid()
    {
        // A 128×64 sheet of 4×2 cells.
        var grid = SpriteSheetGrid.FromCounts(new Vector2I(128, 64), 4, 2);
        Assert.Equal(new Vector2I(32, 32), grid.CellSize);
        Assert.Equal(new Vector2I(4, 2), grid.GridSize(new Vector2I(128, 64)));
        Assert.Equal(new Rect2I(96, 32, 32, 32), grid.CellRect(new Vector2I(3, 1)));
        Assert.Equal(new Rect2(96, 32, 32, 32), grid.CellRegion(new Vector2I(3, 1)));

        // The same sheet sliced by size: partial cells at the far edges are left out.
        Assert.Equal(new Vector2I(4, 2), new SpriteSheetGrid(new Vector2I(30, 30)).GridSize(new Vector2I(128, 64)));
    }

    [Fact]
    public void MarginsApplyToTheTopLeftAndSeparationBetweenCells()
    {
        // Godot: offset 2,3, separation 1, 3×2 cells of 10×8 in a 2 + 3·10 + 2·1 = 34 wide, 3 + 2·8 + 1 = 20 high sheet.
        var image = new Vector2I(34, 20);
        var grid = SpriteSheetGrid.FromCounts(image, 3, 2, new Vector2I(2, 3), new Vector2I(1, 1));
        Assert.Equal(new Vector2I(10, 8), grid.CellSize);
        Assert.Equal(new Vector2I(3, 2), grid.GridSize(image));
        Assert.Equal(new Rect2I(2, 3, 10, 8), grid.CellRect(Vector2I.Zero));
        Assert.Equal(new Rect2I(24, 12, 10, 8), grid.CellRect(new Vector2I(2, 1)));
        // Integer division, as Godot's dialog: one spare pixel is not a cell.
        Assert.Equal(new Vector2I(10, 8), SpriteSheetGrid.FromCounts(image + new Vector2I(1, 1), 3, 2, new Vector2I(2, 3), new Vector2I(1, 1)).CellSize);
    }

    [Fact]
    public void CellAtFindsCellsAndRejectsGapsAndMargins()
    {
        var grid = new SpriteSheetGrid(new Vector2I(10, 8), new Vector2I(2, 3), new Vector2I(1, 1));
        Assert.Equal(new Vector2I(0, 0), grid.CellAt(new Vector2(2, 3)));
        Assert.Equal(new Vector2I(0, 0), grid.CellAt(new Vector2(11.9f, 10.9f)));
        Assert.Equal(new Vector2I(-1, -1), grid.CellAt(new Vector2(12.5f, 4))); // the separation column
        Assert.Equal(new Vector2I(1, 1), grid.CellAt(new Vector2(13, 12)));
        Assert.Equal(new Vector2I(-1, -1), grid.CellAt(new Vector2(1, 5))); // inside the left margin
        Assert.Equal(new Vector2I(-1, -1), grid.CellAt(new Vector2(5, -1)));
        Assert.Equal(new Vector2I(-1, -1), new SpriteSheetGrid(Vector2I.Zero).CellAt(Vector2.One));
    }

    [Fact]
    public void BadCountsThrowAndEmptyGridsHaveNoCells()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SpriteSheetGrid.FromCounts(new Vector2I(8, 8), 0, 1));
        Assert.Equal(Vector2I.Zero, new SpriteSheetGrid(new Vector2I(0, 4)).GridSize(new Vector2I(64, 64)));
        Assert.Equal(Vector2I.Zero, SpriteSheetGrid.FromCounts(new Vector2I(2, 2), 4, 4, new Vector2I(4, 4)).CellSize);
    }
}

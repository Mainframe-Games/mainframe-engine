namespace MainframeEngine.Editor;

/// <summary>
/// One paint stroke on a <see cref="TileMapLayer"/> ("Paint Tiles", "Erase Tiles", "Fill Tiles", "Paste Tiles"): every
/// changed cell with its value before and after. Strokes apply live and commit once on release
/// (<c>alreadyApplied</c>); undo writes the old cells back through <see cref="TileMapLayer.SetCellData"/>.
/// </summary>
public sealed class PaintTilesAction : IEditorAction
{
    private readonly (Vector2I Coords, TileCell Before, TileCell After)[] _cells;

    public PaintTilesAction(string name, TileMapLayer layer, IEnumerable<(Vector2I Coords, TileCell Before, TileCell After)> cells)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(cells);
        Name = name;
        Layer = layer;
        _cells = [.. cells];
    }

    public string Name { get; }

    public TileMapLayer Layer { get; }

    public IReadOnlyList<(Vector2I Coords, TileCell Before, TileCell After)> Cells => _cells;

    public void Do()
    {
        foreach (var (coords, _, after) in _cells)
            Layer.SetCellData(coords, after);
    }

    public void Undo()
    {
        for (var i = _cells.Length - 1; i >= 0; i--)
            Layer.SetCellData(_cells[i].Coords, _cells[i].Before);
    }
}

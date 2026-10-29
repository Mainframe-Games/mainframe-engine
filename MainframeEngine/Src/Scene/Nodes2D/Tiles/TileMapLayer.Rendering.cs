using System.Numerics;

namespace MainframeEngine;

public partial class TileMapLayer
{
    private bool _warnedInvalid;

    /// <summary>Rebuilds the draw lists of the chunks whose cells changed (the layer's own list stays empty).</summary>
    private protected override void DrawBuiltin() => RebuildPendingChunks();

    private void RebuildPendingChunks()
    {
        if (_pendingRender.Count > 0)
        {
            for (var i = 0; i < _pendingRender.Count; i++)
            {
                var chunk = _pendingRender[i];
                chunk.RenderDirty = false;
                var before = chunk.SubItems.Count;
                RebuildChunk(chunk);
                if (chunk.SubItems.Count != before)
                    _subsDirty = true;
            }

            _pendingRender.Clear();
        }

        EnsureOrder();
        if (!_subsDirty)
            return;
        _subsDirty = false;
        _subs.Clear();
        foreach (var chunk in _ordered)
            _subs.AddRange(chunk.SubItems);
    }

    // One textured quad per cell in Godot's order (rows top to bottom; x reversed with XDrawOrderReversed): non-y-sorted
    // chunks get a sub-item per z, y-sorted ones a sub-item per (row, tile y-sort origin, z) placed at the row's sort
    // position, as Godot's y-sorted quadrants.
    private void RebuildChunk(TileChunk chunk)
    {
        foreach (var sub in chunk.SubItems)
        {
            sub.DrawList.Clear();
            chunk.SparePool.Add(sub);
        }

        chunk.SubItems.Clear();
        chunk.SubKeys.Clear();
        if (!_enabled || _tileSet is not { } set)
            return;

        var ySort = YSortEnabled;
        var size = chunk.Size;
        var origin = chunk.Origin;
        var tileSize = set.TileSize;
        CanvasSubItem? last = null;
        (int Row, int YOrigin, int Z) lastKey = default;
        for (var row = 0; row < size; row++)
        {
            for (var k = 0; k < size; k++)
            {
                var column = _xReversed ? size - 1 - k : k;
                var cell = chunk.Cells[row * size + column];
                if (cell.IsEmpty)
                    continue;
                if (set.GetSource(cell.SourceId) is not { } source || !source.HasTile(cell.AtlasCoords))
                {
                    WarnInvalid(origin + new Vector2I(column, row), cell);
                    continue;
                }

                if (!source.TryGetRuntimeRegion(cell.AtlasCoords, out var texture, out var region))
                    continue;
                var data = source.GetTileData(cell.AtlasCoords, cell.Alternative);
                var coords = new Vector2I(origin.X + column, origin.Y + row);
                var centre = new Vector2((coords.X + 0.5f) * tileSize.X, (coords.Y + 0.5f) * tileSize.Y);
                var key = ySort ? (row, data.YSortOrigin, data.ZIndex) : (0, 0, data.ZIndex);
                if (last is null || key != lastKey)
                {
                    last = SubItemFor(chunk, key, ySort ? new Vector2(origin.X * tileSize.X, centre.Y + data.YSortOrigin + _ySortOrigin) : Vector2.Zero);
                    lastKey = key;
                }

                DrawTile(last, centre - last.Transform.Origin, texture, region, data, cell);
            }
        }
    }

    private static CanvasSubItem SubItemFor(TileChunk chunk, (int Row, int YOrigin, int Z) key, Vector2 position)
    {
        for (var i = 0; i < chunk.SubKeys.Count; i++)
            if (chunk.SubKeys[i] == key)
                return chunk.SubItems[i];
        CanvasSubItem sub;
        if (chunk.SparePool.Count > 0)
        {
            sub = chunk.SparePool[^1];
            chunk.SparePool.RemoveAt(chunk.SparePool.Count - 1);
        }
        else
        {
            sub = new CanvasSubItem();
        }

        sub.Transform = Transform2D.FromTrs(position, 0f, Vector2.One);
        sub.ZOffset = key.Z;
        chunk.SubItems.Add(sub);
        chunk.SubKeys.Add(key);
        return sub;
    }

    // Godot's TileMapLayer::draw_tile: the region centred on the cell, shifted by the texture origin; flips mirror the
    // quad (negative sizes), a transpose swaps the axes.
    private static void DrawTile(CanvasSubItem sub, Vector2 position, Texture2D texture, Rect2 region, TileData data, TileCell cell)
    {
        var transpose = cell.Transpose;
        var size = region.Size;
        var origin = new Vector2(data.TextureOrigin.X, data.TextureOrigin.Y);
        var dest = new Rect2(position - (transpose ? new Vector2(size.Y, size.X) : size) / 2f - origin, size);
        if (cell.FlipH)
            dest.Size.X = -dest.Size.X;
        if (cell.FlipV)
            dest.Size.Y = -dest.Size.Y;
        CanvasPrimitives.TextureRectRegion(sub.DrawList, texture, dest, region, data.Modulate, transpose);
    }

    // Godot's fix_invalid_tiles warns about cells naming a missing source or tile; they are kept and not drawn.
    private void WarnInvalid(Vector2I coords, TileCell cell)
    {
        if (_warnedInvalid)
            return;
        _warnedInvalid = true;
        Log.Warning($"[TileMapLayer] '{Name}': cell {coords} names source {cell.SourceId} tile {cell.AtlasCoords}, which the tile set does not have; it is kept but not drawn.");
    }
}

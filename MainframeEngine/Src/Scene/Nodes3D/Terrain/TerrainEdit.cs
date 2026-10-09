namespace MainframeEngine;

/// <summary>
/// Records a terrain edit (an editor stroke, a ramp, a rectangle fill) for undo: <see cref="Begin"/> starts recording
/// the given layers; the first time a write touches a chunk, that chunk's tile of the layer is copied (heights
/// <c>(ChunkQuads + 1)²</c>, cells <c>(ChunkQuads · CellsPerQuad)²</c> per image). While it records, height and water
/// writes rebuild chunk meshes at once but collision only at <see cref="End"/>, which returns the before and after
/// tiles.
/// </summary>
public sealed class TerrainEdit
{
    private readonly Terrain3D _terrain;
    private readonly Dictionary<(TerrainLayers Layer, int Image, int Chunk), TerrainEditTile> _tiles = [];
    private TerrainData? _data;

    internal TerrainEdit(Terrain3D terrain) => _terrain = terrain;

    /// <summary>True between <see cref="Begin"/> and <see cref="End"/>.</summary>
    public bool IsActive => _data is not null;

    /// <summary>The layers being recorded.</summary>
    public TerrainLayers Layers { get; private set; }

    /// <summary>Starts recording writes to <paramref name="layers"/> of the terrain's data.</summary>
    public void Begin(TerrainLayers layers)
    {
        if (IsActive)
            throw new InvalidOperationException("A terrain edit is already recording; call End first.");
        var data = _terrain.Data ?? throw new InvalidOperationException("The terrain has no data to edit.");
        if (data.Recorder is not null)
            throw new InvalidOperationException("Another terrain edit is recording this data.");
        data.EnsureLoaded();
        _tiles.Clear();
        Layers = layers;
        _data = data;
        data.Recorder = this;
    }

    /// <summary>Stops recording, rebuilds the touched chunks' collision and returns the before and after tiles.</summary>
    public TerrainEditRecord End()
    {
        var data = _data ?? throw new InvalidOperationException("No terrain edit is recording.");
        data.Recorder = null;
        _data = null;
        var tiles = new List<TerrainEditTile>(_tiles.Count);
        foreach (var tile in _tiles.Values)
        {
            tile.After = Read(data, tile, out var heights);
            tile.AfterHeights = heights;
            tiles.Add(tile);
        }

        _tiles.Clear();
        _terrain.FlushPendingCollision();
        return new TerrainEditRecord(data, Layers, tiles);
    }

    /// <summary>Copies the tiles <paramref name="rect"/> touches (vertices for height and water, cells otherwise) before a write.</summary>
    internal void Capture(TerrainLayers layer, Rect2I rect)
    {
        var data = _data!;
        if ((layer & Layers) == 0)
            return;
        var images = layer == TerrainLayers.Surface && data.Profile == TerrainProfile.Realistic ? 2 : 1;
        var chunks = data.ChunksPerSide;
        int cx0, cz0, cx1, cz1;
        if (layer is TerrainLayers.Height or TerrainLayers.Water)
        {
            var q = data.ChunkQuads;
            cx0 = Math.Max(0, (rect.Position.X - 1) / q);
            cz0 = Math.Max(0, (rect.Position.Y - 1) / q);
            cx1 = Math.Min(chunks - 1, (rect.End.X - 1) / q);
            cz1 = Math.Min(chunks - 1, (rect.End.Y - 1) / q);
        }
        else
        {
            var c = data.ChunkQuads * data.CellsPerQuad;
            cx0 = rect.Position.X / c;
            cz0 = rect.Position.Y / c;
            cx1 = Math.Min(chunks - 1, (rect.End.X - 1) / c);
            cz1 = Math.Min(chunks - 1, (rect.End.Y - 1) / c);
        }

        for (var cz = cz0; cz <= cz1; cz++)
            for (var cx = cx0; cx <= cx1; cx++)
                for (var image = 0; image < images; image++)
                {
                    var key = (layer, image, cz * chunks + cx);
                    if (_tiles.ContainsKey(key))
                        continue;
                    var tile = new TerrainEditTile(layer, image, cx, cz, TileRect(data, layer, cx, cz));
                    tile.Before = Read(data, tile, out var heights);
                    tile.BeforeHeights = heights;
                    _tiles.Add(key, tile);
                }
    }

    private static Rect2I TileRect(TerrainData data, TerrainLayers layer, int cx, int cz)
    {
        if (layer is TerrainLayers.Height or TerrainLayers.Water)
        {
            var q = data.ChunkQuads;
            return new Rect2I(cx * q, cz * q, q + 1, q + 1);
        }

        var c = data.ChunkQuads * data.CellsPerQuad;
        return new Rect2I(cx * c, cz * c, c, c);
    }

    private static uint[]? Read(TerrainData data, TerrainEditTile tile, out float[]? heights)
    {
        if (tile.Layer == TerrainLayers.Height)
        {
            heights = new float[tile.Rect.Area];
            data.GetHeights(tile.Rect, heights);
            return null;
        }

        heights = null;
        var texels = new uint[tile.Rect.Area];
        data.GetCells(tile.Layer, tile.Image, tile.Rect, texels);
        return texels;
    }
}

/// <summary>One chunk's tile of one layer image, before and after an edit.</summary>
public sealed class TerrainEditTile
{
    internal TerrainEditTile(TerrainLayers layer, int image, int chunkX, int chunkZ, Rect2I rect)
    {
        Layer = layer;
        Image = image;
        ChunkX = chunkX;
        ChunkZ = chunkZ;
        Rect = rect;
    }

    public TerrainLayers Layer { get; }
    public int Image { get; }
    public int ChunkX { get; }
    public int ChunkZ { get; }

    /// <summary>The tile's rectangle: vertices for height and water, cells otherwise.</summary>
    public Rect2I Rect { get; }

    /// <summary>Heights before and after (height tiles).</summary>
    public float[]? BeforeHeights { get; internal set; }

    public float[]? AfterHeights { get; internal set; }

    /// <summary>Packed RGBA texels before and after (other layers).</summary>
    public uint[]? Before { get; internal set; }

    public uint[]? After { get; internal set; }
}

/// <summary>What <see cref="TerrainEdit.End"/> recorded: <see cref="Undo"/> and <see cref="Redo"/> write the tiles back.</summary>
public sealed class TerrainEditRecord
{
    private readonly TerrainData _data;

    internal TerrainEditRecord(TerrainData data, TerrainLayers layers, IReadOnlyList<TerrainEditTile> tiles)
    {
        _data = data;
        Layers = layers;
        Tiles = tiles;
    }

    public TerrainLayers Layers { get; }

    public IReadOnlyList<TerrainEditTile> Tiles { get; }

    /// <summary>Writes the before tiles back (meshes and collision rebuild; <see cref="Terrain3D.Changed"/> has <c>FromUndo</c>).</summary>
    public void Undo()
    {
        foreach (var tile in Tiles)
            _data.RestoreTile(tile.Layer, tile.Image, tile.Rect, tile.BeforeHeights, tile.Before);
    }

    /// <summary>Writes the after tiles back.</summary>
    public void Redo()
    {
        foreach (var tile in Tiles)
            _data.RestoreTile(tile.Layer, tile.Image, tile.Rect, tile.AfterHeights, tile.After);
    }
}

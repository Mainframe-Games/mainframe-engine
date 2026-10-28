using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// A texture cut into tiles (Godot's <c>TileSetAtlasSource</c>): the grid is a <see cref="SpriteSheetGrid"/> from
/// <see cref="Margins"/>, <see cref="Separation"/> and <see cref="TextureRegionSize"/>. Tiles are created explicitly
/// (<see cref="Tiles"/>); a cell may only show a created tile. <see cref="TileOverrides"/> holds a
/// <see cref="TileData"/> only for tiles (and alternatives) whose data differs from the defaults.
/// </summary>
/// <remarks>
/// Alternative ids carry Godot 4.2's transform flags in their high bits (<see cref="TransformFlipH"/>,
/// <see cref="TransformFlipV"/>, <see cref="TransformTranspose"/>); user alternatives are 1…4095. With
/// <see cref="UseTexturePadding"/> the source draws from a copy of the atlas in which every tile is surrounded by a
/// 1 px copy of its own edge (Godot's padding), so linear filtering and fractional zoom never bleed a neighbour in.
/// </remarks>
[EditorIcon("photo")]
public sealed class TileSetAtlasSource : Resource
{
    /// <summary>Flips the tile horizontally (Godot's <c>TRANSFORM_FLIP_H</c>).</summary>
    public const int TransformFlipH = 1 << 12;

    /// <summary>Flips the tile vertically (Godot's <c>TRANSFORM_FLIP_V</c>).</summary>
    public const int TransformFlipV = 1 << 13;

    /// <summary>Swaps the tile's axes (Godot's <c>TRANSFORM_TRANSPOSE</c>; with a flip: a quarter turn).</summary>
    public const int TransformTranspose = 1 << 14;

    /// <summary>The bits of an alternative id that are the alternative itself (the rest are transform flags).</summary>
    public const int AlternativeMask = TransformFlipH - 1;

    private int _id;
    private Texture2D? _texture;
    private Vector2I _margins;
    private Vector2I _separation;
    private Vector2I _regionSize = new(16, 16);
    private bool _padding = true;
    private Vector2I[] _tiles = [];
    private List<TileData> _overrides = [];
    private readonly HashSet<Vector2I> _tileSet = [];
    private readonly Dictionary<long, TileData> _data = [];
    private bool _cacheDirty = true;

    // The padded atlas (built lazily on the main thread, when drawn).
    private Texture2D? _padded;
    private Texture2D? _paddedFrom;
    private int _paddedVersion = -1;
    private SpriteSheetGrid _paddedGrid;

    /// <summary>The id cells store (Godot's source id); unique within the tile set.</summary>
    [Export]
    public int Id
    {
        get => _id;
        set
        {
            if (_id == value)
                return;
            _id = value;
            EmitChanged();
        }
    }

    [Export]
    public Texture2D? Texture
    {
        get => _texture;
        set
        {
            if (ReferenceEquals(_texture, value))
                return;
            _texture = value;
            Touch(regrid: true);
        }
    }

    /// <summary>Pixels before the first column and row.</summary>
    [Export]
    public Vector2I Margins
    {
        get => _margins;
        set => Set(ref _margins, value);
    }

    /// <summary>Pixels between neighbouring tiles.</summary>
    [Export]
    public Vector2I Separation
    {
        get => _separation;
        set => Set(ref _separation, value);
    }

    /// <summary>One tile's region in the texture (pixels).</summary>
    [Export]
    public Vector2I TextureRegionSize
    {
        get => _regionSize;
        set => Set(ref _regionSize, new Vector2I(Math.Max(1, value.X), Math.Max(1, value.Y)));
    }

    /// <summary>Draws from a padded copy of the atlas (1 px edge copies around every tile).</summary>
    [Export]
    public bool UseTexturePadding
    {
        get => _padding;
        set => Set(ref _padding, value);
    }

    /// <summary>The atlas coordinates of the created tiles.</summary>
    [Export]
    public Vector2I[] Tiles
    {
        get => _tiles;
        set
        {
            _tiles = value ?? [];
            Touch();
        }
    }

    /// <summary>The data of tiles and alternatives with non-default values (only those are saved).</summary>
    [Export]
    public List<TileData> TileOverrides
    {
        get => _overrides;
        set
        {
            foreach (var data in _overrides)
                if (data is not null)
                    data.Changed -= OnDataChanged;
            _overrides = value ?? [];
            foreach (var data in _overrides)
                if (data is not null)
                    data.Changed += OnDataChanged;
            Touch();
        }
    }

    /// <summary>The atlas grid (from the margins, separation and region size).</summary>
    public SpriteSheetGrid Grid => new(_regionSize, _margins, _separation);

    /// <summary>The number of whole tiles across and down the texture ((0, 0) without one).</summary>
    public Vector2I GetAtlasGridSize() => _texture is null ? Vector2I.Zero : Grid.GridSize(new Vector2I(_texture.Width, _texture.Height));

    /// <summary>True when the tile at <paramref name="atlasCoords"/> was created.</summary>
    public bool HasTile(Vector2I atlasCoords)
    {
        EnsureCache();
        return _tileSet.Contains(atlasCoords);
    }

    /// <summary>Creates the tile at <paramref name="atlasCoords"/> (no-op when it exists).</summary>
    public void CreateTile(Vector2I atlasCoords)
    {
        if (atlasCoords.X < 0 || atlasCoords.Y < 0 || atlasCoords.X > ushort.MaxValue - 1 || atlasCoords.Y > ushort.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(atlasCoords), atlasCoords, "Atlas coordinates must be in 0…65534.");
        if (HasTile(atlasCoords))
            return;
        Tiles = [.. _tiles, atlasCoords];
    }

    /// <summary>Removes the tile at <paramref name="atlasCoords"/> and its data (alternatives included).</summary>
    public void RemoveTile(Vector2I atlasCoords)
    {
        if (!HasTile(atlasCoords))
            return;
        var tiles = new List<Vector2I>(_tiles);
        tiles.Remove(atlasCoords);
        for (var i = _overrides.Count - 1; i >= 0; i--)
            if (_overrides[i] is { } data && data.AtlasCoords == atlasCoords)
            {
                data.Changed -= OnDataChanged;
                _overrides.RemoveAt(i);
            }

        Tiles = [.. tiles];
    }

    /// <summary>
    /// The data of the tile (or alternative: the transform flags of <paramref name="alternative"/> are ignored) at
    /// <paramref name="atlasCoords"/>: its override, else the base tile's, else a shared default instance.
    /// </summary>
    public TileData GetTileData(Vector2I atlasCoords, int alternative = 0)
    {
        EnsureCache();
        if (_data.TryGetValue(Key(atlasCoords, alternative & AlternativeMask), out var data))
            return data;
        if ((alternative & AlternativeMask) != 0 && _data.TryGetValue(Key(atlasCoords, 0), out var baseData))
            return baseData;
        return TileData.Default;
    }

    /// <summary>The editable data of a created tile or alternative: its override, created when missing.</summary>
    public TileData GetOrCreateTileData(Vector2I atlasCoords, int alternative = 0)
    {
        alternative &= AlternativeMask;
        EnsureCache();
        if (_data.TryGetValue(Key(atlasCoords, alternative), out var data))
            return data;
        data = new TileData { AtlasCoords = atlasCoords, AlternativeId = alternative };
        data.Changed += OnDataChanged;
        _overrides.Add(data);
        Touch();
        return data;
    }

    /// <summary>Creates an alternative of a created tile (its data copies the base tile's) and returns its id.</summary>
    public int CreateAlternativeTile(Vector2I atlasCoords)
    {
        if (!HasTile(atlasCoords))
            throw new ArgumentException($"No tile at {atlasCoords}.", nameof(atlasCoords));
        var id = 1;
        while (_data.ContainsKey(Key(atlasCoords, id)))
            id++;
        if (id > AlternativeMask)
            throw new InvalidOperationException("A tile has at most 4095 alternatives.");
        var copy = (TileData)GetTileData(atlasCoords).Duplicate(deep: true);
        copy.AtlasCoords = atlasCoords;
        copy.AlternativeId = id;
        copy.Changed += OnDataChanged;
        _overrides.Add(copy);
        Touch();
        return id;
    }

    /// <summary>Appends the alternative ids of the tile at <paramref name="atlasCoords"/> (0 first) to <paramref name="results"/>.</summary>
    public int GetAlternativeTiles(Vector2I atlasCoords, List<int> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        if (!HasTile(atlasCoords))
            return 0;
        var start = results.Count;
        results.Add(0);
        foreach (var data in _overrides)
            if (data is not null && data.AtlasCoords == atlasCoords && data.AlternativeId != 0 && !results.Contains(data.AlternativeId))
                results.Add(data.AlternativeId);
        results.Sort(start, results.Count - start, Comparer<int>.Default);
        return results.Count - start;
    }

    /// <summary>The tile's region in <see cref="Texture"/> (pixels).</summary>
    public Rect2I GetTileTextureRegion(Vector2I atlasCoords) => Grid.CellRect(atlasCoords);

    /// <summary>
    /// The texture the tiles draw from (the padded copy with <see cref="UseTexturePadding"/>) and the tile's region in
    /// it. Builds the padded copy on first use and when the texture changes (main thread, load or edit time).
    /// </summary>
    public bool TryGetRuntimeRegion(Vector2I atlasCoords, out Texture2D texture, out Rect2 region)
    {
        texture = null!;
        region = default;
        if (_texture is not { } source)
            return false;
        if (_padding && EnsurePadded(source) is { } padded)
        {
            texture = padded;
            var r = _paddedGrid.CellRect(atlasCoords);
            region = new Rect2(r.Position.X, r.Position.Y, r.Size.X, r.Size.Y);
            return true;
        }

        texture = source;
        var cell = Grid.CellRect(atlasCoords);
        region = new Rect2(cell.Position.X, cell.Position.Y, cell.Size.X, cell.Size.Y);
        return true;
    }

    /// <summary>
    /// Creates a tile in every grid cell that has a pixel with non-zero alpha (Godot's "Create tiles in non-transparent
    /// texture regions"); returns how many were created. Reads the texture's pixels.
    /// </summary>
    public int CreateTilesFromOpaqueRegions()
    {
        if (_texture is not { } texture)
            return 0;
        var (rgba, width, height) = texture.DecodePixels();
        var grid = Grid;
        var size = grid.GridSize(new Vector2I(width, height));
        var created = new List<Vector2I>(_tiles);
        var added = 0;
        for (var y = 0; y < size.Y; y++)
            for (var x = 0; x < size.X; x++)
            {
                var cell = new Vector2I(x, y);
                if (HasTile(cell) || !HasOpaquePixel(rgba, width, height, grid.CellRect(cell)))
                    continue;
                created.Add(cell);
                added++;
            }

        if (added > 0)
            Tiles = [.. created];
        return added;
    }

    private static bool HasOpaquePixel(byte[] rgba, int width, int height, Rect2I rect)
    {
        var x1 = Math.Min(width, rect.Position.X + rect.Size.X);
        var y1 = Math.Min(height, rect.Position.Y + rect.Size.Y);
        for (var y = Math.Max(0, rect.Position.Y); y < y1; y++)
            for (var x = Math.Max(0, rect.Position.X); x < x1; x++)
                if (rgba[(y * width + x) * 4 + 3] != 0)
                    return true;
        return false;
    }

    // Godot's _update_padded_texture: every grid cell copied 1 px apart from its neighbours with its edge pixels repeated.
    private Texture2D? EnsurePadded(Texture2D source)
    {
        if (_padded is not null && ReferenceEquals(_paddedFrom, source) && _paddedVersion == source.Version)
            return _padded;
        if (source.Viewport is not null)
            return null; // a viewport texture has no CPU pixels: draw from it directly
        byte[] rgba;
        int width, height;
        try
        {
            (rgba, width, height) = source.DecodePixels();
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or InvalidDataException)
        {
            Log.Warning($"[TileSet] Cannot pad '{source}': {e.Message}; drawing from the texture directly.");
            _padding = false;
            return null;
        }

        var grid = Grid;
        var count = grid.GridSize(new Vector2I(width, height));
        if (count.X == 0 || count.Y == 0)
            return null;
        var cell = _regionSize;
        var stride = cell + new Vector2I(2, 2);
        var paddedWidth = count.X * stride.X;
        var paddedHeight = count.Y * stride.Y;
        var output = new byte[paddedWidth * paddedHeight * 4];
        for (var ty = 0; ty < count.Y; ty++)
            for (var tx = 0; tx < count.X; tx++)
            {
                var src = grid.CellRect(new Vector2I(tx, ty));
                var dx = tx * stride.X;
                var dy = ty * stride.Y;
                for (var y = -1; y <= cell.Y; y++)
                {
                    var sy = Math.Clamp(src.Position.Y + Math.Clamp(y, 0, cell.Y - 1), 0, height - 1);
                    for (var x = -1; x <= cell.X; x++)
                    {
                        var sx = Math.Clamp(src.Position.X + Math.Clamp(x, 0, cell.X - 1), 0, width - 1);
                        var s = (sy * width + sx) * 4;
                        var d = ((dy + y + 1) * paddedWidth + dx + x + 1) * 4;
                        output[d] = rgba[s];
                        output[d + 1] = rgba[s + 1];
                        output[d + 2] = rgba[s + 2];
                        output[d + 3] = rgba[s + 3];
                    }
                }
            }

        if (_padded is not null && _padded.Width == paddedWidth && _padded.Height == paddedHeight && ReferenceEquals(_paddedFrom, source))
            _padded.SetPixels(output);
        else
            _padded = Texture2D.FromPixels(paddedWidth, paddedHeight, output, source.ImportSettings);
        _padded.ResourceName = source.ResourceName.Length > 0 ? source.ResourceName + " (padded)" : "padded atlas";
        _paddedFrom = source;
        _paddedVersion = source.Version;
        _paddedGrid = new SpriteSheetGrid(cell, Vector2I.One, new Vector2I(2, 2));
        return _padded;
    }

    /// <summary>Raises <see cref="Resource.Changed"/> after <see cref="TileOverrides"/> was edited in place.</summary>
    public void NotifyChanged() => Touch();

    private static long Key(Vector2I coords, int alternative) => ((long)(coords.X & 0xFFFF) << 32) | ((long)(coords.Y & 0xFFFF) << 16) | (long)(alternative & 0xFFFF);

    private void EnsureCache()
    {
        if (!_cacheDirty)
            return;
        _tileSet.Clear();
        foreach (var tile in _tiles)
            _tileSet.Add(tile);
        _data.Clear();
        foreach (var data in _overrides)
            if (data is not null)
                _data.TryAdd(Key(data.AtlasCoords, data.AlternativeId), data);
        _cacheDirty = false;
    }

    private void OnDataChanged()
    {
        _cacheDirty = true; // a tile's coordinates or alternative id may have changed
        EmitChanged();
    }

    private void Set<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        Touch(regrid: true);
    }

    private void Touch(bool regrid = false)
    {
        _cacheDirty = true;
        if (regrid)
            _paddedVersion = -1; // the grid or texture changed: rebuild the padded copy
        EmitChanged();
    }
}

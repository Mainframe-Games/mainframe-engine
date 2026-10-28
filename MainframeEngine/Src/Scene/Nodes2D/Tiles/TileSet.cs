namespace MainframeEngine;

/// <summary>
/// A physics layer of a <see cref="TileSet"/> (Godot's tile set physics layers): the collision layer and mask and the
/// physics material of every tile polygon on it. A <see cref="TileMapLayer"/> builds one static body per chunk per
/// physics layer that has shapes.
/// </summary>
[EditorIcon("atom-2")]
public sealed class TileSetPhysicsLayer : Resource
{
    private uint _layer = CollisionLayers.Default;
    private uint _mask = CollisionLayers.Default;
    private PhysicsMaterial? _material;

    [Export(Flags = true)]
    public uint CollisionLayer
    {
        get => _layer;
        set
        {
            if (_layer == value)
                return;
            _layer = value;
            EmitChanged();
        }
    }

    [Export(Flags = true)]
    public uint CollisionMask
    {
        get => _mask;
        set
        {
            if (_mask == value)
                return;
            _mask = value;
            EmitChanged();
        }
    }

    /// <summary>Friction and bounce of the tiles' shapes (null: the defaults).</summary>
    [Export]
    public PhysicsMaterial? PhysicsMaterial
    {
        get => _material;
        set
        {
            if (ReferenceEquals(_material, value))
                return;
            if (_material is not null)
                _material.Changed -= EmitChangedHandler;
            _material = value;
            if (_material is not null)
                _material.Changed += EmitChangedHandler;
            EmitChanged();
        }
    }

    private void EmitChangedHandler() => EmitChanged();
}

/// <summary>
/// A named, typed value every tile can carry (Godot's custom data layers): <c>"damage"</c> (int), <c>"surface"</c>
/// (string, for footstep sounds), <c>"solid"</c> (bool, for a navigation grid). Values live in
/// <see cref="TileData.CustomData"/>, read with the typed getters (<see cref="TileData.GetCustomInt"/>…).
/// </summary>
[EditorIcon("database")]
public sealed class TileSetCustomDataLayer : Resource
{
    private string _name = "";
    private CustomDataType _type;

    [Export]
    public string Name
    {
        get => _name;
        set
        {
            value ??= "";
            if (_name == value)
                return;
            _name = value;
            EmitChanged();
        }
    }

    [Export]
    public CustomDataType Type
    {
        get => _type;
        set
        {
            if (_type == value)
                return;
            _type = value;
            EmitChanged();
        }
    }
}

/// <summary>
/// The tiles a <see cref="TileMapLayer"/> paints with (Godot 4's <c>TileSet</c>, square tiles only): the cell size,
/// physics layers, custom data layers and atlas sources (a texture cut into a <see cref="SpriteSheetGrid"/>). Any
/// change to the set or to a source, tile or polygon raises <see cref="Resource.Changed"/>, and layers using it
/// redraw and rebuild their bodies. Lookups by id and name are cached and allocate nothing.
/// </summary>
/// <remarks>
/// Navigation (G3): a grid path finder reads a layer through <see cref="TileMapLayer.GetUsedCells"/>,
/// <see cref="TileMapLayer.GetCellTileData"/> and either collision on a physics layer or a custom data bool layer.
/// Godot's per-tile navigation polygons (<c>TileSetNavigationLayer</c>) are reserved for the navigation milestone.
/// </remarks>
[EditorIcon("layout-grid")]
public sealed class TileSet : Resource
{
    private TileShape _shape;
    private Vector2I _tileSize = new(16, 16);
    private bool _uvClipping;
    private List<TileSetPhysicsLayer> _physicsLayers = [];
    private List<TileSetCustomDataLayer> _customDataLayers = [];
    private List<TileSetAtlasSource> _sources = [];
    private readonly Dictionary<int, TileSetAtlasSource> _byId = [];
    private bool _idsDirty = true;
    private int _version = 1;

    /// <summary>The cell shape; only <see cref="TileShape.Square"/> is supported (the others are reserved).</summary>
    [Export]
    public TileShape TileShape
    {
        get => _shape;
        set
        {
            if (_shape == value)
                return;
            _shape = value;
            Touch();
        }
    }

    /// <summary>One cell in pixels (Godot's <c>tile_size</c>).</summary>
    [Export]
    public Vector2I TileSize
    {
        get => _tileSize;
        set
        {
            value = new Vector2I(Math.Max(1, value.X), Math.Max(1, value.Y));
            if (_tileSize == value)
                return;
            _tileSize = value;
            Touch();
        }
    }

    /// <summary>
    /// Godot's half-texel UV inset, kept for compatibility; tile edges are protected by
    /// <see cref="TileSetAtlasSource.UseTexturePadding"/> instead (docs/design/tile-maps.md).
    /// </summary>
    [Export]
    public bool UvClipping
    {
        get => _uvClipping;
        set
        {
            if (_uvClipping == value)
                return;
            _uvClipping = value;
            Touch();
        }
    }

    [Export]
    public List<TileSetPhysicsLayer> PhysicsLayers
    {
        get => _physicsLayers;
        set => SetList(ref _physicsLayers, value);
    }

    [Export]
    public List<TileSetCustomDataLayer> CustomDataLayers
    {
        get => _customDataLayers;
        set => SetList(ref _customDataLayers, value);
    }

    [Export]
    public List<TileSetAtlasSource> Sources
    {
        get => _sources;
        set => SetList(ref _sources, value);
    }

    /// <summary>Changes whenever the set, a source, a tile or a polygon changes (layers compare it).</summary>
    public int Version => _version;

    /// <summary>The source with <paramref name="sourceId"/>, or null (cached; allocates nothing).</summary>
    public TileSetAtlasSource? GetSource(int sourceId)
    {
        if (_idsDirty)
        {
            _byId.Clear();
            foreach (var source in _sources)
                if (source is not null)
                    _byId.TryAdd(source.Id, source); // the first of duplicate ids wins
            _idsDirty = false;
        }

        return _byId.GetValueOrDefault(sourceId);
    }

    public bool HasSource(int sourceId) => GetSource(sourceId) is not null;

    /// <summary>The smallest source id not in use (Godot's <c>get_next_source_id</c>).</summary>
    public int GetNextSourceId()
    {
        var id = 0;
        while (HasSource(id))
            id++;
        return id;
    }

    /// <summary>Adds <paramref name="source"/> under <paramref name="id"/> (-1: the next free id) and returns the id.</summary>
    public int AddSource(TileSetAtlasSource source, int id = -1)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (id < 0)
            id = GetNextSourceId();
        else if (HasSource(id))
            throw new ArgumentException($"The tile set already has a source with id {id}.", nameof(id));
        source.Id = id;
        source.Changed += OnChildChanged;
        _sources.Add(source);
        Touch();
        return id;
    }

    /// <summary>Removes the source with <paramref name="sourceId"/>; false when there is none.</summary>
    public bool RemoveSource(int sourceId)
    {
        if (GetSource(sourceId) is not { } source)
            return false;
        source.Changed -= OnChildChanged;
        _sources.Remove(source);
        Touch();
        return true;
    }

    /// <summary>The index of the custom data layer named <paramref name="name"/>, or -1 (ordinal).</summary>
    public int GetCustomDataLayerByName(string name)
    {
        for (var i = 0; i < _customDataLayers.Count; i++)
            if (string.Equals(_customDataLayers[i]?.Name, name, StringComparison.Ordinal))
                return i;
        return -1;
    }

    /// <summary>Adds a physics layer and returns its index.</summary>
    public int AddPhysicsLayer(uint collisionLayer = CollisionLayers.Default, uint collisionMask = CollisionLayers.Default)
    {
        var layer = new TileSetPhysicsLayer { CollisionLayer = collisionLayer, CollisionMask = collisionMask };
        layer.Changed += OnChildChanged;
        _physicsLayers.Add(layer);
        Touch();
        return _physicsLayers.Count - 1;
    }

    /// <summary>Adds a custom data layer and returns its index.</summary>
    public int AddCustomDataLayer(string name, CustomDataType type)
    {
        var layer = new TileSetCustomDataLayer { Name = name, Type = type };
        layer.Changed += OnChildChanged;
        _customDataLayers.Add(layer);
        Touch();
        return _customDataLayers.Count - 1;
    }

    /// <summary>The data of a cell's tile, or null when the source or tile does not exist.</summary>
    public TileData? GetTileData(in TileCell cell)
    {
        if (cell.IsEmpty || GetSource(cell.SourceId) is not { } source || !source.HasTile(cell.AtlasCoords))
            return null;
        return source.GetTileData(cell.AtlasCoords, cell.Alternative);
    }

    /// <summary>Raises <see cref="Resource.Changed"/> after a list was edited in place.</summary>
    public void NotifyChanged() => Touch();

    private void SetList<T>(ref List<T> field, List<T>? value) where T : Resource
    {
        foreach (var item in field)
            if (item is not null)
                item.Changed -= OnChildChanged;
        field = value ?? [];
        foreach (var item in field)
            if (item is not null)
                item.Changed += OnChildChanged;
        Touch();
    }

    private void OnChildChanged() => Touch();

    private void Touch()
    {
        _idsDirty = true;
        _version++;
        EmitChanged();
    }
}

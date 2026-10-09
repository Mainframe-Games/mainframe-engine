using System.Globalization;
using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// A terrain's foliage (G8a scatter, ADR 0157): an internal, unsaved child of a <see cref="Terrain3D"/> that draws the
/// <see cref="TerrainData.FoliageTypes"/> as one <see cref="MultiMeshInstance3D"/> per (tile, type) — a tile is a
/// terrain chunk, or a part of one (<see cref="FoliageType.Subdivisions"/>). The instance nodes are unsaved children too.
/// </summary>
/// <remarks>
/// <para>Placement (<c>FoliagePlacement</c>) is deterministic; an edit (<see cref="Terrain3D.Changed"/> with heights,
/// splat weights or water) rebuilds only the tiles whose instances it can touch, and a change to the type list or a
/// type rebuilds that type.</para>
/// <para>Every frame (<c>OnProcess</c>, also in the editor) each tile draws the first factor × count of its
/// instances, which are sorted by a uniform per-instance hash key, with the distance factor 1 up to <see cref="FoliageType.CullDistance"/> −
/// <see cref="FoliageType.ThinBand"/>, falling linearly to 0 at the cull distance, where the tile hides; the terrain's
/// <see cref="Terrain3D.FoliageDensityScale"/> and <see cref="Terrain3D.FoliageDistanceScale"/> scale both at run time. Only
/// <see cref="MultiMesh.VisibleInstanceCount"/> and <see cref="Node3D.Visible"/> change, so it allocates nothing and
/// uploads nothing.</para>
/// </remarks>
[Tool]
[EditorIcon("stack-2")]
public sealed class TerrainFoliage3D : Node3D
{
    private readonly Action<TerrainChange> _onChanged;
    private Terrain3D? _subscribed;
    private TypeState[] _types = [];
    private FoliageType[]? _builtList;

    public TerrainFoliage3D()
    {
        _onChanged = OnTerrainChanged;
    }

    /// <summary>The terrain this foliage belongs to.</summary>
    public Terrain3D? Terrain { get; internal set; }

    /// <summary>Foliage types built (the length of the terrain data's list when it was last built).</summary>
    public int TypeCount => _types.Length;

    /// <summary>Tiles per map side of type <paramref name="type"/>.</summary>
    public int GetTilesPerSide(int type) => _types[type].TilesPerSide;

    /// <summary>The node drawing tile (<paramref name="tx"/>, <paramref name="tz"/>) of type <paramref name="type"/>, or null when it is empty.</summary>
    public MultiMeshInstance3D? GetTileNode(int type, int tx, int tz) => _types[type].Tiles[tz * _types[type].TilesPerSide + tx].Node;

    /// <summary>Instances placed in tile (<paramref name="tx"/>, <paramref name="tz"/>) of type <paramref name="type"/> (drawn or not).</summary>
    public int GetTileInstanceCount(int type, int tx, int tz) => _types[type].Tiles[tz * _types[type].TilesPerSide + tx].Data.Count;

    /// <summary>How many times tile (<paramref name="tx"/>, <paramref name="tz"/>) of <paramref name="type"/> was built (tests).</summary>
    internal int GetTileBuilds(int type, int tx, int tz) => _types[type].Tiles[tz * _types[type].TilesPerSide + tx].Builds;

    /// <summary>Instances of every type placed over the whole map.</summary>
    public int TotalInstances
    {
        get
        {
            var total = 0;
            foreach (var state in _types)
                foreach (var tile in state.Tiles)
                    total += tile.Data.Count;
            return total;
        }
    }

    /// <summary>Instances of every type drawn after the last distance update.</summary>
    public int DrawnInstances
    {
        get
        {
            var total = 0;
            foreach (var state in _types)
                foreach (var tile in state.Tiles)
                    total += tile.Drawn;
            return total;
        }
    }

    // ── Lifecycle ──────────────────────────────────────────────────────────────

    protected override void OnEnterTree()
    {
        base.OnEnterTree();
        if (Terrain is { } terrain && _subscribed is null)
        {
            terrain.Changed += _onChanged;
            _subscribed = terrain;
        }
    }

    protected override void OnExitTree()
    {
        if (_subscribed is { } terrain)
            terrain.Changed -= _onChanged;
        _subscribed = null;
        base.OnExitTree();
    }

    protected override void OnReady()
    {
        base.OnReady();
        SyncTypes();
    }

    protected override void OnProcess(in GameTime gameTime)
    {
        SyncTypes();
        if (_types.Length == 0 || GetViewport() is not { } viewport)
            return;
        Vector3 camera;
        if (viewport.CameraOverride is { } overrideCamera)
            camera = overrideCamera.Position;
        else if (viewport.ActiveCamera3D is { } active)
            camera = active.GlobalPosition;
        else
            return;
        UpdateDistances(camera - GlobalPosition);
    }

    /// <summary>
    /// Sets every tile's drawn prefix for a camera at <paramref name="localCamera"/> (terrain-local): the first
    /// factor × count instances (sorted by a uniform hash key, so the instances keyed below the factor); a tile with none
    /// hides. Allocation-free and O(1) per tile.
    /// </summary>
    public void UpdateDistances(Vector3 localCamera)
    {
        // Runtime quality knobs (Terrain3D.FoliageDensityScale / FoliageDistanceScale): fewer of the same sorted
        // instances and a shorter cull distance, with no rebuild.
        var density = Terrain?.FoliageDensityScale ?? 1f;
        var reach = Terrain?.FoliageDistanceScale ?? 1f;
        foreach (var state in _types)
        {
            if (state.Type is not { } type)
                continue;
            var cull = type.CullDistance * reach;
            var band = MathF.Max(type.ThinBand * reach, 1e-3f);
            foreach (var tile in state.Tiles)
            {
                if (tile.Node is not { } node)
                    continue;
                var distance = Vector3.Distance(localCamera, tile.Center);
                var factor = Math.Clamp((cull - distance) / band, 0f, 1f) * density;
                var drawn = (int)(factor * tile.Data.Count + 0.5f); // the keys are uniform: ≈ the instances keyed below the factor
                tile.Drawn = drawn;
                if (drawn == 0)
                {
                    node.Visible = false;
                    continue;
                }

                tile.Multimesh!.VisibleInstanceCount = drawn;
                node.Visible = true;
            }
        }
    }

    // ── Building ───────────────────────────────────────────────────────────────

    /// <summary>Rebuilds the types whose list entry, settings or mesh changed (or all when the list changed).</summary>
    private void SyncTypes()
    {
        if (Terrain?.Data is not { } data)
            return;
        var list = data.FoliageTypes;
        if (!ReferenceEquals(list, _builtList) || list.Length != _types.Length)
        {
            FreeTiles();
            _builtList = list;
            _types = new TypeState[list.Length];
            for (var i = 0; i < list.Length; i++)
                _types[i] = new TypeState(list[i]);
        }

        for (var i = 0; i < _types.Length; i++)
        {
            var state = _types[i];
            var type = list[i];
            if (!ReferenceEquals(state.Type, type) || state.Stamp != Stamp(type))
                BuildType(data, i, type);
        }
    }

    // The type's settings and its mesh's content (a mesh edited in place moves the bounds).
    private static long Stamp(FoliageType? type) => type is null ? 0 : ((long)type.Version << 32) | (uint)(type.Mesh?.Version ?? 0);

    private void BuildType(TerrainData data, int index, FoliageType? type)
    {
        var state = _types[index];
        FreeTiles(state);
        state.Type = type;
        state.Stamp = Stamp(type);
        if (type is null)
        {
            state.Tiles = [];
            state.TilesPerSide = 0;
            return;
        }

        var n = FoliagePlacement.TilesPerSide(data, type);
        state.TilesPerSide = n;
        state.TileSize = FoliagePlacement.TileSize(data, type);
        state.Tiles = new Tile[n * n];
        for (var tz = 0; tz < n; tz++)
            for (var tx = 0; tx < n; tx++)
            {
                state.Tiles[tz * n + tx] = new Tile();
                BuildTile(data, index, tx, tz);
            }
    }

    private void BuildTile(TerrainData data, int typeIndex, int tx, int tz)
    {
        var state = _types[typeIndex];
        var type = state.Type!;
        var tile = state.Tiles[tz * state.TilesPerSide + tx];
        var built = FoliagePlacement.BuildTile(data, type, typeIndex, tx, tz);
        tile.Data = built;
        tile.Builds++;
        var size = state.TileSize;
        var (min, max) = data.ChunkHeightRange(
            Math.Min(tx / Math.Max(1, type.Subdivisions), data.ChunksPerSide - 1),
            Math.Min(tz / Math.Max(1, type.Subdivisions), data.ChunksPerSide - 1));
        tile.Center = new Vector3((tx + 0.5f) * size, 0.5f * (min + max), (tz + 0.5f) * size);
        if (built.Count == 0)
        {
            if (tile.Node is { } empty)
            {
                empty.Free();
                tile.Node = null;
                tile.Multimesh = null;
            }

            tile.Drawn = 0;
            return;
        }

        if (tile.Node is null)
        {
            tile.Multimesh = new MultiMesh();
            tile.Node = new MultiMeshInstance3D
            {
                Name = string.Create(CultureInfo.InvariantCulture, $"Foliage{typeIndex}_{tx}_{tz}"),
                Multimesh = tile.Multimesh,
                Position = new Vector3(tx * size, 0f, tz * size),
            };
            AddChild(tile.Node);
        }

        var multimesh = tile.Multimesh!;
        multimesh.Mesh = type.Mesh;
        multimesh.Transforms = built.Transforms;
        multimesh.CustomAabb = built.Bounds;
        multimesh.VisibleInstanceCount = built.Count;
        tile.Node.MaterialOverride = type.Material;
        tile.Node.CastShadows = type.CastShadows;
        tile.Node.VisibilityRangeEnd = type.CullDistance;
        tile.Drawn = built.Count;
    }

    private void OnTerrainChanged(TerrainChange change)
    {
        if ((change.Layers & (TerrainLayers.Height | TerrainLayers.Surface | TerrainLayers.Water)) == 0 ||
            Terrain?.Data is not { } data || change.Cells.Size.X <= 0 || change.Cells.Size.Y <= 0)
            return;

        // The touched area in metres, grown by a vertex (normals read neighbours).
        var cell = data.CellSize;
        var grow = data.VertexSpacing;
        var x0 = change.Cells.Position.X * cell - grow;
        var z0 = change.Cells.Position.Y * cell - grow;
        var x1 = change.Cells.End.X * cell + grow;
        var z1 = change.Cells.End.Y * cell + grow;
        for (var i = 0; i < _types.Length; i++)
        {
            var state = _types[i];
            if (state.Tiles.Length == 0)
                continue;

            // A tile holds points up to half a tile (fuzz) beyond its edges.
            var size = state.TileSize;
            var n = state.TilesPerSide;
            var tx0 = Math.Clamp((int)MathF.Floor((x0 - size * 0.5f) / size), 0, n - 1);
            var tz0 = Math.Clamp((int)MathF.Floor((z0 - size * 0.5f) / size), 0, n - 1);
            var tx1 = Math.Clamp((int)MathF.Floor((x1 + size * 0.5f) / size), 0, n - 1);
            var tz1 = Math.Clamp((int)MathF.Floor((z1 + size * 0.5f) / size), 0, n - 1);
            for (var tz = tz0; tz <= tz1; tz++)
                for (var tx = tx0; tx <= tx1; tx++)
                    BuildTile(data, i, tx, tz);
        }
    }

    private void FreeTiles()
    {
        foreach (var state in _types)
            FreeTiles(state);
        _types = [];
        _builtList = null;
    }

    private static void FreeTiles(TypeState state)
    {
        foreach (var tile in state.Tiles)
            tile.Node?.Free();
        state.Tiles = [];
    }

    private sealed class TypeState(FoliageType? type)
    {
        public FoliageType? Type = type;
        public long Stamp = long.MinValue;
        public int TilesPerSide;
        public float TileSize;
        public Tile[] Tiles = [];
    }

    private sealed class Tile
    {
        public FoliageTileData Data = FoliageTileData.Empty;
        public MultiMeshInstance3D? Node;
        public MultiMesh? Multimesh;
        public Vector3 Center;
        public int Drawn;
        public int Builds;
    }
}

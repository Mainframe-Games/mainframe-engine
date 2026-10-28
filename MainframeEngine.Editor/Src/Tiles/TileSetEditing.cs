using System.Numerics;

namespace MainframeEngine.Editor;

/// <summary>
/// The whole editable state of a <see cref="TileSet"/>: its settings, physics and custom data layers, sources (by
/// reference) with their grids, tiles and tile data, and each tile's polygons. Restoring a snapshot puts every value
/// back, so any TileSet edit from the TileSet tab is one undo entry (ADR 0226), like SpriteFrames' (ADR 0224).
/// </summary>
public sealed class TileSetSnapshot
{
    private readonly (TileShape Shape, Vector2I Size, bool UvClipping) _set;
    private readonly (TileSetPhysicsLayer Layer, uint CollisionLayer, uint Mask, PhysicsMaterial? Material)[] _physics;
    private readonly (TileSetCustomDataLayer Layer, string Name, CustomDataType Type)[] _custom;
    private readonly SourceState[] _sources;

    private sealed record SourceState(TileSetAtlasSource Source, int Id, Texture2D? Texture, Vector2I Margins, Vector2I Separation,
        Vector2I RegionSize, bool Padding, Vector2I[] Tiles, DataState[] Overrides);

    private sealed record DataState(TileData Data, Vector2I Coords, int Alternative, Vector2I Origin, Vector4 Modulate, int Z, int YSort,
        float Probability, string[] CustomData, (TileCollisionPolygon Polygon, int Layer, Vector2[] Points)[] Collision);

    public TileSetSnapshot(TileSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        _set = (set.TileShape, set.TileSize, set.UvClipping);
        _physics = [.. set.PhysicsLayers.Where(l => l is not null).Select(l => (l, l.CollisionLayer, l.CollisionMask, l.PhysicsMaterial))];
        _custom = [.. set.CustomDataLayers.Where(l => l is not null).Select(l => (l, l.Name, l.Type))];
        _sources = [.. set.Sources.Where(s => s is not null).Select(s => new SourceState(s, s.Id, s.Texture, s.Margins, s.Separation,
            s.TextureRegionSize, s.UseTexturePadding, s.Tiles, [.. s.TileOverrides.Where(d => d is not null).Select(Capture)]))];
    }

    private static DataState Capture(TileData d) => new(d, d.AtlasCoords, d.AlternativeId, d.TextureOrigin, d.Modulate, d.ZIndex, d.YSortOrigin,
        d.Probability, d.CustomData, [.. d.Collision.Where(p => p is not null).Select(p => (p, p.PhysicsLayer, p.Points))]);

    public void Restore(TileSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        foreach (var s in _sources)
        {
            foreach (var d in s.Overrides)
            {
                foreach (var (polygon, layer, points) in d.Collision)
                {
                    polygon.PhysicsLayer = layer;
                    polygon.Points = points;
                }

                var data = d.Data;
                data.AtlasCoords = d.Coords;
                data.AlternativeId = d.Alternative;
                data.TextureOrigin = d.Origin;
                data.Modulate = d.Modulate;
                data.ZIndex = d.Z;
                data.YSortOrigin = d.YSort;
                data.Probability = d.Probability;
                data.CustomData = d.CustomData;
                data.Collision = [.. d.Collision.Select(c => c.Polygon)];
            }

            var source = s.Source;
            source.Id = s.Id;
            source.Texture = s.Texture;
            source.Margins = s.Margins;
            source.Separation = s.Separation;
            source.TextureRegionSize = s.RegionSize;
            source.UseTexturePadding = s.Padding;
            source.Tiles = s.Tiles;
            source.TileOverrides = [.. s.Overrides.Select(o => o.Data)];
        }

        foreach (var (layer, collisionLayer, mask, material) in _physics)
        {
            layer.CollisionLayer = collisionLayer;
            layer.CollisionMask = mask;
            layer.PhysicsMaterial = material;
        }

        foreach (var (layer, name, type) in _custom)
        {
            layer.Name = name;
            layer.Type = type;
        }

        set.TileShape = _set.Shape;
        set.TileSize = _set.Size;
        set.UvClipping = _set.UvClipping;
        set.PhysicsLayers = [.. _physics.Select(p => p.Layer)];
        set.CustomDataLayers = [.. _custom.Select(c => c.Layer)];
        set.Sources = [.. _sources.Select(s => s.Source)]; // last: layers redraw once everything is back
    }

    /// <summary>True when both describe the same state (an edit that changed nothing is not recorded).</summary>
    public bool SameAs(TileSetSnapshot other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (_set != other._set || !_physics.AsSpan().SequenceEqual(other._physics) || !_custom.AsSpan().SequenceEqual(other._custom) ||
            _sources.Length != other._sources.Length)
            return false;
        for (var i = 0; i < _sources.Length; i++)
        {
            var (a, b) = (_sources[i], other._sources[i]);
            if (!ReferenceEquals(a.Source, b.Source) || a.Id != b.Id || !ReferenceEquals(a.Texture, b.Texture) || a.Margins != b.Margins ||
                a.Separation != b.Separation || a.RegionSize != b.RegionSize || a.Padding != b.Padding || !a.Tiles.AsSpan().SequenceEqual(b.Tiles) ||
                a.Overrides.Length != b.Overrides.Length)
                return false;
            for (var j = 0; j < a.Overrides.Length; j++)
            {
                var (x, y) = (a.Overrides[j], b.Overrides[j]);
                if (!ReferenceEquals(x.Data, y.Data) || x.Coords != y.Coords || x.Alternative != y.Alternative || x.Origin != y.Origin ||
                    x.Modulate != y.Modulate || x.Z != y.Z || x.YSort != y.YSort || x.Probability != y.Probability ||
                    !x.CustomData.AsSpan().SequenceEqual(y.CustomData) || x.Collision.Length != y.Collision.Length)
                    return false;
                for (var k = 0; k < x.Collision.Length; k++)
                    if (!ReferenceEquals(x.Collision[k].Polygon, y.Collision[k].Polygon) || x.Collision[k].Layer != y.Collision[k].Layer ||
                        !x.Collision[k].Points.AsSpan().SequenceEqual(y.Collision[k].Points))
                        return false;
            }
        }

        return true;
    }
}

/// <summary>One TileSet edit as one history entry.</summary>
public sealed class TileSetEditAction(string name, TileSet set, TileSetSnapshot before, TileSetSnapshot after) : IEditorAction
{
    public string Name { get; } = name;

    public TileSet TileSet { get; } = set;

    public void Do() => after.Restore(TileSet);

    public void Undo() => before.Restore(TileSet);
}

/// <summary>TileSet edits from the TileSet tab: each runs as one undoable entry in the scene's or resource file's history.</summary>
public static class TileSetEditing
{
    /// <summary>
    /// Runs <paramref name="change"/> on <paramref name="set"/> and records it as one entry named <paramref name="name"/>
    /// (nothing when it changed nothing). A <c>.mres</c> edited through a scene is written when the scene is saved.
    /// </summary>
    public static bool Edit(IInspectorContext context, TileSet set, string name, Action<TileSet> change)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(change);
        var before = new TileSetSnapshot(set);
        change(set);
        var after = new TileSetSnapshot(set);
        if (before.SameAs(after))
            return false;
        set.NotifyChanged();
        if (context is EditedScene scene && set.IsExternal)
            scene.MarkResourceFileEdited(set);
        context.History.Commit(new TileSetEditAction(name, set, before, after), alreadyApplied: true);
        return true;
    }

    /// <summary>Toggles a tile: created when missing, removed (with its data) when present.</summary>
    public static void ToggleTile(TileSetAtlasSource source, Vector2I coords)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.HasTile(coords))
            source.RemoveTile(coords);
        else
            source.CreateTile(coords);
    }

    /// <summary>Sets the tile's polygons on <paramref name="physicsLayer"/> to one full-tile square.</summary>
    public static void SetFullTile(TileSet set, TileSetAtlasSource source, Vector2I coords, int physicsLayer)
    {
        ArgumentNullException.ThrowIfNull(set);
        var data = source.GetOrCreateTileData(coords);
        var list = data.Collision.Where(p => p.PhysicsLayer != physicsLayer).ToList();
        list.Add(TileCollisionPolygon.FullTile(set.TileSize, physicsLayer));
        data.Collision = list;
    }

    /// <summary>Removes the tile's polygons on <paramref name="physicsLayer"/>.</summary>
    public static void ClearCollision(TileSetAtlasSource source, Vector2I coords, int physicsLayer)
    {
        ArgumentNullException.ThrowIfNull(source);
        var data = source.GetOrCreateTileData(coords);
        data.Collision = [.. data.Collision.Where(p => p.PhysicsLayer != physicsLayer)];
    }

    /// <summary>A point snapped to <paramref name="step"/> pixels and clamped to the tile (tile-local, origin at the centre).</summary>
    public static Vector2 Snap(Vector2 point, Vector2I tileSize, float step)
    {
        var half = new Vector2(tileSize.X, tileSize.Y) * 0.5f;
        if (step > 0)
            point = new Vector2(MathF.Round(point.X / step) * step, MathF.Round(point.Y / step) * step);
        return Vector2.Clamp(point, -half, half);
    }
}

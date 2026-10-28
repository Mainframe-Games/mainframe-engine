using System.Numerics;
using Box2D.NET;
using static Box2D.NET.B2Bodies;
using static Box2D.NET.B2Geometries;
using static Box2D.NET.B2Hulls;
using static Box2D.NET.B2MathFunction;
using static Box2D.NET.B2Shapes;
using static Box2D.NET.B2Types;
using static Box2D.NET.B2Worlds;

namespace MainframeEngine;

/// <summary>
/// A <see cref="TileMapLayer"/>'s static bodies in its <see cref="PhysicsSpace2D"/>: one Box2D static body per chunk
/// per tile set physics layer that has shapes (Godot's per-quadrant bodies). Full-tile squares merge into one
/// rectangle per run of cells (rows, then equal runs below), other polygons become convex pieces of at most eight
/// points. Bodies follow the layer's global transform (teleported). Rebuilds happen in the space's flush, never
/// inside a step; collisions report the layer (<see cref="BodyRecord2D.Owner"/>). All Box2D calls stay here and in
/// <see cref="PhysicsSpace2D"/>.
/// </summary>
internal sealed class TileBodies2D : IDisposable
{
    private readonly PhysicsSpace2D _space;
    private readonly TileMapLayer _layer;
    private readonly List<TileChunk> _dirty = [];
    private readonly List<Vector2[]> _pieces = [];
    private readonly List<Vector2> _scratchPoints = [];
    private bool[] _full = [];
    private bool _transformDirty;
    private bool _disposed;
    private Vector2 _builtScale = Vector2.One;
    private int _bodyCount;

    internal TileBodies2D(PhysicsSpace2D space, TileMapLayer layer)
    {
        _space = space;
        _layer = layer;
        _builtScale = layer.GlobalTransform.Scale;
    }

    public TileMapLayer Layer => _layer;

    /// <summary>Static bodies alive (chunks × physics layers with shapes).</summary>
    public int BodyCount => _bodyCount;

    internal bool InDirtyList { get; set; }

    internal bool HasPendingWork => _dirty.Count > 0 || _transformDirty;

    public void MarkChunkDirty(TileChunk chunk)
    {
        if (_disposed)
            return;
        if (!chunk.PhysicsDirty)
        {
            chunk.PhysicsDirty = true;
            _dirty.Add(chunk);
        }

        _space.MarkTileLayerDirty(this);
    }

    public void MarkTransformDirty()
    {
        if (_disposed)
            return;
        _transformDirty = true;
        _space.MarkTileLayerDirty(this);
    }

    /// <summary>Destroys a removed chunk's bodies now.</summary>
    public void RemoveChunk(TileChunk chunk)
    {
        if (chunk.PhysicsDirty)
        {
            chunk.PhysicsDirty = false;
            _dirty.Remove(chunk);
        }

        DestroyBodies(chunk);
    }

    /// <summary>Applies the pending rebuilds now (<see cref="TileMapLayer.UpdateInternals"/>).</summary>
    public void FlushNow() => Flush();

    internal void Flush()
    {
        if (_disposed)
            return;
        if (_transformDirty)
        {
            _transformDirty = false;
            var global = _layer.GlobalTransform;
            if (Vector2.DistanceSquared(global.Scale, _builtScale) > 1e-10f)
            {
                _builtScale = global.Scale;
                foreach (var chunk in _layer.Chunks)
                    if (!chunk.PhysicsDirty)
                    {
                        chunk.PhysicsDirty = true;
                        _dirty.Add(chunk);
                    }
            }
            else
            {
                foreach (var chunk in _layer.Chunks)
                    if (chunk.Bodies is { } bodies)
                        foreach (var record in bodies)
                            if (record is not null)
                                _space.TeleportTileBody(record, global.Origin, global.Rotation);
            }
        }

        for (var i = 0; i < _dirty.Count; i++)
        {
            var chunk = _dirty[i];
            chunk.PhysicsDirty = false;
            Rebuild(chunk);
        }

        _dirty.Clear();
    }

    private void Rebuild(TileChunk chunk)
    {
        var set = _layer.TileSet;
        var layers = set is null || !_layer.Enabled || chunk.Used == 0 ? 0 : set.PhysicsLayers.Count;
        var bodies = chunk.Bodies;
        // Physics layers that no longer exist (or a disabled layer): no bodies.
        if (bodies is not null)
            for (var l = bodies.Count - 1; l >= layers; l--)
            {
                if (bodies[l] is { } stale)
                    DestroyBody(stale);
                bodies.RemoveAt(l);
            }

        for (var l = 0; l < layers; l++)
        {
            CollectShapes(chunk, set!, l, out var rectangles);
            var any = rectangles > 0 || _pieces.Count > 0;
            bodies = chunk.Bodies;
            var record = bodies is not null && l < bodies.Count ? bodies[l] : null;
            if (!any)
            {
                if (record is not null)
                {
                    DestroyBody(record);
                    bodies![l] = null;
                }

                continue;
            }

            if (record is null)
            {
                var global = _layer.GlobalTransform;
                var physicsLayer = set!.PhysicsLayers[l];
                record = _space.CreateTileBody(_layer, physicsLayer?.CollisionLayer ?? CollisionLayers.Default,
                    physicsLayer?.CollisionMask ?? CollisionLayers.Default, global.Origin, global.Rotation);
                _bodyCount++;
                chunk.Bodies ??= [];
                while (chunk.Bodies.Count <= l)
                    chunk.Bodies.Add(null);
                chunk.Bodies[l] = record;
            }
            else if (set!.PhysicsLayers[l] is { } physicsLayer)
            {
                record.Layer = physicsLayer.CollisionLayer;
                record.Mask = physicsLayer.CollisionMask;
            }

            _space.SetTileBodyShapes(record, chunk, rectangles, _full, _pieces, set!.TileSize, _builtScale, set.PhysicsLayers[l]?.PhysicsMaterial);
        }

        _pieces.Clear();
    }

    // The chunk's shapes on physics layer `l`: full-tile cells marked in _full (counted), every other polygon as convex
    // pieces in _pieces (layer-local pixels).
    private void CollectShapes(TileChunk chunk, TileSet set, int l, out int fullCells)
    {
        fullCells = 0;
        _pieces.Clear();
        var size = chunk.Size;
        if (_full.Length < size * size)
            _full = new bool[size * size];
        Array.Clear(_full, 0, size * size);
        var tile = set.TileSize;
        var half = new Vector2(tile.X, tile.Y) * 0.5f;
        var origin = chunk.Origin;
        for (var i = 0; i < chunk.Cells.Length; i++)
        {
            var cell = chunk.Cells[i];
            if (cell.IsEmpty || set.GetTileData(cell) is not { Collision.Count: > 0 } data)
                continue;
            var centre = new Vector2((origin.X + i % size + 0.5f) * tile.X, (origin.Y + i / size + 0.5f) * tile.Y);
            foreach (var polygon in data.Collision)
            {
                if (polygon is null || polygon.PhysicsLayer != l || polygon.Points.Length < 3)
                    continue;
                var points = polygon.Points;
                if (TilePolygons.IsFullTile(points, half) && (!cell.Transpose || tile.X == tile.Y))
                {
                    if (!_full[i])
                        fullCells++;
                    _full[i] = true;
                    continue;
                }

                _scratchPoints.Clear();
                foreach (var p in points)
                    _scratchPoints.Add(centre + TilePolygons.Transform(p, cell.FlipH, cell.FlipV, cell.Transpose));
                if (!TilePolygons.Decompose(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_scratchPoints), _pieces))
                    Log.Warning($"[TileMapLayer] '{_layer.Name}': a collision polygon of tile {cell.AtlasCoords} cannot be triangulated; ignored.");
            }
        }
    }

    private void DestroyBodies(TileChunk chunk)
    {
        if (chunk.Bodies is not { } bodies)
            return;
        foreach (var record in bodies)
            if (record is not null)
                DestroyBody(record);
        chunk.Bodies = null;
    }

    private void DestroyBody(BodyRecord2D record)
    {
        _space.DestroyTileBody(record);
        _bodyCount--;
    }

    /// <summary>Outlines of every shape (world pixels).</summary>
    internal void DrawDebug(DebugLines lines, Vector4 color)
    {
        foreach (var chunk in _layer.Chunks)
            if (chunk.Bodies is { } bodies)
                foreach (var record in bodies)
                    if (record is not null)
                        _space.DrawTileBodyDebug(lines, record, color);
    }

    internal void OnSpaceDisposed()
    {
        _disposed = true;
        foreach (var chunk in _layer.Chunks)
        {
            chunk.Bodies = null;
            chunk.PhysicsDirty = false;
        }

        _dirty.Clear();
        _bodyCount = 0;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        foreach (var chunk in _layer.Chunks)
        {
            DestroyBodies(chunk);
            chunk.PhysicsDirty = false;
        }

        _dirty.Clear();
        _disposed = true;
        _space.RemoveTileLayer(this);
    }
}

public sealed partial class PhysicsSpace2D
{
    private static readonly Vector4 TileColor = new(0.35f, 0.85f, 0.55f, 1f);

    private readonly List<TileBodies2D> _tileLayers = [];
    private readonly List<TileBodies2D> _dirtyTileLayers = [];

    /// <summary>True when a tile layer draws its shapes whatever the debug setting (<see cref="DebugVisibilityMode.ForceShow"/>).</summary>
    internal bool HasForcedTileDebug
    {
        get
        {
            foreach (var tiles in _tileLayers)
                if (tiles.Layer.CollisionVisibilityMode == DebugVisibilityMode.ForceShow)
                    return true;
            return false;
        }
    }

    /// <summary>Static bodies of every tile layer in this space.</summary>
    internal int TileBodyCount
    {
        get
        {
            var count = 0;
            foreach (var tiles in _tileLayers)
                count += tiles.BodyCount;
            return count;
        }
    }

    internal TileBodies2D AddTileLayer(TileMapLayer layer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var tiles = new TileBodies2D(this, layer);
        _tileLayers.Add(tiles);
        return tiles;
    }

    internal void RemoveTileLayer(TileBodies2D tiles)
    {
        _tileLayers.Remove(tiles);
        if (tiles.InDirtyList)
        {
            tiles.InDirtyList = false;
            _dirtyTileLayers.Remove(tiles);
        }
    }

    internal void MarkTileLayerDirty(TileBodies2D tiles)
    {
        if (tiles.InDirtyList)
            return;
        tiles.InDirtyList = true;
        _dirtyTileLayers.Add(tiles);
    }

    private void FlushTileLayers()
    {
        if (_dirtyTileLayers.Count == 0)
            return;
        for (var i = 0; i < _dirtyTileLayers.Count; i++)
        {
            _dirtyTileLayers[i].InDirtyList = false;
            _dirtyTileLayers[i].Flush();
        }

        _dirtyTileLayers.Clear();
    }

    internal BodyRecord2D CreateTileBody(TileMapLayer layer, uint collisionLayer, uint collisionMask, Vector2 position, float rotation)
    {
        var record = new BodyRecord2D(null, layer, this, ++_sequence)
        {
            Kind = PhysicsBodyKind.Static,
            Layer = collisionLayer,
            Mask = collisionMask,
        };
        record.PreviousPosition = record.CurrentPosition = position;
        record.PreviousRotation = record.CurrentRotation = rotation;
        var def = b2DefaultBodyDef();
        def.type = B2BodyType.b2_staticBody;
        def.position = ToB2(position / PixelsPerMeter);
        def.rotation = b2MakeRot(rotation);
        def.userData = B2UserData.Ref(record);
        record.Body = b2CreateBody(_world, def);
        record.HasBody = true;
        return record;
    }

    internal void TeleportTileBody(BodyRecord2D record, Vector2 position, float rotation)
    {
        if (!record.HasBody)
            return;
        WakeTouching(record); // bodies resting on the old pose
        b2Body_SetTransform(record.Body, ToB2(position / PixelsPerMeter), b2MakeRot(rotation));
        record.PreviousPosition = record.CurrentPosition = position;
        record.PreviousRotation = record.CurrentRotation = rotation;
        WakeTouching(record);
    }

    // Replaces the body's shapes: merged rectangles of the full cells, then the convex pieces (layer-local pixels, scaled).
    internal void SetTileBodyShapes(BodyRecord2D record, TileChunk chunk, int fullCells, bool[] full, List<Vector2[]> pieces, Vector2I tileSize,
        Vector2 scale, PhysicsMaterial? material)
    {
        if (!record.HasBody)
            return;
        WakeTouching(record);
        foreach (var shape in record.Shapes)
        {
            _shapes.Remove(Key(shape.Id));
            b2DestroyShape(shape.Id, false);
        }

        record.Shapes.Clear();
        var def = b2DefaultShapeDef();
        def.filter = PackFilter(record.Layer, record.Mask);
        def.enableCustomFiltering = true;
        def.enableSensorEvents = true;
        def.enableContactEvents = false;
        def.updateBodyMass = false;
        def.material.friction = material?.Friction ?? PhysicsMaterial.DefaultFriction;
        def.material.restitution = material?.Bounce ?? 0f;
        def.userData = B2UserData.Ref(record);
        var ppm = PixelsPerMeter;
        Span<B2Vec2> points = stackalloc B2Vec2[TilePolygons.MaxVertices];

        if (fullCells > 0)
        {
            // Greedy rectangles: a row run of full cells, extended down while the rows below have the same run free.
            var size = chunk.Size;
            var origin = chunk.Origin;
            var used = System.Buffers.ArrayPool<bool>.Shared.Rent(size * size);
            Array.Clear(used, 0, size * size);
            try
            {
                for (var y = 0; y < size; y++)
                    for (var x = 0; x < size; x++)
                    {
                        if (!full[y * size + x] || used[y * size + x])
                            continue;
                        var x1 = x;
                        while (x1 + 1 < size && full[y * size + x1 + 1] && !used[y * size + x1 + 1])
                            x1++;
                        var y1 = y;
                        while (y1 + 1 < size && RunFree(full, used, size, y1 + 1, x, x1))
                            y1++;
                        for (var yy = y; yy <= y1; yy++)
                            for (var xx = x; xx <= x1; xx++)
                                used[yy * size + xx] = true;
                        var min = new Vector2((origin.X + x) * tileSize.X, (origin.Y + y) * tileSize.Y) * scale;
                        var max = new Vector2((origin.X + x1 + 1) * tileSize.X, (origin.Y + y1 + 1) * tileSize.Y) * scale;
                        points[0] = ToB2(min / ppm);
                        points[1] = ToB2(new Vector2(max.X, min.Y) / ppm);
                        points[2] = ToB2(max / ppm);
                        points[3] = ToB2(new Vector2(min.X, max.Y) / ppm);
                        AddTilePolygon(record, def, points[..4]);
                    }
            }
            finally
            {
                System.Buffers.ArrayPool<bool>.Shared.Return(used);
            }
        }

        foreach (var piece in pieces)
        {
            var n = Math.Min(piece.Length, TilePolygons.MaxVertices);
            for (var i = 0; i < n; i++)
                points[i] = ToB2(piece[i] * scale / ppm);
            AddTilePolygon(record, def, points[..n]);
        }

        WakeInBounds(record);
    }

    private static bool RunFree(bool[] full, bool[] used, int size, int y, int x0, int x1)
    {
        for (var x = x0; x <= x1; x++)
            if (!full[y * size + x] || used[y * size + x])
                return false;
        return true;
    }

    private void AddTilePolygon(BodyRecord2D record, B2ShapeDef def, ReadOnlySpan<B2Vec2> points)
    {
        var hull = b2ComputeHull(points, points.Length);
        if (hull.count < 3)
            return; // degenerate at this scale
        var polygon = b2MakePolygon(hull, 0f);
        var id = b2CreatePolygonShape(record.Body, def, polygon);
        if (id.index1 == 0)
            return;
        record.Shapes.Add(new ShapeEntry2D { Id = id, Geometry = new ShapeGeometry2D { Type = B2ShapeType.b2_polygonShape, Polygon = polygon } });
        _shapes[Key(id)] = new ShapeRef(record, null, record.Shapes.Count - 1);
    }

    // New shapes do not wake sleeping bodies resting where they appear: wake every dynamic body in the chunk's bounds.
    private void WakeInBounds(BodyRecord2D record)
    {
        if (record.Shapes.Count == 0)
            return;
        var aabb = b2Body_ComputeAABB(record.Body);
        _shapeScratch.Clear();
        BeginQuery(record);
        b2World_OverlapAABB(_world, aabb, QueryFilter(uint.MaxValue), OverlapCallback, this);
        _queryExclude = null;
        foreach (var id in _shapeScratch)
        {
            var body = b2Shape_GetBody(id);
            if (b2Body_GetType(body) == B2BodyType.b2_dynamicBody)
                b2Body_SetAwake(body, true);
        }

        _shapeScratch.Clear();
    }

    internal void DestroyTileBody(BodyRecord2D record)
    {
        if (record.Removed)
            return;
        record.Removed = true;
        foreach (var shape in record.Shapes)
            _shapes.Remove(Key(shape.Id));
        record.Shapes.Clear();
        if (record.HasBody && !_disposed)
        {
            WakeTouching(record);
            b2DestroyBody(record.Body);
        }

        record.HasBody = false;
        EmitExitsForRemoved(record);
    }

    private void DrawTileLayersDebug(DebugLines lines, bool forcedOnly)
    {
        if (_tileLayers.Count == 0)
            return;
        FlushTileLayers(); // the editor does not step: show the cells as they are now
        foreach (var tiles in _tileLayers)
        {
            var mode = tiles.Layer.CollisionVisibilityMode;
            if (mode == DebugVisibilityMode.ForceHide || (forcedOnly && mode != DebugVisibilityMode.ForceShow) || !tiles.Layer.IsVisibleInTree())
                continue;
            tiles.DrawDebug(lines, TileColor);
        }
    }

    internal void DrawTileBodyDebug(DebugLines lines, BodyRecord2D record, Vector4 color)
    {
        if (!record.HasBody)
            return;
        var transform = Transform2D.FromTrs(record.CurrentPosition, record.CurrentRotation, Vector2.One);
        Span<Vector2> points = stackalloc Vector2[TilePolygons.MaxVertices];
        foreach (var shape in record.Shapes)
        {
            var polygon = shape.Geometry.Polygon;
            var n = polygon.count;
            for (var i = 0; i < n; i++)
                points[i] = ToNumerics(polygon.vertices[i]) * PixelsPerMeter;
            lines.AddPolygon2D(transform, points[..n], color);
        }
    }
}

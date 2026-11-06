using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// G3.9: bakes a <see cref="NavigationPolygon"/> for a <see cref="NavigationRegion2D"/> from the scene, Godot's way. The
/// traversable area is the polygon's <see cref="NavigationPolygon.Outlines"/> (clipped to, or replaced by, its
/// <see cref="NavigationPolygon.BakingRect"/>); the obstructions are parsed from the nodes the
/// <see cref="NavigationPolygon.SourceGeometryMode"/> names — <see cref="StaticBody2D"/> collision shapes and
/// <see cref="TileMapLayer"/> collision polygons on <see cref="NavigationPolygon.ParsedCollisionMask"/> (static colliders),
/// <see cref="Polygon2D"/>s (mesh instances), and <see cref="NavigationObstacle2D"/>s with
/// <see cref="NavigationObstacle2D.AffectNavigationMesh"/> — all in the region's local space. Then
/// <see cref="NavigationPolygonBuilder"/> offsets by the agent radius and partitions into convex polygons. Reads nodes
/// only (never Box2D), on the main thread; allocates (edit time, level load).
/// </summary>
public static class NavigationPolygonBaker2D
{
    private const int CircleSegments = 24;

    internal static (Vector2[] Vertices, int[][] Polygons)? Bake(NavigationRegion2D region, NavigationPolygon polygon, out string? error)
    {
        error = null;
        var rect = polygon.BakingRect;
        if (polygon.Outlines.Length == 0 && !(rect.Size.X > 0f && rect.Size.Y > 0f))
        {
            error = "the navigation polygon has no outlines and no baking rectangle";
            return null;
        }

        var obstructions = new List<Vector2[]>();
        ParseSourceGeometry(region, polygon, obstructions);
        try
        {
            return NavigationPolygonBuilder.Build(polygon.Outlines, obstructions, polygon.AgentRadius, polygon.CellSize, rect);
        }
        catch (ArgumentOutOfRangeException e)
        {
            error = e.Message;
            return null;
        }
    }

    /// <summary>Appends the obstruction outlines <paramref name="polygon"/>'s settings parse for <paramref name="region"/> (its local pixels).</summary>
    public static void ParseSourceGeometry(NavigationRegion2D region, NavigationPolygon polygon, List<Vector2[]> obstructions)
    {
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(polygon);
        ArgumentNullException.ThrowIfNull(obstructions);
        var toLocal = region.GlobalTransform.AffineInverse();
        var parser = new Parser(polygon, toLocal, obstructions);
        if (polygon.SourceGeometryMode == NavigationSourceGeometryMode.RootNodeChildren || region.Tree is not { } tree)
        {
            foreach (var child in region.Children)
                parser.Parse(child, recurse: true);
            return;
        }

        foreach (var node in tree.GetNodesInGroup(polygon.SourceGeometryGroupName))
            parser.Parse(node, recurse: polygon.SourceGeometryMode == NavigationSourceGeometryMode.GroupsWithChildren);
    }

    private sealed class Parser(NavigationPolygon polygon, Transform2D toLocal, List<Vector2[]> output)
    {
        private readonly bool _colliders = polygon.ParsedGeometryType != NavigationParsedGeometryType.MeshInstances;
        private readonly bool _meshes = polygon.ParsedGeometryType != NavigationParsedGeometryType.StaticColliders;
        private readonly uint _mask = polygon.ParsedCollisionMask;
        private readonly List<Vector2I> _cells = [];

        public void Parse(Node node, bool recurse)
        {
            switch (node)
            {
                case StaticBody2D body when _colliders && (body.CollisionLayer & _mask) != 0:
                    foreach (var child in body.Children)
                        if (child is CollisionShape2D { Disabled: false, Shape: { } shape } collision)
                            AddShape(shape, toLocal * collision.GlobalTransform);
                    break;
                case TileMapLayer layer when _colliders && layer.CollisionEnabled:
                    AddTiles(layer);
                    break;
                case Polygon2D shape when _meshes:
                    AddPolygon2D(shape);
                    break;
                case NavigationObstacle2D { AffectNavigationMesh: true } obstacle when obstacle.VerticesSpan.Length >= 3:
                    Add(obstacle.VerticesSpan, toLocal * obstacle.GlobalTransform);
                    break;
            }

            if (!recurse)
                return;
            foreach (var child in node.Children)
                Parse(child, recurse: true);
        }

        private void Add(ReadOnlySpan<Vector2> points, in Transform2D transform)
        {
            var outline = new Vector2[points.Length];
            for (var i = 0; i < points.Length; i++)
                outline[i] = transform.TransformPoint(points[i]);
            output.Add(outline);
        }

        private void AddShape(Shape2D shape, in Transform2D transform)
        {
            switch (shape)
            {
                case RectangleShape2D rectangle:
                {
                    var h = rectangle.Size * 0.5f;
                    Add([new(-h.X, -h.Y), new(h.X, -h.Y), new(h.X, h.Y), new(-h.X, h.Y)], transform);
                    break;
                }
                case CircleShape2D circle:
                {
                    Span<Vector2> points = stackalloc Vector2[CircleSegments];
                    for (var i = 0; i < CircleSegments; i++)
                    {
                        var angle = i * (2.0 * Math.PI / CircleSegments);
                        points[i] = new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle)) * circle.Radius;
                    }

                    Add(points, transform);
                    break;
                }
                case CapsuleShape2D capsule:
                {
                    // Vertical, like Godot's: two half circles joined by straight sides; Height is the whole length.
                    const int half = CircleSegments / 2;
                    Span<Vector2> points = stackalloc Vector2[half * 2 + 2];
                    var r = capsule.Radius;
                    var offset = MathF.Max(0f, capsule.Height * 0.5f - r);
                    var n = 0;
                    for (var i = 0; i <= half; i++)
                    {
                        var angle = i * (Math.PI / half);
                        points[n++] = new Vector2((float)Math.Cos(angle) * r, (float)Math.Sin(angle) * r + offset);
                    }

                    for (var i = 0; i <= half; i++)
                    {
                        var angle = Math.PI + i * (Math.PI / half);
                        points[n++] = new Vector2((float)Math.Cos(angle) * r, (float)Math.Sin(angle) * r - offset);
                    }

                    Add(points[..n], transform);
                    break;
                }
                case ConvexPolygonShape2D convex when convex.Points.Length >= 3:
                    Add(convex.Points, transform);
                    break;
                case ConcavePolygonShape2D concave when concave.Segments.Length >= 6:
                {
                    // Segment pairs round one closed outline (Godot's reading): every segment's first point.
                    var segments = concave.Segments;
                    var points = new Vector2[segments.Length / 2];
                    for (var i = 0; i < points.Length; i++)
                        points[i] = segments[i * 2];
                    Add(points, transform);
                    break;
                }
            }
        }

        private void AddTiles(TileMapLayer layer)
        {
            if (layer.TileSet is not { } set)
                return;
            var transform = toLocal * layer.GlobalTransform;
            Span<Vector2> points = stackalloc Vector2[TilePolygons.MaxVertices * 4];
            _cells.Clear();
            layer.GetUsedCells(_cells);
            foreach (var coords in _cells)
            {
                var cell = layer.GetCell(coords);
                if (cell.IsEmpty || set.GetTileData(cell) is not { Collision.Count: > 0 } data)
                    continue;
                var centre = layer.MapToLocal(coords);
                foreach (var collision in data.Collision)
                {
                    if (collision is null || collision.Points.Length < 3)
                        continue;
                    var physics = collision.PhysicsLayer;
                    var bits = physics >= 0 && physics < set.PhysicsLayers.Count && set.PhysicsLayers[physics] is { } info
                        ? info.CollisionLayer
                        : CollisionLayers.Default;
                    if ((bits & _mask) == 0)
                        continue;
                    var source = collision.Points;
                    var span = source.Length <= points.Length ? points[..source.Length] : new Vector2[source.Length];
                    for (var i = 0; i < source.Length; i++)
                        span[i] = centre + TilePolygons.Transform(source[i], cell.FlipH, cell.FlipV, cell.Transpose);
                    Add(span, transform);
                }
            }
        }

        private void AddPolygon2D(Polygon2D shape)
        {
            var points = shape.Polygon;
            if (points.Length < 3)
                return;
            var transform = toLocal * shape.GlobalTransform;
            var offset = shape.Offset;
            if (shape.Polygons is { Length: > 0 } polygons)
            {
                foreach (var indices in polygons)
                {
                    if (indices is not { Length: >= 3 })
                        continue;
                    var outline = new Vector2[indices.Length];
                    var valid = true;
                    for (var i = 0; i < indices.Length && valid; i++)
                    {
                        valid = (uint)indices[i] < (uint)points.Length;
                        if (valid)
                            outline[i] = points[indices[i]] + offset;
                    }

                    if (valid)
                        Add(outline, transform);
                }

                return;
            }

            var count = points.Length - Math.Clamp(shape.InternalVertexCount, 0, points.Length);
            if (count < 3)
                return;
            var ring = new Vector2[count];
            for (var i = 0; i < count; i++)
                ring[i] = points[i] + offset;
            Add(ring, transform);
        }
    }
}

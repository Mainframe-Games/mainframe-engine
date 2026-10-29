using System.Globalization;
using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// The physics source of <see cref="NavigationGrid2D"/>: a cell is solid when a <see cref="StaticBody2D"/> on
/// <see cref="NavigationGrid2D.BakeCollisionMask"/> overlaps it, grown by <see cref="NavigationGrid2D.AgentRadius"/> on
/// each side. One <see cref="PhysicsDirectSpaceState2D.IntersectShape"/> per cell with one reused rectangle, through the
/// public query API only (no Box2D calls here). Rigid and character bodies move, so they are not baked; areas are never
/// reported by queries. Main thread; allocates a little (edit time or a level load: a 128 × 128 grid is 16 384 overlap
/// tests, a few milliseconds).
/// </summary>
public sealed class GridPhysicsBaker2D : INavigationGridBaker
{
    // The query box shrinks by this much (pixels) on each side, so a wall that only touches a cell's edge leaves it walkable.
    private const float Inset = 0.1f;

    public bool Bake(NavigationGrid2D node, AStarGrid2D cells, out string? failure)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(cells);
        failure = null;
        if (node.GetViewport() is not { } viewport)
        {
            failure = "the grid is not inside a viewport";
            return false;
        }

        var space = viewport.World2D.DirectSpaceState;
        var size = cells.CellSize + new Vector2(2f * (node.AgentRadius - Inset));
        var box = new RectangleShape2D { Size = Vector2.Max(size, new Vector2(0.01f)) };
        var hits = new List<Node2D>(8);
        var region = cells.Region;
        var mask = node.BakeCollisionMask;
        for (var y = region.Position.Y; y < region.End.Y; y++)
        {
            for (var x = region.Position.X; x < region.End.X; x++)
            {
                var cell = new Vector2I(x, y);
                hits.Clear();
                space.IntersectShape(box, new Transform2D(0f, cells.GetPointPosition(cell)), hits, mask);
                if (HasStaticBody(hits))
                    cells.SetSolid(cells.Index(x, y), true);
            }
        }

        cells.BumpVersion();
        return true;
    }

    /// <summary>
    /// The static bodies overlapping the grid's area, hashed (their transforms, layers and shapes) in hex: the bake is out of
    /// date when this differs from <see cref="NavigationGridData.SourceHash"/>. One overlap query; order-independent.
    /// </summary>
    public string? SourceHash(NavigationGrid2D node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node.GetViewport() is not { } viewport)
            return null;
        var region = node.Region;
        var cellSize = node.CellSize;
        var area = new Vector2(region.Size.X, region.Size.Y) * cellSize + new Vector2(2f * node.AgentRadius);
        if (area.X <= 0f || area.Y <= 0f)
            return "";
        var centre = node.GlobalPosition + (new Vector2(region.Position.X, region.Position.Y) + new Vector2(region.Size.X, region.Size.Y) * 0.5f) * cellSize;
        var hits = new List<Node2D>(32);
        viewport.World2D.DirectSpaceState.IntersectShape(new RectangleShape2D { Size = area }, new Transform2D(0f, centre), hits, node.BakeCollisionMask);
        ulong hash = 0;
        foreach (var hit in hits)
            if (hit is StaticBody2D body)
                hash ^= Hash(body); // XOR: the query's order does not matter
        return hash.ToString("x16", CultureInfo.InvariantCulture);
    }

    private static bool HasStaticBody(List<Node2D> hits)
    {
        // Hits are Node2D since tile maps collide (ADR 0225): a tile layer's chunk bodies are static too. Its cells are not
        // in the out-of-date hash yet; the tile map source (G3.6) tracks them.
        foreach (var hit in hits)
            if (hit is StaticBody2D or TileMapLayer)
                return true;
        return false;
    }

    private static ulong Hash(StaticBody2D body)
    {
        var h = new Fnv();
        h.Add(body.GlobalTransform);
        h.Add(body.CollisionLayer);
        for (var i = 0; i < body.ChildCount; i++)
        {
            if (body.GetChild(i) is not CollisionShape2D owner)
                continue;
            h.Add(owner.GlobalTransform);
            h.Add(owner.Disabled ? 1u : 0u);
            switch (owner.Shape)
            {
                case RectangleShape2D r:
                    h.Add(1u);
                    h.Add(r.Size);
                    break;
                case CircleShape2D c:
                    h.Add(2u);
                    h.Add(c.Radius);
                    break;
                case CapsuleShape2D c:
                    h.Add(3u);
                    h.Add(c.Radius);
                    h.Add(c.Height);
                    break;
                case ConvexPolygonShape2D p:
                    h.Add(4u);
                    foreach (var point in p.Points)
                        h.Add(point);
                    break;
                case SegmentShape2D s:
                    h.Add(5u);
                    h.Add(s.A);
                    h.Add(s.B);
                    break;
                case ConcavePolygonShape2D p:
                    h.Add(6u);
                    foreach (var point in p.Segments)
                        h.Add(point);
                    break;
                case null:
                    h.Add(0u);
                    break;
                default:
                    h.Add(7u);
                    break;
            }
        }

        return h.Value;
    }

    // FNV-1a over the raw bits of floats and integers.
    private struct Fnv()
    {
        public ulong Value { get; private set; } = 14695981039346656037ul;

        public void Add(uint value)
        {
            for (var i = 0; i < 4; i++)
            {
                Value ^= (value >> (8 * i)) & 0xFF;
                Value *= 1099511628211ul;
            }
        }

        public void Add(float value) => Add(BitConverter.SingleToUInt32Bits(value));

        public void Add(Vector2 value)
        {
            Add(value.X);
            Add(value.Y);
        }

        public void Add(in Transform2D t)
        {
            Add(t.X);
            Add(t.Y);
            Add(t.Origin);
        }
    }
}

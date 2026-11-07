using System.Globalization;
using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// G3.11: the physics bake of a <see cref="NavigationGrid3D"/> through the 3D query API only: one overlap box per cell —
/// the cell grown by the agent radius (inset 1 mm so a wall that only touches a cell's edge leaves it walkable), from
/// <see cref="NavigationGrid3D.AgentMaxClimb"/> to <see cref="NavigationGrid3D.AgentHeight"/> above the grid — masked by
/// <see cref="NavigationGrid3D.BakeCollisionMask"/>; only <see cref="StaticBody3D"/> hits count. Main thread.
/// </summary>
internal static class GridPhysicsBaker3D
{
    private const float Inset = 0.001f;

    public static void Bake(NavigationGrid3D node, World3D world, AStarGrid2D cells)
    {
        var space = world.DirectSpaceState;
        var bottom = node.AgentMaxClimb;
        var top = MathF.Max(node.AgentHeight, bottom + 0.01f);
        var size = cells.CellSize + new Vector2(2f * (node.AgentRadius - Inset));
        var box = new BoxShape3D { Size = new Vector3(MathF.Max(size.X, 0.001f), top - bottom, MathF.Max(size.Y, 0.001f)) };
        var y = node.Height + (bottom + top) * 0.5f;
        var hits = new List<CollisionObject3D>(8);
        var region = cells.Region;
        var mask = node.BakeCollisionMask;
        for (var z = region.Position.Y; z < region.End.Y; z++)
        {
            for (var x = region.Position.X; x < region.End.X; x++)
            {
                var centre = cells.GetPointPosition(new Vector2I(x, z));
                hits.Clear();
                space.IntersectShape(box, new Transform3D(Basis.Identity, new Vector3(centre.X, y, centre.Y)), hits, mask);
                foreach (var hit in hits)
                {
                    if (hit is not StaticBody3D)
                        continue;
                    cells.SetSolid(cells.Index(x, z), true);
                    break;
                }
            }
        }

        cells.BumpVersion();
    }

    /// <summary>A hash of the static bodies one overlap query over the region finds (their transforms, layers and shapes).</summary>
    public static string SourceHash(NavigationGrid3D node, World3D world)
    {
        var region = node.Region;
        var cell = node.CellSize;
        var origin = node.GlobalPosition;
        var area = new Vector2(region.Size.X, region.Size.Y) * cell + new Vector2(2f * node.AgentRadius);
        if (area.X <= 0f || area.Y <= 0f)
            return "";
        var bottom = node.AgentMaxClimb;
        var top = MathF.Max(node.AgentHeight, bottom + 0.01f);
        var centre = new Vector3(origin.X + (region.Position.X + region.Size.X * 0.5f) * cell.X, origin.Y + (bottom + top) * 0.5f,
            origin.Z + (region.Position.Y + region.Size.Y * 0.5f) * cell.Y);
        var hits = new List<CollisionObject3D>(32);
        world.DirectSpaceState.IntersectShape(new BoxShape3D { Size = new Vector3(area.X, top - bottom, area.Y) },
            new Transform3D(Basis.Identity, centre), hits, node.BakeCollisionMask);
        ulong hash = 0;
        foreach (var hit in hits)
            if (hit is StaticBody3D body)
                hash ^= Hash(body); // XOR: the query's order does not matter
        return hash.ToString("x16", CultureInfo.InvariantCulture);
    }

    private static ulong Hash(StaticBody3D body)
    {
        var h = 14695981039346656037UL;
        void Add(float f)
        {
            h ^= BitConverter.SingleToUInt32Bits(f);
            h *= 1099511628211UL;
        }

        void AddTransform(in Transform3D t)
        {
            Add(t.Basis.X.X); Add(t.Basis.X.Y); Add(t.Basis.X.Z);
            Add(t.Basis.Y.X); Add(t.Basis.Y.Y); Add(t.Basis.Y.Z);
            Add(t.Basis.Z.X); Add(t.Basis.Z.Y); Add(t.Basis.Z.Z);
            Add(t.Origin.X); Add(t.Origin.Y); Add(t.Origin.Z);
        }

        AddTransform(body.GlobalTransform);
        Add(BitConverter.UInt32BitsToSingle(body.CollisionLayer));
        foreach (var child in body.Children)
        {
            if (child is not CollisionShape3D shape)
                continue;
            AddTransform(shape.GlobalTransform);
            Add(shape.Disabled ? 1f : 0f);
            Add(shape.Shape?.GetType().Name.Length ?? 0);
            if (shape.Shape is BoxShape3D box)
            {
                Add(box.Size.X); Add(box.Size.Y); Add(box.Size.Z);
            }
        }

        return h;
    }
}

using System.Numerics;
using Clipper2Lib;

namespace MainframeEngine;

/// <summary>
/// G3.9: turns traversable outlines and obstruction outlines into a navigation polygon's convex polygons. Godot's 2D
/// recipe on Clipper2 (ADR 0203): the traversable outlines (even-odd: an outline inside another is a hole; clipped to the
/// baking rectangle, or the rectangle alone) shrink by the agent radius, the obstructions (non-zero union) grow by it,
/// the difference is partitioned into convex polygons (<see cref="PolygonPartition"/>). Everything runs on Clipper2's
/// integer grid with one unit per <c>cellSize</c> pixels and miter joins (limit 2, Godot's), so a bake is exact and the
/// same on every run.
/// </summary>
internal static class NavigationPolygonBuilder
{
    /// <summary>The largest coordinate in grid units (keeps every cross product inside a <see cref="long"/>).</summary>
    public const long MaxCoordinate = 1L << 30;

    public static (Vector2[] Vertices, int[][] Polygons) Build(IReadOnlyList<Vector2[]> traversable,
        IReadOnlyList<Vector2[]> obstructions, float agentRadius, float cellSize, Rect2 bakingRect)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cellSize);
        var scale = 1.0 / cellSize;
        var subject = new Paths64(traversable.Count);
        foreach (var outline in traversable)
            if (ToPath(outline, scale) is { } path)
                subject.Add(path);
        var walkable = subject.Count > 0 ? Clipper.Union(subject, FillRule.EvenOdd) : [];

        if (bakingRect.Size.X > 0f && bakingRect.Size.Y > 0f)
        {
            var r = bakingRect;
            var rect = ToPath([r.Position, new Vector2(r.End.X, r.Position.Y), r.End, new Vector2(r.Position.X, r.End.Y)], scale)!;
            walkable = walkable.Count == 0 ? [rect] : Clipper.Intersect(walkable, [rect], FillRule.NonZero);
        }

        if (walkable.Count == 0)
            return ([], []);

        var delta = agentRadius * scale;
        if (delta > 0)
            walkable = Clipper.InflatePaths(walkable, -delta, JoinType.Miter, EndType.Polygon, 2.0);

        var clip = new Paths64(obstructions.Count);
        foreach (var outline in obstructions)
            if (ToPath(outline, scale) is { } path)
                clip.Add(path);
        if (clip.Count > 0)
        {
            clip = Clipper.Union(clip, FillRule.NonZero);
            if (delta > 0)
                clip = Clipper.InflatePaths(clip, delta, JoinType.Miter, EndType.Polygon, 2.0);
        }

        var clipper = new Clipper64 { PreserveCollinear = false };
        clipper.AddSubject(walkable);
        if (clip.Count > 0)
            clipper.AddClip(clip);
        var tree = new PolyTree64();
        clipper.Execute(clip.Count > 0 ? ClipType.Difference : ClipType.Union, FillRule.NonZero, tree);

        var points = new List<Point64>();
        var weld = new Dictionary<Point64, int>();
        var polygons = new List<int[]>();
        var partition = new PolygonPartition();
        var holes = new List<Path64>();
        Walk(tree, partition, holes, points, weld, polygons);

        var vertices = new Vector2[points.Count];
        for (var i = 0; i < points.Count; i++)
            vertices[i] = new Vector2((float)(points[i].X * (double)cellSize), (float)(points[i].Y * (double)cellSize));
        return (vertices, polygons.ToArray());
    }

    // Outer polygons are the tree's non-hole nodes; their children are holes, whose children are islands (outer again).
    private static void Walk(PolyPath64 node, PolygonPartition partition, List<Path64> holes, List<Point64> points,
        Dictionary<Point64, int> weld, List<int[]> polygons)
    {
        for (var i = 0; i < node.Count; i++)
        {
            var outer = node.Child(i);
            holes.Clear();
            for (var h = 0; h < outer.Count; h++)
                if (outer.Child(h).Polygon is { Count: >= 3 } hole)
                    holes.Add(hole);
            if (outer.Polygon is { Count: >= 3 } ring)
                partition.Partition(ring, holes, points, weld, polygons);
            for (var h = 0; h < outer.Count; h++)
                Walk(outer.Child(h), partition, holes, points, weld, polygons);
        }
    }

    private static Path64? ToPath(ReadOnlySpan<Vector2> outline, double scale)
    {
        if (outline.Length < 3)
            return null;
        var path = new Path64(outline.Length);
        foreach (var p in outline)
        {
            if (!float.IsFinite(p.X) || !float.IsFinite(p.Y))
                return null;
            var x = (long)Math.Round(p.X * scale, MidpointRounding.AwayFromZero);
            var y = (long)Math.Round(p.Y * scale, MidpointRounding.AwayFromZero);
            if (Math.Abs(x) > MaxCoordinate || Math.Abs(y) > MaxCoordinate)
                throw new ArgumentOutOfRangeException(nameof(outline), $"An outline point ({p.X}, {p.Y}) is too far out for the cell size.");
            path.Add(new Point64(x, y));
        }

        return path;
    }
}

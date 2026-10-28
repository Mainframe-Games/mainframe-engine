using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// The polygon maths of tile collision (no Box2D): Godot's flip/transpose of tile polygons, the full-tile test that
/// lets square tiles merge into rectangles, and the decomposition of concave or large polygons into convex pieces of
/// at most <see cref="MaxVertices"/> points (ear clipping, then Hertel–Mehlhorn merging).
/// </summary>
internal static class TilePolygons
{
    /// <summary>Box2D's polygon vertex limit.</summary>
    public const int MaxVertices = ConvexPolygonShape2D.MaxPoints;

    /// <summary>Godot's <c>TileData::get_transformed_vertices</c>: transpose first, then the flips.</summary>
    public static Vector2 Transform(Vector2 p, bool flipH, bool flipV, bool transpose)
    {
        if (transpose)
            p = new Vector2(p.Y, p.X);
        if (flipH)
            p.X = -p.X;
        if (flipV)
            p.Y = -p.Y;
        return p;
    }

    /// <summary>True when the polygon is exactly the tile's square (corners at ±<paramref name="half"/>, any order).</summary>
    public static bool IsFullTile(ReadOnlySpan<Vector2> points, Vector2 half)
    {
        if (points.Length != 4)
            return false;
        var seen = 0;
        const float e = 1e-3f;
        foreach (var p in points)
        {
            if (MathF.Abs(MathF.Abs(p.X) - half.X) > e || MathF.Abs(MathF.Abs(p.Y) - half.Y) > e)
                return false;
            seen |= 1 << ((p.X > 0 ? 1 : 0) | (p.Y > 0 ? 2 : 0));
        }

        return seen == 0b1111;
    }

    /// <summary>True when every turn of the polygon goes the same way (collinear points allowed).</summary>
    public static bool IsConvex(ReadOnlySpan<Vector2> points)
    {
        if (points.Length < 3)
            return false;
        var sign = 0;
        for (var i = 0; i < points.Length; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Length];
            var c = points[(i + 2) % points.Length];
            var cross = Cross(b - a, c - b);
            if (MathF.Abs(cross) < 1e-6f)
                continue;
            var s = cross > 0 ? 1 : -1;
            if (sign == 0)
                sign = s;
            else if (s != sign)
                return false;
        }

        return sign != 0;
    }

    /// <summary>
    /// Appends convex pieces of at most <see cref="MaxVertices"/> points covering <paramref name="points"/>: the polygon
    /// itself when it is convex and small enough, else its ear-clipped triangles merged greedily across their shared
    /// diagonals (Hertel–Mehlhorn). False when the outline cannot be triangulated (self-intersecting).
    /// </summary>
    public static bool Decompose(ReadOnlySpan<Vector2> points, List<Vector2[]> output)
    {
        if (points.Length < 3)
            return false;
        if (points.Length <= MaxVertices && IsConvex(points))
        {
            output.Add(points.ToArray());
            return true;
        }

        if (CanvasPrimitives.Triangulate(points) is not { Length: >= 3 } triangles)
            return false;
        var pieces = new List<List<int>>(triangles.Length / 3);
        for (var i = 0; i + 2 < triangles.Length; i += 3)
            pieces.Add([triangles[i], triangles[i + 1], triangles[i + 2]]);
        var sign = Orientation(points, pieces[0]);

        // Merge across shared edges while the result stays convex and small (the edge is a diagonal: both pieces have it).
        var merged = true;
        while (merged)
        {
            merged = false;
            for (var a = 0; a < pieces.Count && !merged; a++)
                for (var b = a + 1; b < pieces.Count && !merged; b++)
                {
                    if (TryMerge(points, pieces[a], pieces[b], sign) is not { } union)
                        continue;
                    pieces[a] = union;
                    pieces.RemoveAt(b);
                    merged = true;
                }
        }

        foreach (var piece in pieces)
        {
            if (MathF.Abs(IndexedArea(points, piece)) < 1e-4f)
                continue; // a sliver between collinear points: nothing to collide with
            var polygon = new Vector2[piece.Count];
            for (var i = 0; i < piece.Count; i++)
                polygon[i] = points[piece[i]];
            output.Add(polygon);
        }

        return true;
    }

    private static List<int>? TryMerge(ReadOnlySpan<Vector2> points, List<int> a, List<int> b, int sign)
    {
        if (a.Count + b.Count - 2 > MaxVertices)
            return null;
        for (var i = 0; i < a.Count; i++)
        {
            var u = a[i];
            var v = a[(i + 1) % a.Count];
            for (var j = 0; j < b.Count; j++)
            {
                if (b[j] != v || b[(j + 1) % b.Count] != u)
                    continue;
                // a from v around to u, then b's vertices after u up to before v.
                var union = new List<int>(a.Count + b.Count - 2);
                for (var k = 0; k < a.Count; k++)
                    union.Add(a[(i + 1 + k) % a.Count]);
                for (var k = 2; k < b.Count; k++)
                    union.Add(b[(j + k) % b.Count]);
                return IsConvexIndexed(points, union, sign) ? union : null;
            }
        }

        return null;
    }

    private static bool IsConvexIndexed(ReadOnlySpan<Vector2> points, List<int> polygon, int sign)
    {
        for (var i = 0; i < polygon.Count; i++)
        {
            var a = points[polygon[i]];
            var b = points[polygon[(i + 1) % polygon.Count]];
            var c = points[polygon[(i + 2) % polygon.Count]];
            var cross = Cross(b - a, c - b);
            if (cross * sign < -1e-6f)
                return false;
        }

        return true;
    }

    private static float IndexedArea(ReadOnlySpan<Vector2> points, List<int> polygon)
    {
        var area = 0f;
        for (var i = 0; i < polygon.Count; i++)
            area += Cross(points[polygon[i]], points[polygon[(i + 1) % polygon.Count]]);
        return area / 2;
    }

    private static int Orientation(ReadOnlySpan<Vector2> points, List<int> triangle) =>
        Cross(points[triangle[1]] - points[triangle[0]], points[triangle[2]] - points[triangle[1]]) >= 0 ? 1 : -1;

    private static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;
}

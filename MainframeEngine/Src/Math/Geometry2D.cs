using System.Numerics;
using Clipper2Lib;

namespace MainframeEngine;

/// <summary>
/// Godot's <c>Geometry2D</c> polygon helpers (ADR 0203): booleans, offsets and triangulation. The booleans and offsets
/// wrap Clipper2 with Godot's settings (5 decimal digits of precision, even-odd fill, collinear points removed; offsets
/// with a miter limit of 2 and an arc tolerance of 0.25), so the results match Godot's. Polygons are point lists in either
/// winding; results may hold several polygons, and holes come out with the opposite winding
/// (<see cref="IsPolygonClockwise"/>).
/// </summary>
public static class Geometry2D
{
    /// <summary>How offset corners are joined (Godot's <c>PolyJoinType</c>).</summary>
    public enum PolyJoinType
    {
        Square,
        Round,
        Miter,
    }

    /// <summary>How the ends of an offset polyline are capped (Godot's <c>PolyEndType</c>).</summary>
    public enum PolyEndType
    {
        /// <summary>A closed polygon (<see cref="OffsetPolygon"/>).</summary>
        Polygon,

        /// <summary>The polyline is closed and offset as a line.</summary>
        Joined,
        Butt,
        Square,
        Round,
    }

    // Godot's clipper_precision, "based on CMP_EPSILON".
    private const int Precision = 5;

    /// <summary>The union of two polygons (Godot's <c>merge_polygons</c>): one polygon, or both when they do not touch.</summary>
    public static Vector2[][] MergePolygons(ReadOnlySpan<Vector2> a, ReadOnlySpan<Vector2> b) => Boolean(ClipType.Union, a, b, open: false);

    /// <summary><paramref name="a"/> minus <paramref name="b"/> (Godot's <c>clip_polygons</c>).</summary>
    public static Vector2[][] ClipPolygons(ReadOnlySpan<Vector2> a, ReadOnlySpan<Vector2> b) => Boolean(ClipType.Difference, a, b, open: false);

    /// <summary>The area inside both polygons (Godot's <c>intersect_polygons</c>).</summary>
    public static Vector2[][] IntersectPolygons(ReadOnlySpan<Vector2> a, ReadOnlySpan<Vector2> b) => Boolean(ClipType.Intersection, a, b, open: false);

    /// <summary>The area inside exactly one of the polygons (Godot's <c>exclude_polygons</c>, xor).</summary>
    public static Vector2[][] ExcludePolygons(ReadOnlySpan<Vector2> a, ReadOnlySpan<Vector2> b) => Boolean(ClipType.Xor, a, b, open: false);

    /// <summary>The parts of <paramref name="polyline"/> outside <paramref name="polygon"/> (Godot's <c>clip_polyline_with_polygon</c>).</summary>
    public static Vector2[][] ClipPolylineWithPolygon(ReadOnlySpan<Vector2> polyline, ReadOnlySpan<Vector2> polygon) =>
        Boolean(ClipType.Difference, polyline, polygon, open: true);

    /// <summary>The parts of <paramref name="polyline"/> inside <paramref name="polygon"/> (Godot's <c>intersect_polyline_with_polygon</c>).</summary>
    public static Vector2[][] IntersectPolylineWithPolygon(ReadOnlySpan<Vector2> polyline, ReadOnlySpan<Vector2> polygon) =>
        Boolean(ClipType.Intersection, polyline, polygon, open: true);

    /// <summary>
    /// Grows (<paramref name="delta"/> &gt; 0) or shrinks a polygon by <paramref name="delta"/> (Godot's
    /// <c>offset_polygon</c>); shrinking can split it, or leave nothing.
    /// </summary>
    public static Vector2[][] OffsetPolygon(ReadOnlySpan<Vector2> polygon, float delta, PolyJoinType joinType = PolyJoinType.Square) =>
        Offset(polygon, delta, joinType, PolyEndType.Polygon);

    /// <summary>
    /// The outline of <paramref name="polyline"/> thickened by <paramref name="delta"/> on each side (Godot's
    /// <c>offset_polyline</c>). <paramref name="endType"/> cannot be <see cref="PolyEndType.Polygon"/>.
    /// </summary>
    public static Vector2[][] OffsetPolyline(ReadOnlySpan<Vector2> polyline, float delta, PolyJoinType joinType = PolyJoinType.Square, PolyEndType endType = PolyEndType.Square)
    {
        if (endType == PolyEndType.Polygon)
            throw new ArgumentException("Offset a polygon with OffsetPolygon, not OffsetPolyline.", nameof(endType));
        return Offset(polyline, delta, joinType, endType);
    }

    /// <summary>
    /// Triangle indices into <paramref name="polygon"/> (Godot's <c>triangulate_polygon</c>, ear clipping), three per
    /// triangle; empty when the polygon cannot be triangulated (fewer than 3 points, self-intersecting).
    /// </summary>
    public static int[] TriangulatePolygon(ReadOnlySpan<Vector2> polygon) => CanvasPrimitives.Triangulate(polygon) ?? [];

    /// <summary>
    /// True when the points wind clockwise with +Y up (Godot's <c>is_polygon_clockwise</c>); with +Y down, as on the
    /// canvas, flip the answer.
    /// </summary>
    public static bool IsPolygonClockwise(ReadOnlySpan<Vector2> polygon)
    {
        var c = polygon.Length;
        if (c < 3)
            return false;
        var sum = 0f;
        for (var i = 0; i < c; i++)
        {
            var v1 = polygon[i];
            var v2 = polygon[(i + 1) % c];
            sum += (v2.X - v1.X) * (v2.Y + v1.Y);
        }

        return sum > 0f;
    }

    private static Vector2[][] Boolean(ClipType op, ReadOnlySpan<Vector2> a, ReadOnlySpan<Vector2> b, bool open)
    {
        var clipper = new ClipperD(Precision) { PreserveCollinear = false }; // removes redundant vertices
        if (open)
            clipper.AddOpenSubject([ToPath(a)]);
        else
            clipper.AddSubject([ToPath(a)]);
        clipper.AddClip([ToPath(b)]);

        var paths = new PathsD();
        if (open)
            clipper.Execute(op, FillRule.EvenOdd, new PolyTreeD(), paths); // the tree form fills the open paths
        else
            clipper.Execute(op, FillRule.EvenOdd, paths);
        return ToArrays(paths);
    }

    private static Vector2[][] Offset(ReadOnlySpan<Vector2> path, float delta, PolyJoinType joinType, PolyEndType endType)
    {
        var join = joinType switch
        {
            PolyJoinType.Round => JoinType.Round,
            PolyJoinType.Miter => JoinType.Miter,
            _ => JoinType.Square,
        };
        var end = endType switch
        {
            PolyEndType.Joined => EndType.Joined,
            PolyEndType.Butt => EndType.Butt,
            PolyEndType.Square => EndType.Square,
            PolyEndType.Round => EndType.Round,
            _ => EndType.Polygon,
        };
        return ToArrays(Clipper.InflatePaths([ToPath(path)], delta, join, end, 2.0, Precision, 0.25));
    }

    private static PathD ToPath(ReadOnlySpan<Vector2> points)
    {
        var path = new PathD(points.Length);
        foreach (var p in points)
            path.Add(new PointD(p.X, p.Y));
        return path;
    }

    private static Vector2[][] ToArrays(PathsD paths)
    {
        var result = new Vector2[paths.Count][];
        for (var i = 0; i < paths.Count; i++)
        {
            var path = paths[i];
            var points = new Vector2[path.Count];
            for (var j = 0; j < path.Count; j++)
                points[j] = new Vector2((float)path[j].x, (float)path[j].y);
            result[i] = points;
        }

        return result;
    }
}

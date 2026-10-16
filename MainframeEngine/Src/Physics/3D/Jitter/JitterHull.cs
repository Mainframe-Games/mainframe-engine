using System.Numerics;
using Jitter2.Collision.Shapes;
using Jitter2.LinearMath;

namespace MainframeEngine;

/// <summary>Convex hull triangles for collision-shape debug drawing (Jitter2's <c>ShapeHelper.Tessellate</c>, kept so the drawn hull is unchanged).</summary>
internal static class JitterHull
{
    /// <summary>The hull of <paramref name="source"/> as a triangle list (3 vertices each); empty below 4 points.</summary>
    public static Vector3[] Tessellate(Vector3[] source, int subdivisions)
    {
        if (source.Length < 4)
            return [];
        Span<JVector> points = source.Length <= 256 ? stackalloc JVector[source.Length] : new JVector[source.Length];
        for (var i = 0; i < source.Length; i++)
            points[i] = source[i].ToJ();
        var hull = ShapeHelper.Tessellate(points, subdivisions);
        var triangles = new Vector3[hull.Count * 3];
        for (var i = 0; i < hull.Count; i++)
        {
            triangles[i * 3] = hull[i].V0.ToNumerics();
            triangles[i * 3 + 1] = hull[i].V1.ToNumerics();
            triangles[i * 3 + 2] = hull[i].V2.ToNumerics();
        }

        return triangles;
    }
}

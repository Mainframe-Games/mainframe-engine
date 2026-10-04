using System.Numerics;

namespace MainframeEngine;

/// <summary>An axis-aligned bounding box (Godot's <c>AABB</c>, stored as min/max corners).</summary>
public readonly record struct Aabb(Vector3 Min, Vector3 Max)
{
    /// <summary>An empty box: <see cref="Encapsulate(Vector3)"/> on it yields the point.</summary>
    public static Aabb Empty => new(new Vector3(float.PositiveInfinity), new Vector3(float.NegativeInfinity));

    /// <summary>True when the box contains no point (min &gt; max on some axis).</summary>
    public bool IsEmpty => Min.X > Max.X || Min.Y > Max.Y || Min.Z > Max.Z;

    public Vector3 Center => (Min + Max) * 0.5f;

    /// <summary>Half the size on each axis.</summary>
    public Vector3 Extents => (Max - Min) * 0.5f;

    public Vector3 Size => Max - Min;

    /// <summary>The smallest box containing every point in <paramref name="points"/> (<see cref="Empty"/> for none).</summary>
    public static Aabb FromPoints(ReadOnlySpan<Vector3> points)
    {
        if (points.IsEmpty)
            return Empty;
        var min = points[0];
        var max = points[0];
        for (var i = 1; i < points.Length; i++)
        {
            min = Vector3.Min(min, points[i]);
            max = Vector3.Max(max, points[i]);
        }

        return new Aabb(min, max);
    }

    /// <summary>The box grown to contain <paramref name="point"/>.</summary>
    public Aabb Encapsulate(Vector3 point) => new(Vector3.Min(Min, point), Vector3.Max(Max, point));

    /// <summary>The smallest box containing both boxes (empty boxes are ignored).</summary>
    public Aabb Merge(in Aabb other)
    {
        if (other.IsEmpty) return this;
        if (IsEmpty) return other;
        return new Aabb(Vector3.Min(Min, other.Min), Vector3.Max(Max, other.Max));
    }

    /// <summary>
    /// The box enclosing this box transformed by <paramref name="matrix"/> (row-vector convention: <c>v · M</c>, as
    /// <see cref="Node3D.ModelMatrix"/>). Exact for the transformed corners (Arvo's method).
    /// </summary>
    public Aabb Transform(in Matrix4x4 matrix)
    {
        if (IsEmpty)
            return this;
        var center = Vector3.Transform(Center, matrix);
        var e = Extents;
        var extents = new Vector3(
            MathF.Abs(matrix.M11) * e.X + MathF.Abs(matrix.M21) * e.Y + MathF.Abs(matrix.M31) * e.Z,
            MathF.Abs(matrix.M12) * e.X + MathF.Abs(matrix.M22) * e.Y + MathF.Abs(matrix.M32) * e.Z,
            MathF.Abs(matrix.M13) * e.X + MathF.Abs(matrix.M23) * e.Y + MathF.Abs(matrix.M33) * e.Z);
        return new Aabb(center - extents, center + extents);
    }

    public bool Contains(Vector3 point) =>
        point.X >= Min.X && point.Y >= Min.Y && point.Z >= Min.Z &&
        point.X <= Max.X && point.Y <= Max.Y && point.Z <= Max.Z;
}

/// <summary>
/// The six planes of a view-projection frustum (Vulkan clip space: x, y in [-w, w], z in [0, w]), for culling
/// bounding boxes. Planes point inwards.
/// </summary>
public readonly struct Frustum
{
    private readonly Vector4 _left, _right, _bottom, _top, _near, _far;

    /// <summary>
    /// Extracts the planes from a row-vector view-projection matrix (<c>clip = v · VP</c>, System.Numerics with
    /// Vulkan's [0, 1] depth).
    /// </summary>
    public Frustum(in Matrix4x4 viewProjection)
    {
        var m = viewProjection;
        var c0 = new Vector4(m.M11, m.M21, m.M31, m.M41);
        var c1 = new Vector4(m.M12, m.M22, m.M32, m.M42);
        var c2 = new Vector4(m.M13, m.M23, m.M33, m.M43);
        var c3 = new Vector4(m.M14, m.M24, m.M34, m.M44);
        _left = c3 + c0;
        _right = c3 - c0;
        _bottom = c3 + c1;
        _top = c3 - c1;
        _near = c2;
        _far = c3 - c2;
    }

    /// <summary>True when the box is at least partly inside the frustum (conservative: may keep boxes just outside a corner).</summary>
    public bool Intersects(in Aabb box)
    {
        if (box.IsEmpty)
            return false;
        var center = box.Center;
        var extents = box.Extents;
        return Inside(_left, center, extents) && Inside(_right, center, extents) &&
               Inside(_bottom, center, extents) && Inside(_top, center, extents) &&
               Inside(_near, center, extents) && Inside(_far, center, extents);
    }

    private static bool Inside(in Vector4 plane, Vector3 center, Vector3 extents)
    {
        var normal = new Vector3(plane.X, plane.Y, plane.Z);
        var distance = Vector3.Dot(normal, center) + plane.W;
        var radius = Vector3.Dot(Vector3.Abs(normal), extents);
        return distance + radius >= 0f;
    }
}

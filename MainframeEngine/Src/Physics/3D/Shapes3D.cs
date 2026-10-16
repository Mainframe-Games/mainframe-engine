using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// Base of 3D collision shape resources (Godot's <c>Shape3D</c>). A <see cref="CollisionShape3D"/> child gives a body
/// its shape; the same resource can be shared by many shapes and is saved with the scene (inline) or as a
/// <c>.mres</c>. Changing a property rebuilds every body using it at the next physics step.
/// </summary>
[EditorIcon("shape")]
public abstract class Shape3D : Resource
{
    /// <summary>True for triangle-based shapes that only static (and kinematic) bodies may use.</summary>
    public virtual bool IsConcave => false;

    /// <summary>Bumped by every property change that affects the geometry (backends cache native shapes by it).</summary>
    internal int ChangeCount { get; private set; }

    /// <summary>
    /// Adds this shape's neutral description for one attachment: <paramref name="linear"/> (rotation × scale) and
    /// <paramref name="translation"/> place it in its body's frame.
    /// </summary>
    internal virtual void Describe(List<ShapeDescription3D> output, in Basis linear, Vector3 translation) =>
        output.Add(DescribeConvex(linear, translation));

    /// <summary>The description of a convex shape (concave shapes throw <see cref="NotSupportedException"/>).</summary>
    internal abstract ShapeDescription3D DescribeConvex(in Basis linear, Vector3 translation);

    /// <summary>The shape at its own origin and scale 1, for shape casts and overlap queries.</summary>
    internal ShapeDescription3D DescribeQuery()
    {
        if (IsConcave)
            throw new NotSupportedException($"{GetType().Name} cannot be used as a query shape (it is concave).");
        return DescribeConvex(Basis.Identity, Vector3.Zero);
    }

    private protected ShapeDescription3D Description(ShapeKind3D kind, in Basis linear, Vector3 translation) =>
        new() { Kind = kind, Resource = this, ChangeCount = ChangeCount, Linear = linear, Translation = translation };

    /// <summary>Wireframe of the shape under <paramref name="transform"/> (world space, including scale).</summary>
    internal abstract void DrawDebug(DebugLines lines, in Transform3D transform, Vector4 color);

    /// <summary>Call when a property changed: drops cached data and notifies users (<see cref="Resource.Changed"/>).</summary>
    protected void OnShapeChanged()
    {
        ChangeCount++;
        EmitChanged();
    }

    private protected static float Positive(float value, string name)
    {
        if (!(value > 0) || !float.IsFinite(value))
            throw new ArgumentOutOfRangeException(name, value, "Must be positive and finite.");
        return value;
    }
}

/// <summary>A box (Godot's <c>BoxShape3D</c>); <see cref="Size"/> is the full extent.</summary>
[EditorIcon("box")]
public sealed class BoxShape3D : Shape3D
{
    [Export]
    public Vector3 Size
    {
        get;
        set
        {
            Positive(value.X, nameof(Size));
            Positive(value.Y, nameof(Size));
            Positive(value.Z, nameof(Size));
            if (field == value)
                return;
            field = value;
            OnShapeChanged();
        }
    } = Vector3.One;

    internal override ShapeDescription3D DescribeConvex(in Basis linear, Vector3 translation) =>
        Description(ShapeKind3D.Box, linear, translation) with { Size = Size };

    internal override void DrawDebug(DebugLines lines, in Transform3D transform, Vector4 color) =>
        lines.AddBox(transform, Size * 0.5f, color);
}

/// <summary>A sphere (Godot's <c>SphereShape3D</c>).</summary>
[EditorIcon("sphere")]
public sealed class SphereShape3D : Shape3D
{
    [Export]
    public float Radius
    {
        get;
        set
        {
            Positive(value, nameof(Radius));
            if (field == value)
                return;
            field = value;
            OnShapeChanged();
        }
    } = 0.5f;

    internal override ShapeDescription3D DescribeConvex(in Basis linear, Vector3 translation) =>
        Description(ShapeKind3D.Sphere, linear, translation) with { Radius = Radius };

    internal override void DrawDebug(DebugLines lines, in Transform3D transform, Vector4 color) =>
        lines.AddSphere(transform, Radius, color);
}

/// <summary>A capsule along local Y (Godot's <c>CapsuleShape3D</c>); <see cref="Height"/> includes both caps.</summary>
[EditorIcon("capsule")]
public sealed class CapsuleShape3D : Shape3D
{
    [Export]
    public float Radius
    {
        get;
        set
        {
            Positive(value, nameof(Radius));
            if (field == value)
                return;
            field = value;
            OnShapeChanged();
        }
    } = 0.5f;

    /// <summary>Total height, caps included (at least 2 × <see cref="Radius"/>; smaller values are clamped).</summary>
    [Export]
    public float Height
    {
        get;
        set
        {
            Positive(value, nameof(Height));
            if (field == value)
                return;
            field = value;
            OnShapeChanged();
        }
    } = 2f;

    /// <summary>Length of the cylindrical middle part (Jitter2's capsule length).</summary>
    public float MidHeight => MathF.Max(0, Height - 2 * Radius);

    internal override ShapeDescription3D DescribeConvex(in Basis linear, Vector3 translation) =>
        Description(ShapeKind3D.Capsule, linear, translation) with { Radius = Radius, Height = Height };

    internal override void DrawDebug(DebugLines lines, in Transform3D transform, Vector4 color) =>
        lines.AddCapsule(transform, Radius, MathF.Max(Height, 2 * Radius), color);
}

/// <summary>A cylinder along local Y (Godot's <c>CylinderShape3D</c>).</summary>
[EditorIcon("cylinder")]
public sealed class CylinderShape3D : Shape3D
{
    [Export]
    public float Radius
    {
        get;
        set
        {
            Positive(value, nameof(Radius));
            if (field == value)
                return;
            field = value;
            OnShapeChanged();
        }
    } = 0.5f;

    [Export]
    public float Height
    {
        get;
        set
        {
            Positive(value, nameof(Height));
            if (field == value)
                return;
            field = value;
            OnShapeChanged();
        }
    } = 2f;

    internal override ShapeDescription3D DescribeConvex(in Basis linear, Vector3 translation) =>
        Description(ShapeKind3D.Cylinder, linear, translation) with { Radius = Radius, Height = Height };

    internal override void DrawDebug(DebugLines lines, in Transform3D transform, Vector4 color) =>
        lines.AddCylinder(transform, Radius, Height, color);
}

/// <summary>
/// The convex hull of a point cloud (Godot's <c>ConvexPolygonShape3D</c>). Jitter2 uses the points' support map
/// directly, so keep the set small (a few dozen points); the hull is only built for debug drawing (Jitter2's tessellation,
/// through <c>JitterHull</c>).
/// </summary>
[EditorIcon("polygon")]
public sealed class ConvexPolygonShape3D : Shape3D
{
    private Vector3[] _hullTriangles = [];
    private bool _hullValid;

    /// <summary>The points (at least 4, not coplanar). Assign a new array to change them.</summary>
    [Export]
    public Vector3[] Points
    {
        get;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            field = value;
            _hullValid = false;
            OnShapeChanged();
        }
    } = [];

    internal override ShapeDescription3D DescribeConvex(in Basis linear, Vector3 translation)
    {
        if (Points.Length < 4)
            throw new InvalidOperationException("ConvexPolygonShape3D needs at least 4 points.");
        return Description(ShapeKind3D.ConvexHull, linear, translation) with { Points = Points };
    }

    internal override void DrawDebug(DebugLines lines, in Transform3D transform, Vector4 color)
    {
        if (!_hullValid)
        {
            _hullValid = true;
            _hullTriangles = JitterHull.Tessellate(Points, 2);
        }

        lines.AddTriangles(transform, _hullTriangles, color);
    }
}

/// <summary>
/// A triangle soup (Godot's <c>ConcavePolygonShape3D</c>): every 3 entries of <see cref="Faces"/> form a triangle.
/// Static and kinematic bodies only (triangles have no volume, so no mass); one-sided against back faces.
/// </summary>
[EditorIcon("vector-triangle")]
public sealed class ConcavePolygonShape3D : Shape3D
{
    /// <summary>Triangle vertices, 3 per triangle (counter-clockwise when seen from the solid side's outside).</summary>
    [Export]
    public Vector3[] Faces
    {
        get;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value.Length % 3 != 0)
                throw new ArgumentException("Faces must hold 3 vertices per triangle.", nameof(value));
            field = value;
            OnShapeChanged();
        }
    } = [];

    public override bool IsConcave => true;

    internal override void Describe(List<ShapeDescription3D> output, in Basis linear, Vector3 translation) =>
        output.Add(Description(ShapeKind3D.ConcaveMesh, linear, translation) with { Points = Faces });

    internal override ShapeDescription3D DescribeConvex(in Basis linear, Vector3 translation) =>
        throw new NotSupportedException("ConcavePolygonShape3D has no single convex base shape.");

    internal override void DrawDebug(DebugLines lines, in Transform3D transform, Vector4 color) =>
        lines.AddTriangles(transform, Faces, color);
}

/// <summary>
/// A height field (Godot's <c>HeightMapShape3D</c>): <see cref="MapWidth"/> × <see cref="MapDepth"/> heights on a
/// 1-unit grid centred on the origin (scale the <see cref="CollisionShape3D"/> to change the cell size). Jitter2 has
/// no native height field, so it is built as a triangle mesh — static and kinematic bodies only.
/// </summary>
[EditorIcon("mountain")]
public sealed class HeightMapShape3D : Shape3D
{
    private Vector3[]? _faces;

    /// <summary>Samples along X (at least 2).</summary>
    [Export]
    public int MapWidth
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 2);
            if (field == value)
                return;
            field = value;
            Invalidate();
        }
    } = 2;

    /// <summary>Samples along Z (at least 2).</summary>
    [Export]
    public int MapDepth
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 2);
            if (field == value)
                return;
            field = value;
            Invalidate();
        }
    } = 2;

    /// <summary>Heights, row by row (<c>MapWidth × MapDepth</c> entries; missing entries are 0).</summary>
    [Export]
    public float[] MapData
    {
        get;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            field = value;
            Invalidate();
        }
    } = [0, 0, 0, 0];

    public override bool IsConcave => true;

    private void Invalidate()
    {
        _faces = null;
        OnShapeChanged();
    }

    private float Height(int x, int z)
    {
        var index = z * MapWidth + x;
        return index < MapData.Length ? MapData[index] : 0f;
    }

    /// <summary>The height field as a triangle list (two triangles per cell, counter-clockwise from above).</summary>
    public ReadOnlySpan<Vector3> GetFaces() => GetFaceArray();

    internal Vector3[] GetFaceArray()
    {
        if (_faces is not null)
            return _faces;
        var faces = new Vector3[(MapWidth - 1) * (MapDepth - 1) * 6];
        var ox = (MapWidth - 1) * 0.5f;
        var oz = (MapDepth - 1) * 0.5f;
        var n = 0;
        for (var z = 0; z < MapDepth - 1; z++)
        {
            for (var x = 0; x < MapWidth - 1; x++)
            {
                var p00 = new Vector3(x - ox, Height(x, z), z - oz);
                var p10 = new Vector3(x + 1 - ox, Height(x + 1, z), z - oz);
                var p01 = new Vector3(x - ox, Height(x, z + 1), z + 1 - oz);
                var p11 = new Vector3(x + 1 - ox, Height(x + 1, z + 1), z + 1 - oz);
                // Normals up (+Y): counter-clockwise seen from above.
                faces[n++] = p00;
                faces[n++] = p01;
                faces[n++] = p10;
                faces[n++] = p10;
                faces[n++] = p01;
                faces[n++] = p11;
            }
        }

        _faces = faces;
        return faces;
    }

    internal override void Describe(List<ShapeDescription3D> output, in Basis linear, Vector3 translation) =>
        output.Add(Description(ShapeKind3D.HeightMap, linear, translation) with { Points = GetFaceArray() });

    internal override ShapeDescription3D DescribeConvex(in Basis linear, Vector3 translation) =>
        throw new NotSupportedException("HeightMapShape3D has no single convex base shape.");

    internal override void DrawDebug(DebugLines lines, in Transform3D transform, Vector4 color) =>
        lines.AddTriangles(transform, GetFaces(), color);
}

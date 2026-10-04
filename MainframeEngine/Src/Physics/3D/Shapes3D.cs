using System.Numerics;
using Jitter2.Collision.Shapes;
using Jitter2.LinearMath;

namespace MainframeEngine;

/// <summary>
/// Base of 3D collision shape resources (Godot's <c>Shape3D</c>). A <see cref="CollisionShape3D"/> child gives a body
/// its shape; the same resource can be shared by many shapes and is saved with the scene (inline) or as a
/// <c>.mres</c>. Changing a property rebuilds every body using it at the next physics step.
/// </summary>
[EditorIcon("shape")]
public abstract class Shape3D : Resource
{
    private RigidBodyShape? _queryShape;

    /// <summary>True for triangle-based shapes that only static (and kinematic) bodies may use.</summary>
    public virtual bool IsConcave => false;

    /// <summary>
    /// Creates the Jitter2 shapes for one attachment: <paramref name="linear"/> (rotation × scale) and
    /// <paramref name="translation"/> place the shape in its body's frame.
    /// </summary>
    internal virtual void CreateShapes(List<RigidBodyShape> output, in Basis linear, Vector3 translation)
    {
        var shape = CreateBaseShape();
        if (JitterMath.IsIdentity(linear) && translation == Vector3.Zero)
            output.Add(shape);
        else
            output.Add(new TransformedShape(shape, translation.ToJ(), linear.ToJ()));
    }

    /// <summary>The shape in its own frame (convex shapes).</summary>
    internal abstract RigidBodyShape CreateBaseShape();

    /// <summary>An unattached instance of the base shape for queries (shape casts, overlaps); cached until changed.</summary>
    internal RigidBodyShape GetQueryShape()
    {
        if (IsConcave)
            throw new NotSupportedException($"{GetType().Name} cannot be used as a query shape (it is concave).");
        return _queryShape ??= CreateBaseShape();
    }

    /// <summary>Wireframe of the shape under <paramref name="transform"/> (world space, including scale).</summary>
    internal abstract void DrawDebug(DebugLines lines, in Transform3D transform, Vector4 color);

    /// <summary>Call when a property changed: drops cached data and notifies users (<see cref="Resource.Changed"/>).</summary>
    protected void OnShapeChanged()
    {
        _queryShape = null;
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

    internal override RigidBodyShape CreateBaseShape() => new BoxShape(Size.ToJ());

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

    internal override RigidBodyShape CreateBaseShape() => new SphereShape(Radius);

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

    internal override RigidBodyShape CreateBaseShape() => new CapsuleShape(Radius, MidHeight);

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

    // Note Jitter2's argument order: (height, radius).
    internal override RigidBodyShape CreateBaseShape() => new CylinderShape(Height, Radius);

    internal override void DrawDebug(DebugLines lines, in Transform3D transform, Vector4 color) =>
        lines.AddCylinder(transform, Radius, Height, color);
}

/// <summary>
/// The convex hull of a point cloud (Godot's <c>ConvexPolygonShape3D</c>). Jitter2 uses the points' support map
/// directly, so keep the set small (a few dozen points); the hull is only built for debug drawing.
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

    internal override RigidBodyShape CreateBaseShape()
    {
        if (Points.Length < 4)
            throw new InvalidOperationException("ConvexPolygonShape3D needs at least 4 points.");
        Span<JVector> points = Points.Length <= 256 ? stackalloc JVector[Points.Length] : new JVector[Points.Length];
        for (var i = 0; i < Points.Length; i++)
            points[i] = Points[i].ToJ();
        return new PointCloudShape(points);
    }

    internal override void DrawDebug(DebugLines lines, in Transform3D transform, Vector4 color)
    {
        if (!_hullValid)
        {
            _hullValid = true;
            _hullTriangles = [];
            if (Points.Length >= 4)
            {
                Span<JVector> points = Points.Length <= 256 ? stackalloc JVector[Points.Length] : new JVector[Points.Length];
                for (var i = 0; i < Points.Length; i++)
                    points[i] = Points[i].ToJ();
                var hull = ShapeHelper.Tessellate(points, 2);
                _hullTriangles = new Vector3[hull.Count * 3];
                for (var i = 0; i < hull.Count; i++)
                {
                    _hullTriangles[i * 3] = hull[i].V0.ToNumerics();
                    _hullTriangles[i * 3 + 1] = hull[i].V1.ToNumerics();
                    _hullTriangles[i * 3 + 2] = hull[i].V2.ToNumerics();
                }
            }
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

    internal override void CreateShapes(List<RigidBodyShape> output, in Basis linear, Vector3 translation) =>
        AddTriangleShapes(output, Faces, linear, translation);

    internal static void AddTriangleShapes(List<RigidBodyShape> output, ReadOnlySpan<Vector3> faces, in Basis linear, Vector3 translation)
    {
        if (faces.Length < 3)
            return;
        var soup = new JTriangle[faces.Length / 3];
        for (var i = 0; i < soup.Length; i++)
        {
            soup[i] = new JTriangle(
                (linear.Transform(faces[i * 3]) + translation).ToJ(),
                (linear.Transform(faces[i * 3 + 1]) + translation).ToJ(),
                (linear.Transform(faces[i * 3 + 2]) + translation).ToJ());
        }

        var mesh = new TriangleMesh(soup, ignoreDegenerated: true);
        for (var i = 0; i < mesh.Indices.Length; i++)
            output.Add(new TriangleShape(mesh, i));
    }

    internal override RigidBodyShape CreateBaseShape() =>
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
    public ReadOnlySpan<Vector3> GetFaces()
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

    internal override void CreateShapes(List<RigidBodyShape> output, in Basis linear, Vector3 translation) =>
        ConcavePolygonShape3D.AddTriangleShapes(output, GetFaces(), linear, translation);

    internal override RigidBodyShape CreateBaseShape() =>
        throw new NotSupportedException("HeightMapShape3D has no single convex base shape.");

    internal override void DrawDebug(DebugLines lines, in Transform3D transform, Vector4 color) =>
        lines.AddTriangles(transform, GetFaces(), color);
}

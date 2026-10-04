using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// A mesh generated from a few parameters (Godot's <c>PrimitiveMesh</c>): one surface, built on first use and
/// rebuilt when a parameter changes. Serialized by its parameters only. Conventions: Y up, counter-clockwise
/// front faces, UV origin top-left, centred on the origin.
/// </summary>
public abstract class PrimitiveMesh : Mesh
{
    private MeshSurface? _surface;

    /// <summary>Turns the mesh inside out: reversed winding and normals.</summary>
    [Export]
    public bool FlipFaces
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Rebuild();
        }
    }

    /// <summary>The surface's material (null: the engine default).</summary>
    [Export]
    public Material? Material
    {
        get;
        set
        {
            if (ReferenceEquals(field, value)) return;
            field = value;
            Rebuild();
        }
    }

    public override int SurfaceCount => 1;

    public override MeshSurface GetSurface(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(index, 0);
        if (_surface is null)
        {
            var builder = new MeshBuilder();
            Generate(builder);
            _surface = builder.Build(Material, FlipFaces);
        }

        return _surface;
    }

    private protected abstract void Generate(MeshBuilder builder);

    /// <summary>Drops the generated surface; the next access regenerates it.</summary>
    protected void Rebuild()
    {
        _surface = null;
        Invalidate();
    }

    private protected static int Clamp(int value, int min) => Math.Max(value, min);
}

/// <summary>An axis-aligned box (Godot's <c>BoxMesh</c>): 24 vertices, one 0..1 UV square per face.</summary>
public sealed class BoxMesh : PrimitiveMesh
{
    [Export]
    public Vector3 Size
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Rebuild();
        }
    } = Vector3.One;

    private protected override void Generate(MeshBuilder b)
    {
        var h = Size * 0.5f;
        // (normal, U, V) with U × V = normal, so (-1,-1) → (1,-1) → (1,1) is counter-clockwise from outside.
        Face(b, h, Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY);
        Face(b, h, -Vector3.UnitZ, -Vector3.UnitX, Vector3.UnitY);
        Face(b, h, Vector3.UnitX, -Vector3.UnitZ, Vector3.UnitY);
        Face(b, h, -Vector3.UnitX, Vector3.UnitZ, Vector3.UnitY);
        Face(b, h, Vector3.UnitY, Vector3.UnitX, -Vector3.UnitZ);
        Face(b, h, -Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ);
    }

    private static void Face(MeshBuilder b, Vector3 half, Vector3 n, Vector3 u, Vector3 v)
    {
        var center = n * half;
        var uAxis = u * half;
        var vAxis = v * half;
        var i0 = b.AddVertex(center - uAxis - vAxis, n, new Vector2(0, 1));
        var i1 = b.AddVertex(center + uAxis - vAxis, n, new Vector2(1, 1));
        var i2 = b.AddVertex(center + uAxis + vAxis, n, new Vector2(1, 0));
        var i3 = b.AddVertex(center - uAxis + vAxis, n, new Vector2(0, 0));
        b.AddQuad(i0, i1, i2, i3);
    }
}

/// <summary>A flat rectangle in the XZ plane facing +Y (Godot's <c>PlaneMesh</c>), optionally subdivided.</summary>
public sealed class PlaneMesh : PrimitiveMesh
{
    /// <summary>Width (X) and depth (Z).</summary>
    [Export]
    public Vector2 Size
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Rebuild();
        }
    } = new(2, 2);

    /// <summary>Extra subdivisions along X.</summary>
    [Export(Range = "0,256,1")]
    public int SubdivideWidth
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Rebuild();
        }
    }

    /// <summary>Extra subdivisions along Z.</summary>
    [Export(Range = "0,256,1")]
    public int SubdivideDepth
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Rebuild();
        }
    }

    private protected override void Generate(MeshBuilder b)
    {
        var columns = Clamp(SubdivideWidth, 0) + 1;
        var rows = Clamp(SubdivideDepth, 0) + 1;
        for (var j = 0; j <= rows; j++)
        {
            var v = (float)j / rows;
            for (var i = 0; i <= columns; i++)
            {
                var u = (float)i / columns;
                b.AddVertex(new Vector3((u - 0.5f) * Size.X, 0f, (v - 0.5f) * Size.Y), Vector3.UnitY, new Vector2(u, v));
            }
        }

        var stride = columns + 1;
        for (var j = 0; j < rows; j++)
            for (var i = 0; i < columns; i++)
            {
                var a = j * stride + i;
                b.AddQuad(a, a + stride, a + stride + 1, a + 1);
            }
    }
}

/// <summary>A rectangle in the XY plane facing +Z (Godot's <c>QuadMesh</c>).</summary>
public sealed class QuadMesh : PrimitiveMesh
{
    [Export]
    public Vector2 Size
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Rebuild();
        }
    } = Vector2.One;

    /// <summary>Offset of the quad's centre.</summary>
    [Export]
    public Vector3 CenterOffset
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Rebuild();
        }
    }

    private protected override void Generate(MeshBuilder b)
    {
        var h = Size * 0.5f;
        var o = CenterOffset;
        var i0 = b.AddVertex(o + new Vector3(-h.X, -h.Y, 0), Vector3.UnitZ, new Vector2(0, 1));
        var i1 = b.AddVertex(o + new Vector3(h.X, -h.Y, 0), Vector3.UnitZ, new Vector2(1, 1));
        var i2 = b.AddVertex(o + new Vector3(h.X, h.Y, 0), Vector3.UnitZ, new Vector2(1, 0));
        var i3 = b.AddVertex(o + new Vector3(-h.X, h.Y, 0), Vector3.UnitZ, new Vector2(0, 0));
        b.AddQuad(i0, i1, i2, i3);
    }
}

/// <summary>A UV sphere (Godot's <c>SphereMesh</c>); <see cref="Height"/> ≠ 2 × <see cref="Radius"/> gives an ellipsoid.</summary>
public sealed class SphereMesh : PrimitiveMesh
{
    [Export]
    public float Radius
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Rebuild();
        }
    } = 0.5f;

    [Export]
    public float Height
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Rebuild();
        }
    } = 1f;

    [Export(Range = "4,256,1")]
    public int RadialSegments
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Rebuild();
        }
    } = 64;

    [Export(Range = "2,256,1")]
    public int Rings
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Rebuild();
        }
    } = 32;

    /// <summary>Only the upper half (a dome closed by nothing).</summary>
    [Export]
    public bool IsHemisphere
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Rebuild();
        }
    }

    private protected override void Generate(MeshBuilder b)
    {
        var radial = Clamp(RadialSegments, 3);
        var rings = Clamp(Rings, 2);
        var halfHeight = Height * 0.5f;
        var maxPhi = IsHemisphere ? MathF.PI * 0.5f : MathF.PI;
        // Ellipsoid normal: gradient of x²/r² + y²/h² + z²/r².
        var invR2 = 1f / MathF.Max(Radius * Radius, 1e-12f);
        var invH2 = 1f / MathF.Max(halfHeight * halfHeight, 1e-12f);

        for (var j = 0; j <= rings; j++)
        {
            var v = (float)j / rings;
            var phi = v * maxPhi;
            var y = MathF.Cos(phi) * halfHeight;
            var ringRadius = MathF.Sin(phi) * Radius;
            for (var i = 0; i <= radial; i++)
            {
                var u = (float)i / radial;
                var theta = u * MathF.Tau;
                var p = new Vector3(MathF.Sin(theta) * ringRadius, y, MathF.Cos(theta) * ringRadius);
                var n = new Vector3(p.X * invR2, p.Y * invH2, p.Z * invR2);
                n = n.LengthSquared() > 1e-20f ? Vector3.Normalize(n) : (j == 0 ? Vector3.UnitY : -Vector3.UnitY);
                b.AddVertex(p, n, new Vector2(u, v));
            }
        }

        AddGrid(b, rings, radial);
    }

    // Rows of (columns + 1) vertices; quads between consecutive rows (pole quads degenerate into triangles).
    internal static void AddGrid(MeshBuilder b, int rows, int columns, int firstVertex = 0)
    {
        var stride = columns + 1;
        for (var j = 0; j < rows; j++)
            for (var i = 0; i < columns; i++)
            {
                var a = firstVertex + j * stride + i;
                var c = a + stride + 1;
                // Split along the diagonal that avoids a zero-area triangle at a pole.
                if (b.IsDegenerate(a, a + 1))
                    b.AddTriangle(a, a + stride, c);
                else if (b.IsDegenerate(a + stride, c))
                    b.AddTriangle(a, a + stride, a + 1);
                else
                    b.AddQuad(a, a + stride, c, a + 1);
            }
    }
}

/// <summary>A cylinder or truncated cone along Y (Godot's <c>CylinderMesh</c>), with optional caps.</summary>
public sealed class CylinderMesh : PrimitiveMesh
{
    [Export]
    public float TopRadius
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Rebuild();
        }
    } = 0.5f;

    [Export]
    public float BottomRadius
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Rebuild();
        }
    } = 0.5f;

    [Export]
    public float Height
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Rebuild();
        }
    } = 2f;

    [Export(Range = "3,256,1")]
    public int RadialSegments
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Rebuild();
        }
    } = 64;

    /// <summary>Extra subdivisions along the height.</summary>
    [Export(Range = "0,256,1")]
    public int Rings
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Rebuild();
        }
    } = 4;

    [Export]
    public bool CapTop
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Rebuild();
        }
    } = true;

    [Export]
    public bool CapBottom
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Rebuild();
        }
    } = true;

    private protected override void Generate(MeshBuilder b)
    {
        var radial = Clamp(RadialSegments, 3);
        var rows = Clamp(Rings, 0) + 1;
        var halfHeight = Height * 0.5f;
        // Side normal of a cone: (direction · height, bottom − top), normalised.
        var slope = Height > 0 ? (BottomRadius - TopRadius) / Height : 0f;

        for (var j = 0; j <= rows; j++)
        {
            var v = (float)j / rows;
            var y = halfHeight - v * Height;
            var radius = TopRadius + (BottomRadius - TopRadius) * v;
            for (var i = 0; i <= radial; i++)
            {
                var u = (float)i / radial;
                var theta = u * MathF.Tau;
                var dir = new Vector3(MathF.Sin(theta), 0f, MathF.Cos(theta));
                var n = Vector3.Normalize(new Vector3(dir.X, slope, dir.Z));
                b.AddVertex(dir * radius + new Vector3(0, y, 0), n, new Vector2(u, v));
            }
        }

        SphereMesh.AddGrid(b, rows, radial);

        if (CapTop && TopRadius > 0f)
            AddCap(b, halfHeight, TopRadius, Vector3.UnitY, radial);
        if (CapBottom && BottomRadius > 0f)
            AddCap(b, -halfHeight, BottomRadius, -Vector3.UnitY, radial);
    }

    internal static void AddCap(MeshBuilder b, float y, float radius, Vector3 normal, int radial)
    {
        var center = b.AddVertex(new Vector3(0, y, 0), normal, new Vector2(0.5f, 0.5f));
        var first = b.VertexCount;
        for (var i = 0; i <= radial; i++)
        {
            var theta = (float)i / radial * MathF.Tau;
            var s = MathF.Sin(theta);
            var c = MathF.Cos(theta);
            b.AddVertex(new Vector3(s * radius, y, c * radius), normal, new Vector2(0.5f + 0.5f * s, 0.5f - 0.5f * c * MathF.Sign(normal.Y)));
        }

        for (var i = 0; i < radial; i++)
            b.AddTriangle(center, first + i, first + i + 1);
    }
}

/// <summary>A capsule along Y (Godot's <c>CapsuleMesh</c>): <see cref="Height"/> includes both hemispheres.</summary>
public sealed class CapsuleMesh : PrimitiveMesh
{
    [Export]
    public float Radius
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Rebuild();
        }
    } = 0.5f;

    /// <summary>Total height, caps included (at least 2 × <see cref="Radius"/>).</summary>
    [Export]
    public float Height
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Rebuild();
        }
    } = 2f;

    [Export(Range = "4,256,1")]
    public int RadialSegments
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Rebuild();
        }
    } = 64;

    /// <summary>Latitude rings per hemisphere.</summary>
    [Export(Range = "1,256,1")]
    public int Rings
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Rebuild();
        }
    } = 8;

    private protected override void Generate(MeshBuilder b)
    {
        var radial = Clamp(RadialSegments, 3);
        var rings = Clamp(Rings, 1);
        var height = MathF.Max(Height, 2f * Radius);
        var middle = height - 2f * Radius;

        // Top hemisphere rows (pole → equator, raised by middle / 2), then bottom hemisphere rows (equator → pole):
        // the step between the two equator rows is the cylindrical middle.
        for (var hemisphere = 0; hemisphere < 2; hemisphere++)
        {
            var offset = hemisphere == 0 ? middle * 0.5f : -middle * 0.5f;
            for (var j = 0; j <= rings; j++)
            {
                var phi = (hemisphere + (float)j / rings) * MathF.PI * 0.5f;
                var sinPhi = MathF.Sin(phi);
                var cosPhi = MathF.Cos(phi);
                var y = offset + cosPhi * Radius;
                for (var i = 0; i <= radial; i++)
                {
                    var u = (float)i / radial;
                    var theta = u * MathF.Tau;
                    var n = new Vector3(MathF.Sin(theta) * sinPhi, cosPhi, MathF.Cos(theta) * sinPhi);
                    var v = (height * 0.5f - y) / height;
                    b.AddVertex(n * Radius + new Vector3(0, offset, 0), Vector3.Normalize(n), new Vector2(u, v));
                }
            }
        }

        SphereMesh.AddGrid(b, 2 * (rings + 1) - 1, radial);
    }
}

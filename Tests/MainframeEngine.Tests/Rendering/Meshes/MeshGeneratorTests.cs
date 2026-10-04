using System.Numerics;

namespace MainframeEngine.Tests.Rendering.Meshes;

/// <summary>The primitive mesh generators: counts, normals, winding, bounds, flipping, regeneration.</summary>
public sealed class MeshGeneratorTests
{
    private const float Epsilon = 1e-4f;

    public static TheoryData<string> Primitives => ["box", "plane", "quad", "sphere", "hemisphere", "cylinder", "cone", "capsule"];

    private static PrimitiveMesh Create(string name) => name switch
    {
        "box" => new BoxMesh(),
        "plane" => new PlaneMesh { SubdivideWidth = 2, SubdivideDepth = 3 },
        "quad" => new QuadMesh(),
        "sphere" => new SphereMesh { RadialSegments = 16, Rings = 8 },
        "hemisphere" => new SphereMesh { RadialSegments = 16, Rings = 8, IsHemisphere = true },
        "cylinder" => new CylinderMesh { RadialSegments = 12, Rings = 2 },
        "cone" => new CylinderMesh { TopRadius = 0f, RadialSegments = 12 },
        "capsule" => new CapsuleMesh { RadialSegments = 12, Rings = 4 },
        _ => throw new ArgumentException(name),
    };

    [Theory]
    [MemberData(nameof(Primitives))]
    public void SurfacesAreValidWithUnitNormalsAndUVs(string name)
    {
        var mesh = Create(name);
        Assert.Equal(1, mesh.SurfaceCount);
        var surface = mesh.GetSurface(0);
        surface.Validate();
        Assert.True(surface.IndexCount > 0);
        Assert.Equal(surface.VertexCount, surface.Normals.Length);
        Assert.Equal(surface.VertexCount, surface.UVs.Length);
        Assert.All(surface.Normals, n => Assert.Equal(1f, n.Length(), 3));
        Assert.All(surface.UVs, uv => Assert.True(uv.X is >= -Epsilon and <= 1 + Epsilon && uv.Y is >= -Epsilon and <= 1 + Epsilon, $"UV {uv}"));
    }

    [Theory]
    [MemberData(nameof(Primitives))]
    public void TrianglesWindCounterClockwiseAroundTheirNormals(string name)
    {
        var surface = Create(name).GetSurface(0);
        var p = surface.Positions;
        var n = surface.Normals;
        var i = surface.Indices;
        var checkedTriangles = 0;
        for (var t = 0; t < i.Length; t += 3)
        {
            var face = Vector3.Cross(p[i[t + 1]] - p[i[t]], p[i[t + 2]] - p[i[t]]);
            if (face.LengthSquared() < 1e-12f)
                continue; // degenerate at a pole
            var normal = n[i[t]] + n[i[t + 1]] + n[i[t + 2]];
            Assert.True(Vector3.Dot(face, normal) > 0f, $"triangle {t / 3} winds against its normals");
            checkedTriangles++;
        }

        Assert.True(checkedTriangles > 0);
    }

    [Theory]
    [InlineData("box")]
    [InlineData("sphere")]
    [InlineData("cylinder")]
    [InlineData("capsule")]
    public void ClosedShapesHaveOutwardNormals(string name)
    {
        var surface = Create(name).GetSurface(0);
        for (var v = 0; v < surface.VertexCount; v++)
        {
            var p = surface.Positions[v];
            var n = surface.Normals[v];
            // Caps of the cylinder: the normal is ±Y; elsewhere the radial direction (or the centre direction).
            var outward = Math.Abs(n.Y) > 0.999f ? new Vector3(0, p.Y, 0) : p;
            Assert.True(Vector3.Dot(n, outward) >= -Epsilon, $"vertex {v} at {p} has an inward normal {n}");
        }
    }

    [Fact]
    public void VertexAndIndexCountsMatchTheParameters()
    {
        var box = new BoxMesh().GetSurface(0);
        Assert.Equal(24, box.VertexCount);
        Assert.Equal(36, box.IndexCount);

        var plane = new PlaneMesh { SubdivideWidth = 2, SubdivideDepth = 3 }.GetSurface(0);
        Assert.Equal(4 * 5, plane.VertexCount); // (2 + 2) columns × (3 + 2) rows of vertices
        Assert.Equal(3 * 4 * 6, plane.IndexCount);

        var quad = new QuadMesh().GetSurface(0);
        Assert.Equal(4, quad.VertexCount);
        Assert.Equal(6, quad.IndexCount);

        var sphere = new SphereMesh { RadialSegments = 16, Rings = 8 }.GetSurface(0);
        Assert.Equal(9 * 17, sphere.VertexCount);
        Assert.Equal((8 - 2) * 16 * 6 + 2 * 16 * 3, sphere.IndexCount); // pole rows are single triangles

        var cylinder = new CylinderMesh { RadialSegments = 12, Rings = 2 }.GetSurface(0);
        Assert.Equal(4 * 13 + 2 * 14, cylinder.VertexCount); // side rows + two caps (centre + ring)
        Assert.Equal(3 * 12 * 6 + 2 * 12 * 3, cylinder.IndexCount);

        var capsule = new CapsuleMesh { RadialSegments = 12, Rings = 4 }.GetSurface(0);
        Assert.Equal(2 * 5 * 13, capsule.VertexCount);
    }

    [Fact]
    public void BoundsMatchTheSizes()
    {
        AssertBounds(new BoxMesh { Size = new Vector3(2, 3, 4) }, new Vector3(-1, -1.5f, -2), new Vector3(1, 1.5f, 2));
        AssertBounds(new PlaneMesh { Size = new Vector2(10, 6) }, new Vector3(-5, 0, -3), new Vector3(5, 0, 3));
        AssertBounds(new QuadMesh { Size = new Vector2(2, 1) }, new Vector3(-1, -0.5f, 0), new Vector3(1, 0.5f, 0));
        AssertBounds(new SphereMesh { Radius = 2, Height = 4 }, new Vector3(-2), new Vector3(2));
        AssertBounds(new SphereMesh { Radius = 1, Height = 1 }, new Vector3(-1, -0.5f, -1), new Vector3(1, 0.5f, 1)); // ellipsoid
        AssertBounds(new CylinderMesh { TopRadius = 0.5f, BottomRadius = 1, Height = 3 }, new Vector3(-1, -1.5f, -1), new Vector3(1, 1.5f, 1));
        AssertBounds(new CapsuleMesh { Radius = 0.5f, Height = 3 }, new Vector3(-0.5f, -1.5f, -0.5f), new Vector3(0.5f, 1.5f, 0.5f));
    }

    private static void AssertBounds(Mesh mesh, Vector3 min, Vector3 max)
    {
        var bounds = mesh.Bounds;
        Assert.True(Vector3.Distance(min, bounds.Min) < 1e-3f, $"{mesh.GetType().Name}: min {bounds.Min}, expected {min}");
        Assert.True(Vector3.Distance(max, bounds.Max) < 1e-3f, $"{mesh.GetType().Name}: max {bounds.Max}, expected {max}");
    }

    [Fact]
    public void PlaneFacesUpAndQuadFacesPlusZ()
    {
        Assert.All(new PlaneMesh().GetSurface(0).Normals, n => Assert.Equal(Vector3.UnitY, n));
        Assert.All(new QuadMesh().GetSurface(0).Normals, n => Assert.Equal(Vector3.UnitZ, n));
    }

    [Fact]
    public void FlipFacesReversesWindingAndNormals()
    {
        var normal = new QuadMesh().GetSurface(0);
        var flipped = new QuadMesh { FlipFaces = true }.GetSurface(0);
        Assert.All(flipped.Normals, n => Assert.Equal(-Vector3.UnitZ, n));
        Assert.Equal(normal.Indices[0], flipped.Indices[0]);
        Assert.Equal(normal.Indices[1], flipped.Indices[2]);
        Assert.Equal(normal.Indices[2], flipped.Indices[1]);
    }

    [Fact]
    public void ChangingAParameterRegeneratesAndBumpsTheVersion()
    {
        var box = new BoxMesh();
        var first = box.GetSurface(0);
        var version = box.Version;
        var changes = 0;
        box.Changed += () => changes++;

        box.Size = new Vector3(2);
        Assert.NotEqual(version, box.Version);
        Assert.NotSame(first, box.GetSurface(0));
        Assert.Equal(new Vector3(1), box.Bounds.Max);
        Assert.Equal(1, changes);

        version = box.Version;
        box.Size = new Vector3(2); // same value: nothing changes
        Assert.Equal(version, box.Version);
    }

    [Fact]
    public void ArrayMeshTracksSurfaceEdits()
    {
        var mesh = new ArrayMesh();
        Assert.Equal(0, mesh.SurfaceCount);
        Assert.True(mesh.Bounds.IsEmpty);

        var surface = new BoxMesh().GetSurface(0);
        mesh.AddSurface(new MeshSurface(surface.Positions, surface.Normals, surface.UVs, surface.Indices));
        var version = mesh.Version;
        Assert.Equal(new Vector3(0.5f), mesh.Bounds.Max);

        mesh.GetSurface(0).Positions = [.. surface.Positions.Select(p => p * 2)];
        Assert.NotEqual(version, mesh.Version);
        Assert.Equal(new Vector3(1f), mesh.Bounds.Max);

        version = mesh.Version;
        Assert.Equal(version, mesh.Version); // stable while nothing changes
    }

    [Fact]
    public void SurfaceValidationReportsBadData()
    {
        Assert.Throws<InvalidDataException>(() => new MeshSurface([Vector3.Zero], [], [], [0, 0]).Validate());
        Assert.Throws<InvalidDataException>(() => new MeshSurface([Vector3.Zero], [], [], [0, 0, 1]).Validate());
        Assert.Throws<InvalidDataException>(() => new MeshSurface([Vector3.Zero], [Vector3.UnitY, Vector3.UnitY], [], [0, 0, 0]).Validate());
        new MeshSurface([Vector3.Zero, Vector3.UnitX, Vector3.UnitY], [], [], [0, 1, 2]).Validate();
    }

    [Fact]
    public void MissingNormalsAreGeneratedSmooth()
    {
        // A triangle in the XY plane, counter-clockwise from +Z.
        var surface = new MeshSurface([Vector3.Zero, Vector3.UnitX, Vector3.UnitY], [], [], [0, 1, 2]);
        var vertices = new MeshVertex[3];
        surface.WriteVertices(vertices);
        Assert.All(vertices, v => Assert.Equal(Vector3.UnitZ, v.Normal));
        Assert.All(vertices, v => Assert.Equal(Vector2.Zero, v.UV));
        Assert.Equal(Vector3.UnitX, vertices[1].Position);
    }

    [Fact]
    public void VertexLayoutMatchesTheShaderInputs()
    {
        Assert.Equal(MeshVertex.Size, System.Runtime.InteropServices.Marshal.SizeOf<MeshVertex>());
        Assert.Equal(MeshInstanceData.Size, System.Runtime.InteropServices.Marshal.SizeOf<MeshInstanceData>());
        Assert.Equal(0u, VertexLayouts.MeshInstancedAttributes[0].Offset); // position first: shadow pipelines read it alone
        Assert.Equal((uint)MeshVertex.Size, VertexLayouts.MeshInstancedBindings[0].Stride);
    }
}

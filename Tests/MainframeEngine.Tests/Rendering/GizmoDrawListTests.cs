using System.Numerics;

namespace MainframeEngine.Tests.Rendering;

public sealed class GizmoDrawListTests
{
    private static readonly Vector4 Red = new(1f, 0f, 0f, 1f);

    private static GizmoDrawList Looking(Vector3 eye, Vector3 at)
    {
        var list = new GizmoDrawList();
        list.SetCamera(eye, at - eye, Vector3.UnitY, orthographic: false);
        return list;
    }

    [Fact]
    public void ALineIsOneQuadCarryingItsWidthAndBothEnds()
    {
        var list = new GizmoDrawList();
        list.Line(Vector3.Zero, Vector3.UnitX, Red, 3f);
        Assert.Equal(6, list.VertexCount);
        var vertices = list.Vertices;
        foreach (var v in vertices)
        {
            Assert.Equal(3f, v.Line.Y);
            Assert.Equal(1f, MathF.Abs(v.Line.X));
            Assert.True(v.Position == Vector3.Zero ? v.Other == Vector3.UnitX : v.Position == Vector3.UnitX && v.Other == Vector3.Zero);
        }

        // Both sides at both ends.
        Assert.Contains(vertices.ToArray(), v => v.Position == Vector3.Zero && v.Line.X > 0);
        Assert.Contains(vertices.ToArray(), v => v.Position == Vector3.Zero && v.Line.X < 0);
        Assert.Contains(vertices.ToArray(), v => v.Position == Vector3.UnitX && v.Line.X > 0);
        Assert.Contains(vertices.ToArray(), v => v.Position == Vector3.UnitX && v.Line.X < 0);

        list.Clear();
        Assert.Equal(0, list.VertexCount);
    }

    [Fact]
    public void SolidsKeepOnlyTheFacesTowardTheCameraAndAreShaded()
    {
        // A box seen from a corner shows three faces (6 triangles) and six silhouette edges; from the front, one face and
        // its four edges.
        var corner = Looking(new Vector3(5, 5, 5), Vector3.Zero);
        corner.Box(Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, Red);
        Assert.Equal(6 * 3 + 6 * 6, corner.VertexCount);
        var front = Looking(new Vector3(0, 0, 5), Vector3.Zero);
        front.Box(Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, Red);
        Assert.Equal(2 * 3 + 4 * 6, front.VertexCount);

        // Shading: the visible faces of the corner view differ in brightness, none brighter than the colour.
        var shades = corner.Vertices.ToArray().Where(v => v.Line.Y == 0).Select(v => v.Color.X).Distinct().ToArray();
        Assert.Equal(3, shades.Length);
        Assert.All(shades, s => Assert.InRange(s, 0.4f, 1f));

        // A cone pointing at the camera shows its sides, pointing away shows its base and fewer sides.
        var toward = Looking(new Vector3(0, 0, 5), Vector3.Zero);
        toward.Cone(new Vector3(0, 0, 1), Vector3.UnitZ, 1f, 0.3f, Red, segments: 16);
        var away = Looking(new Vector3(0, 0, 5), Vector3.Zero);
        away.Cone(new Vector3(0, 0, -1), -Vector3.UnitZ, 1f, 0.3f, Red, segments: 16);
        Assert.Equal(16 * 3 + 16 * 6, toward.VertexCount); // all sides and the rim (the silhouette); no base
        Assert.True(away.VertexCount >= 16 * 3 && away.Vertices.ToArray().Any(v => v.Line.Y > 0), "the base and its rim");
    }

    [Fact]
    public void IconsUseTheirAtlasRectangle()
    {
        using var atlas = new GizmoIconAtlas(64, 64);
        var pixels = new byte[8 * 8 * 4];
        pixels.AsSpan().Fill(200);
        var icon = atlas.Add(pixels, 8, 8);
        var list = new GizmoDrawList { Atlas = atlas };
        list.Icon(icon, Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector4.One);
        var (min, max) = atlas.UvRect(icon);
        Assert.Equal(6, list.VertexCount);
        Assert.Contains(list.Vertices.ToArray(), v => v.Uv == min);
        Assert.Contains(list.Vertices.ToArray(), v => v.Uv == max);
        list.Line(Vector3.Zero, Vector3.One, Red, 2f);
        Assert.Equal(atlas.WhiteUv, list.Vertices[^1].Uv);
    }

    [Fact]
    public void AFullListDropsShapes()
    {
        var list = new GizmoDrawList();
        for (var i = 0; i < GizmoDrawList.MaxVertices / 6; i++)
            list.Line(Vector3.Zero, Vector3.One, Red, 1f);
        list.Line(Vector3.Zero, Vector3.One, Red, 1f);
        Assert.Equal(GizmoDrawList.MaxVertices / 6 * 6, list.VertexCount);
        Assert.Equal(6, list.DroppedVertices);
        list.Clear();
        Assert.Equal(0, list.DroppedVertices);
    }

    [Fact]
    public void RefillingTheListAllocatesNothing()
    {
        var list = Looking(new Vector3(3, 4, 5), Vector3.Zero);
        void Frame()
        {
            list.Clear();
            for (var i = 0; i < 3; i++)
            {
                list.Line(Vector3.Zero, Vector3.UnitX * i, Red, 3f);
                list.Cone(Vector3.UnitY * i, Vector3.UnitY, 0.3f, 0.1f, Red);
                list.Box(Vector3.UnitZ * i, Vector3.UnitX * 0.1f, Vector3.UnitY * 0.1f, Vector3.UnitZ * 0.1f, Red);
            }

            list.Circle(Vector3.Zero, Vector3.UnitX, Vector3.UnitZ, 1f, Red, 2f);
            list.Disc(Vector3.Zero, Vector3.UnitX, Vector3.UnitY, 0.2f, Red);
        }

        Frame();
        Assert.Equal(0, AllocationGate.SmallestWindow(() =>
        {
            for (var i = 0; i < 50; i++)
                Frame();
        }));
    }

    [Fact]
    public void TheAtlasPacksIconsAndRejectsOverflow()
    {
        using var atlas = new GizmoIconAtlas(24, 28);
        var version = atlas.Version;
        var image = new byte[10 * 10 * 4];
        var a = atlas.Add(image, 10, 10);
        var b = atlas.Add(image, 10, 10);
        Assert.Equal((0, 1), (a, b));
        Assert.True(atlas.Version > version);
        var (minA, maxA) = atlas.UvRect(a);
        var (minB, _) = atlas.UvRect(b);
        Assert.True(minB.X >= maxA.X || minB.Y >= maxA.Y, "icons overlap");
        Assert.Throws<InvalidOperationException>(() => atlas.Add(image, 10, 10));
        Assert.Throws<InvalidOperationException>(() => atlas.Add(new byte[40 * 40 * 4], 40, 40));

        // The white block is white.
        var white = atlas.WhiteUv;
        var (x, y) = ((int)(white.X * atlas.Width), (int)(white.Y * atlas.Height));
        Assert.Equal(255, atlas.Pixels[(y * atlas.Width + x) * 4]);
        atlas.Clear();
        Assert.Equal(0, atlas.Count);
    }
}

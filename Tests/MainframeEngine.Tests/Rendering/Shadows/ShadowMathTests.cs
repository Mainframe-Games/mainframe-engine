using System.Numerics;

namespace MainframeEngine.Tests.Rendering;

/// <summary>Cascade splits, the bounding-sphere fit, texel snapping, light matrices and the receiver bias math.</summary>
public sealed class ShadowMathTests
{
    private static readonly Vector3 SunDirection = Vector3.Normalize(new Vector3(-0.4f, -1f, -0.6f));

    private static Matrix4x4 Perspective(float fovDegrees = 60f, float aspect = 16f / 9f, float near = 0.1f, float far = 1000f) =>
        Matrix4x4.CreatePerspectiveFieldOfView(float.DegreesToRadians(fovDegrees), aspect, near, far);

    // ── Splits ───────────────────────────────────────────────────────────────

    [Fact]
    public void LambdaZeroSplitsUniformly()
    {
        Span<float> splits = stackalloc float[4];
        ShadowMath.ComputeSplits(1f, 101f, 0f, splits);
        Assert.Equal([26f, 51f, 76f, 101f], splits.ToArray(), (a, b) => MathF.Abs(a - b) < 1e-3f);
    }

    [Fact]
    public void LambdaOneSplitsLogarithmically()
    {
        Span<float> splits = stackalloc float[4];
        ShadowMath.ComputeSplits(1f, 10000f, 1f, splits);
        Assert.Equal([10f, 100f, 1000f, 10000f], splits.ToArray(), (a, b) => MathF.Abs(a - b) / b < 1e-4f);
    }

    [Fact]
    public void PracticalSplitsBlendTheTwoSchemesAndEndAtFar()
    {
        Span<float> practical = stackalloc float[4];
        Span<float> uniform = stackalloc float[4];
        Span<float> log = stackalloc float[4];
        ShadowMath.ComputeSplits(0.1f, 100f, 0.75f, practical);
        ShadowMath.ComputeSplits(0.1f, 100f, 0f, uniform);
        ShadowMath.ComputeSplits(0.1f, 100f, 1f, log);

        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(0.75f * log[i] + 0.25f * uniform[i], practical[i], 3);
            if (i > 0)
                Assert.True(practical[i] > practical[i - 1]);
        }

        Assert.Equal(100f, practical[3]);
        Assert.True(practical[0] < 10f, "the first cascade stays close to the camera");
    }

    [Fact]
    public void InvalidSplitRangesThrow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ShadowMath.ComputeSplits(0f, 10f, 0.5f, new float[4]));
        Assert.Throws<ArgumentOutOfRangeException>(() => ShadowMath.ComputeSplits(5f, 5f, 0.5f, new float[4]));
    }

    // ── Sphere fit ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0.1f, 6f)]
    [InlineData(6f, 15f)]
    [InlineData(15f, 40f)]
    [InlineData(40f, 100f)]
    public void SliceSphereContainsTheWholeSlice(float near, float far)
    {
        var projection = Perspective();
        var (center, radius) = ShadowMath.SliceSphere(projection, near, far);

        Matrix4x4.Invert(projection, out var inverse);
        foreach (var corner in SliceCorners(inverse, near, far))
            Assert.True(Vector3.Distance(center, corner) <= radius + 1e-3f, $"corner {corner} is outside the sphere");

        // Symmetric frustum: the centre is on the view axis, between the slice planes.
        Assert.True(MathF.Abs(center.X) < 1e-3f && MathF.Abs(center.Y) < 1e-3f);
        Assert.InRange(-center.Z, near, far);
        Assert.Equal(0f, radius % ShadowMath.RadiusQuantum, 4);
    }

    [Theory]
    [InlineData(0.1f, 6f)]
    [InlineData(6f, 15f)]
    [InlineData(40f, 100f)]
    public void SliceSphereIsTheSmallestSphereAroundTheSlice(float near, float far)
    {
        // Symmetric frustum: centre on the axis at depth c = (n + f)(1 + k) / 2 (clamped to the far plane), where k is
        // the squared half-diagonal of the rectangle at depth 1; the radius reaches both the near and far corners.
        const float fov = 60f, aspect = 16f / 9f;
        var ty = MathF.Tan(float.DegreesToRadians(fov / 2f));
        var k = ty * ty * (1f + aspect * aspect);
        var c = MathF.Min((near + far) * (1f + k) / 2f, far);
        var optimal = MathF.Max(MathF.Sqrt((c - near) * (c - near) + near * near * k), MathF.Sqrt((far - c) * (far - c) + far * far * k));

        var (center, radius) = ShadowMath.SliceSphere(Perspective(fov, aspect), near, far);
        Assert.InRange(radius, optimal - 1e-3f, optimal + ShadowMath.RadiusQuantum + 1e-3f);
        Assert.Equal(-c, center.Z, 2);
    }

    [Fact]
    public void SliceSphereWorksForOrthographicProjections()
    {
        var projection = Matrix4x4.CreateOrthographic(20f, 10f, 0.1f, 100f);
        var (center, radius) = ShadowMath.SliceSphere(projection, 10f, 30f);
        Matrix4x4.Invert(projection, out var inverse);
        foreach (var corner in SliceCorners(inverse, 10f, 30f))
            Assert.True(Vector3.Distance(center, corner) <= radius + 1e-3f);
        Assert.Equal(-20f, center.Z, 3);
    }

    [Fact]
    public void CascadeSizeDoesNotDependOnCameraOrientation()
    {
        // The sphere is computed in view space: rotating or moving the camera moves it rigidly, never resizes it.
        var projection = Perspective();
        var (viewCenter, radius) = ShadowMath.SliceSphere(projection, 6f, 15f);

        var a = Matrix4x4.CreateLookAt(new Vector3(0, 2, 5), Vector3.Zero, Vector3.UnitY);
        var b = Matrix4x4.CreateLookAt(new Vector3(30, 7, -3), new Vector3(-4, 1, 9), Vector3.UnitY);
        Matrix4x4.Invert(a, out var ia);
        Matrix4x4.Invert(b, out var ib);
        var ca = Vector3.Transform(viewCenter, ia);
        var cb = Vector3.Transform(viewCenter, ib);
        Assert.Equal(Vector3.Distance(new Vector3(0, 2, 5), ca), Vector3.Distance(new Vector3(30, 7, -3), cb), 3);
        Assert.Equal(radius, ShadowMath.SliceSphere(projection, 6f, 15f).Radius);
    }

    private static Vector3[] SliceCorners(in Matrix4x4 inverseProjection, float near, float far)
    {
        var corners = new Vector3[8];
        for (var i = 0; i < 8; i++)
        {
            var x = (i & 1) == 0 ? -1f : 1f;
            var y = (i & 2) == 0 ? -1f : 1f;
            var n = Unproject(inverseProjection, new Vector3(x, y, 0f));
            var f = Unproject(inverseProjection, new Vector3(x, y, 1f));
            var depth = i < 4 ? near : far;
            var t = (depth + n.Z) / (n.Z - f.Z);
            corners[i] = Vector3.Lerp(n, f, t);
        }

        // Order so corners[0] and corners[7] are diagonally opposite (near bottom-left, far top-right).
        return corners;
    }

    private static Vector3 Unproject(in Matrix4x4 inverse, Vector3 ndc)
    {
        var v = Vector4.Transform(new Vector4(ndc, 1f), inverse);
        return new Vector3(v.X, v.Y, v.Z) / v.W;
    }

    // ── Sphere light matrix and texel snapping ───────────────────────────────

    [Fact]
    public void SphereMatrixMapsTheSphereIntoTheMap()
    {
        var rotation = ShadowMath.LightRotation(SunDirection);
        var center = new Vector3(3, 1, -2);
        var matrix = ShadowMath.SphereLightMatrix(rotation, center, 5f, 1024, 10f, snap: true, out var texel);

        Assert.Equal(2f * (5f * 1024f / 1022f) / 1024f, texel, 6); // one texel of margin around the sphere
        // Points on the sphere (± one texel of snapping) land inside the map, depth in [0, 1].
        foreach (var dir in new[] { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ, SunDirection, -SunDirection })
        {
            var clip = Vector4.Transform(new Vector4(center + dir * (5f - 2f * texel), 1f), matrix);
            Assert.InRange(clip.X, -1f, 1f);
            Assert.InRange(clip.Y, -1f, 1f);
            Assert.InRange(clip.Z, 0f, 1f);
        }

        // Nearer the light = smaller depth; a caster up to the pull-back in front of the sphere is still in range.
        var front = Vector4.Transform(new Vector4(center - SunDirection * (5f + 9f), 1f), matrix);
        var back = Vector4.Transform(new Vector4(center + SunDirection * 4f, 1f), matrix);
        Assert.InRange(front.Z, 0f, 0.1f);
        Assert.True(front.Z < back.Z);
    }

    [Fact]
    public void SubTexelCameraMovesLeaveTheSnappedMatrixUnchanged()
    {
        var rotation = ShadowMath.LightRotation(SunDirection);
        Matrix4x4.Invert(rotation, out var inverseRotation);
        const float radius = 8f;
        const int resolution = 2048;
        var texel = 2f * (radius * resolution / (resolution - 2f)) / resolution;

        // A sphere centre in the middle of a texel cell (light space), then moves of up to 0.4 texel each way.
        var cell = new Vector3(123.5f, -77.5f, 31.5f) * texel;
        var start = Vector3.Transform(cell, inverseRotation);
        var reference = ShadowMath.SphereLightMatrix(rotation, start, radius, resolution, 10f, snap: true, out _);
        foreach (var offset in new[] { new Vector3(0.4f, 0, 0), new Vector3(-0.4f, 0.3f, 0), new Vector3(0.2f, -0.35f, 0.4f), new Vector3(0, 0, -0.4f) })
        {
            var moved = Vector3.Transform(cell + offset * texel, inverseRotation);
            var matrix = ShadowMath.SphereLightMatrix(rotation, moved, radius, resolution, 10f, snap: true, out _);
            Assert.Equal(reference, matrix);
        }
    }

    [Fact]
    public void LargerMovesShiftTheMapByWholeTexels()
    {
        // Wherever the camera goes, a world point keeps the same position within its texel: edges never crawl.
        var rotation = ShadowMath.LightRotation(SunDirection);
        const float radius = 8f;
        const int resolution = 2048;
        var point = new Vector3(1.234f, 0.5f, -2.345f);
        var first = TexelCoordinate(ShadowMath.SphereLightMatrix(rotation, new Vector3(0.1f, 0, 0.2f), radius, resolution, 10f, true, out _), point, resolution);

        for (var i = 1; i <= 20; i++)
        {
            var center = new Vector3(0.1f + i * 0.0137f, 0.002f * i, 0.2f - i * 0.0091f);
            var texelCoordinate = TexelCoordinate(ShadowMath.SphereLightMatrix(rotation, center, radius, resolution, 10f, true, out _), point, resolution);
            var delta = texelCoordinate - first;
            Assert.Equal(MathF.Round(delta.X), delta.X, 2);
            Assert.Equal(MathF.Round(delta.Y), delta.Y, 2);
        }
    }

    [Fact]
    public void WithoutSnappingTheMapSlidesWithTheCamera()
    {
        var rotation = ShadowMath.LightRotation(SunDirection);
        var point = new Vector3(1.234f, 0.5f, -2.345f);
        var a = TexelCoordinate(ShadowMath.SphereLightMatrix(rotation, Vector3.Zero, 8f, 2048, 10f, false, out _), point, 2048);
        var b = TexelCoordinate(ShadowMath.SphereLightMatrix(rotation, new Vector3(0.003f, 0, 0.002f), 8f, 2048, 10f, false, out _), point, 2048);
        var delta = b - a;
        Assert.True(MathF.Abs(delta.X - MathF.Round(delta.X)) > 0.05f || MathF.Abs(delta.Y - MathF.Round(delta.Y)) > 0.05f,
            "an unsnapped map moves by fractions of a texel");
    }

    private static Vector2 TexelCoordinate(in Matrix4x4 matrix, Vector3 world, int resolution)
    {
        var clip = Vector4.Transform(new Vector4(world, 1f), matrix);
        return (new Vector2(clip.X, clip.Y) / clip.W * 0.5f + new Vector2(0.5f)) * resolution;
    }

    [Fact]
    public void SnappingNeverUncoversTheSphere()
    {
        // Wherever the centre falls in its texel, every point of the sphere (and the pull-back in front) stays inside.
        var rotation = ShadowMath.LightRotation(SunDirection);
        Matrix4x4.Invert(rotation, out var inverse);
        var random = new Random(7);
        for (var i = 0; i < 200; i++)
        {
            var center = new Vector3(random.NextSingle() * 40f - 20f, random.NextSingle() * 10f, random.NextSingle() * 40f - 20f);
            var matrix = ShadowMath.SphereLightMatrix(rotation, center, 6f, 256, 5f, snap: true, out _);
            var lightSpace = Vector3.Transform(center, rotation);
            foreach (var offset in new[] { new Vector3(6, 0, 0), new Vector3(-6, 0, 0), new Vector3(0, 6, 0), new Vector3(0, -6, 0), new Vector3(0, 0, -6), new Vector3(0, 0, 6 + 4.9f) })
            {
                var clip = Vector4.Transform(new Vector4(Vector3.Transform(lightSpace + offset, inverse), 1f), matrix);
                Assert.InRange(clip.X, -1f, 1f);
                Assert.InRange(clip.Y, -1f, 1f);
                Assert.InRange(clip.Z, 0f, 1f);
            }
        }
    }

    [Fact]
    public void SnapRoundsDownToTheGrid()
    {
        var snapped = ShadowMath.SnapToTexel(new Vector3(1.26f, -1.26f, 0.49f), 0.25f);
        Assert.Equal(new Vector3(1.25f, -1.5f, 0.25f), snapped);
    }

    [Fact]
    public void PullbackReachesEveryCasterInFrontOfTheSphere()
    {
        var rotation = ShadowMath.LightRotation(-Vector3.UnitY); // straight down: light space z = world... up
        var center = Vector3.Zero;
        var tower = new Aabb(new Vector3(-1, 0, -1), new Vector3(1, 40, 1));
        var pullback = ShadowMath.CasterPullback(rotation, tower, center, 5f, 2f, 1000f);
        Assert.Equal(35f, pullback); // the tower top is 40 above the centre, the sphere front 5

        Assert.Equal(2f, ShadowMath.CasterPullback(rotation, Aabb.Empty, center, 5f, 2f, 1000f));
        Assert.Equal(2f, ShadowMath.CasterPullback(rotation, new Aabb(new Vector3(-1, -10, -1), new Vector3(1, -9, 1)), center, 5f, 2f, 1000f));
        Assert.Equal(20f, ShadowMath.CasterPullback(rotation, tower, center, 5f, 2f, 20f));
    }

    // ── Spot, cube, texel sizes ──────────────────────────────────────────────

    [Fact]
    public void SpotMatrixCentresTheAxisAndCoversTheCone()
    {
        var position = new Vector3(2, 6, -1);
        var direction = Vector3.Normalize(new Vector3(0.3f, -1f, 0.2f));
        var matrix = ShadowMath.SpotLightMatrix(position, direction, 30f, 20f);

        var onAxis = Project(matrix, position + direction * 5f);
        Assert.Equal(0f, onAxis.X, 4);
        Assert.Equal(0f, onAxis.Y, 4);
        Assert.InRange(onAxis.Z, 0f, 1f);

        // A point on the outer cone sits on the map's edge (|ndc| = 1 along the axis it was tilted on).
        var up = ShadowMath.ChooseUp(direction);
        var side = Vector3.Normalize(Vector3.Cross(direction, up));
        var onCone = Project(matrix, position + (direction + side * MathF.Tan(float.DegreesToRadians(30f))) * 5f);
        Assert.Equal(1f, MathF.Abs(onCone.X), 3);
    }

    [Fact]
    public void CubeFacesLookAlongTheirAxes()
    {
        var position = new Vector3(1, 2, 3);
        for (var face = 0; face < 6; face++)
        {
            var (dir, _) = ShadowMath.CubeFace(face);
            var p = Project(ShadowMath.CubeFaceMatrix(position, face, 10f), position + dir * 4f);
            Assert.Equal(0f, p.X, 4);
            Assert.Equal(0f, p.Y, 4);
            Assert.InRange(p.Z, 0f, 1f);
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => ShadowMath.CubeFace(6));
    }

    [Fact]
    public void TexelSizesFollowTheProjection()
    {
        Assert.Equal(2f / 512f, ShadowMath.CubeTexelPerDistance(512), 6);
        Assert.Equal(2f * MathF.Tan(float.DegreesToRadians(30f)) / 1024f, ShadowMath.SpotTexelPerDistance(30f, 1024), 6);
        ShadowMath.SphereLightMatrix(ShadowMath.LightRotation(SunDirection), Vector3.Zero, 6f, 1536, 1f, true, out var texel);
        Assert.Equal(2f * (6f * 1536f / 1534f) / 1536f, texel, 6);
    }

    private static Vector3 Project(in Matrix4x4 matrix, Vector3 world)
    {
        var clip = Vector4.Transform(new Vector4(world, 1f), matrix);
        return new Vector3(clip.X, clip.Y, clip.Z) / clip.W;
    }

    // ── Receiver bias ────────────────────────────────────────────────────────

    [Fact]
    public void FacingTheLightTheReceiverMovesOnlyTowardsIt()
    {
        var p = ShadowMath.ReceiverPosition(Vector3.Zero, Vector3.UnitY, Vector3.UnitY, 0.01f, 0.5f, 1.5f);
        Assert.Equal(new Vector3(0, 0.005f, 0), p);
    }

    [Fact]
    public void AtGrazingAnglesTheNormalOffsetTakesOver()
    {
        var toLight = Vector3.Normalize(new Vector3(1f, 0.001f, 0f));
        var p = ShadowMath.ReceiverPosition(Vector3.Zero, Vector3.UnitY, toLight, 0.01f, 0f, 1.5f);
        Assert.Equal(0.015f, p.Y, 4); // ≈ normal bias × texel
        Assert.Equal(0f, p.X, 6);
    }

    [Fact]
    public void BackFacingReceiversGetTheFullNormalOffset()
    {
        // N·L < 0 (facing away): treated as grazing, so the offset never pulls into the surface.
        var p = ShadowMath.ReceiverPosition(Vector3.Zero, Vector3.UnitY, -Vector3.UnitY, 0.02f, 0f, 1f);
        Assert.Equal(0.02f, p.Y, 5);
    }
}

using System.Numerics;

namespace MainframeEngine.Tests.Rendering;

/// <summary>Conventions from docs/design/coordinate-conventions.md: right-handed, +Y up, camera looks
/// down −Z, row vectors (clip = v × View × Proj), Vulkan [0, 1] clip depth.</summary>
public class CameraTests
{
    private const float Epsilon = 1e-4f;

    private static Vector3 ToNdc(ICamera camera, Vector3 world, out float w)
    {
        var clip = Vector4.Transform(new Vector4(world, 1f), camera.ViewMatrix * camera.ProjectionMatrix);
        w = clip.W;
        return new Vector3(clip.X, clip.Y, clip.Z) / clip.W;
    }

    [Fact]
    public void Camera3DDefaultsLookDownNegativeZWithYUp()
    {
        var camera = new Camera3D();

        Assert.Equal(-Vector3.UnitZ, camera.Forward);
        Assert.Equal(Vector3.UnitY, camera.Up);
        Assert.Equal(45f, camera.FieldOfView);
        // Right-handed: forward × up points right (+X).
        Assert.Equal(Vector3.UnitX, Vector3.Cross(camera.Forward, camera.Up));
    }

    [Fact]
    public void Camera3DDepthMapsNearAndFarToVulkanZeroToOne()
    {
        var camera = new Camera3D { AspectRatio = 16f / 9f };

        var near = ToNdc(camera, new Vector3(0, 0, -0.1f), out _);
        var far = ToNdc(camera, new Vector3(0, 0, -1000f), out _);
        var mid = ToNdc(camera, new Vector3(0, 0, -10f), out _);

        Assert.Equal(0f, near.Z, Epsilon);
        Assert.Equal(1f, far.Z, Epsilon);
        Assert.InRange(mid.Z, 0f, 1f);
    }

    [Fact]
    public void Camera3DProjectsRightAndUpToPositiveNdc()
    {
        var camera = new Camera3D { AspectRatio = 1f };

        var right = ToNdc(camera, new Vector3(1, 0, -5), out var wRight);
        var up = ToNdc(camera, new Vector3(0, 1, -5), out _);

        Assert.True(wRight > 0, "points in front of the camera have positive w");
        Assert.True(right.X > 0);
        Assert.Equal(0f, right.Y, Epsilon);
        Assert.True(up.Y > 0, "+Y is up in NDC; the main pass flips the viewport, not the matrix");
    }

    [Fact]
    public void Camera3DPointsBehindTheCameraHaveNegativeW()
    {
        var camera = new Camera3D { AspectRatio = 1f };

        ToNdc(camera, new Vector3(0, 0, 5), out var w);

        Assert.True(w < 0);
    }

    [Fact]
    public void Camera3DFieldOfViewMatchesTheFrustumEdge()
    {
        var camera = new Camera3D { AspectRatio = 1f };
        var halfHeight = MathF.Tan(float.DegreesToRadians(45f) / 2f) * 10f;

        var edge = ToNdc(camera, new Vector3(0, halfHeight, -10f), out _);

        Assert.Equal(1f, edge.Y, Epsilon);
    }

    [Fact]
    public void LookAtPointsTheCameraAtTheTarget()
    {
        var camera = new Camera3D { Position = new Vector3(0, 5, 10), AspectRatio = 1f };

        camera.LookAt(Vector3.Zero);

        var expected = Vector3.Normalize(-camera.Position);
        Assert.Equal(expected.X, camera.Forward.X, Epsilon);
        Assert.Equal(expected.Y, camera.Forward.Y, Epsilon);
        Assert.Equal(expected.Z, camera.Forward.Z, Epsilon);

        // The target lands in the centre of the screen at its distance down −Z in view space.
        var view = Vector3.Transform(Vector3.Zero, camera.ViewMatrix);
        Assert.Equal(0f, view.X, Epsilon);
        Assert.Equal(0f, view.Y, Epsilon);
        Assert.Equal(-camera.Position.Length(), view.Z, Epsilon);
    }

    [Fact]
    public void LookAtItsOwnPositionIsIgnored()
    {
        var camera = new Camera3D { Position = new Vector3(1, 2, 3) };

        camera.LookAt(new Vector3(1, 2, 3));

        Assert.Equal(-Vector3.UnitZ, camera.Forward);
    }

    [Fact]
    public void ModifyDirectionClampsPitchShortOfVertical()
    {
        var camera = new Camera3D();

        camera.ModifyDirection(0, -1000f); // look up as far as possible

        Assert.Equal(MathF.Sin(float.DegreesToRadians(89f)), camera.Forward.Y, Epsilon);
        Assert.Equal(1f, camera.Forward.Length(), Epsilon);
    }

    [Fact]
    public void ModifyDirectionYawsAroundY()
    {
        var camera = new Camera3D();

        camera.ModifyDirection(90f, 0f); // yaw −90° → 0°: from −Z to +X

        Assert.Equal(1f, camera.Forward.X, Epsilon);
        Assert.Equal(0f, camera.Forward.Z, Epsilon);
    }

    [Theory]
    [InlineData(1000f, 1f)]
    [InlineData(-1000f, 90f)]
    [InlineData(5f, 40f)]
    public void ModifyZoomClampsFieldOfView(float amount, float expected)
    {
        var camera = new Camera3D();

        camera.ModifyZoom(amount);

        Assert.Equal(expected, camera.FieldOfView, Epsilon);
    }

    [Fact]
    public void Camera2DOrthographicExtentFollowsSizeAndZoom()
    {
        var camera = new Camera2D { Position = new Vector3(0, 0, 10), Size = new Vector2(20, 10) };

        var corner = ToNdc(camera, new Vector3(10, 5, 0), out var w);

        Assert.Equal(1f, w, Epsilon); // orthographic
        Assert.Equal(1f, corner.X, Epsilon);
        Assert.Equal(1f, corner.Y, Epsilon);

        camera.Zoom = 2f; // zoom > 1 shows more of the world
        corner = ToNdc(camera, new Vector3(10, 5, 0), out _);
        Assert.Equal(0.5f, corner.X, Epsilon);
    }

    [Fact]
    public void Camera2DZoomIsClamped()
    {
        var camera = new Camera2D();

        camera.ModifyZoom(1000f);
        Assert.Equal(10f, camera.Zoom);

        camera.ModifyZoom(-1000f);
        Assert.Equal(0.001f, camera.Zoom, 1e-6f);
    }

    [Fact]
    public void Node3DModelMatrixAppliesScaleRotationThenTranslation()
    {
        var node = new Node3D
        {
            Position = new Vector3(10, 0, 0),
            Rotation = new Vector3(0, 90, 0),
            Scale = new Vector3(2, 2, 2),
        };

        // (1,0,0) → scale (2,0,0) → rotate 90° about Y (right-handed: +X → −Z) → translate.
        var world = Vector3.Transform(Vector3.UnitX, node.ModelMatrix);

        Assert.Equal(10f, world.X, Epsilon);
        Assert.Equal(0f, world.Y, Epsilon);
        Assert.Equal(-2f, world.Z, Epsilon);
    }
}

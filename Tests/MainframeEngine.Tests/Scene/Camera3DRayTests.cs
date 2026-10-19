using System.Numerics;

namespace MainframeEngine.Tests.Scene;

public sealed class Camera3DRayTests
{
    private static ICamera LookingDownMinusZ()
    {
        var tree = new SceneTree(new ServerRegistry());
        var camera = new Camera3D { Position = new Vector3(0, 0, 10), Fov = 60f };
        tree.Root.AddChild(camera);
        camera.LookAt(Vector3.Zero);
        return camera.SyncRenderCamera(2f);
    }

    [Fact]
    public void CentrePixelLooksDownTheCameraForward()
    {
        var (origin, direction) = Camera3D.ProjectRay(LookingDownMinusZ(), new Vector2(200, 100), new Vector2(400, 200));
        Assert.Equal(new Vector3(0, 0, 10), origin);
        Assert.True(Vector3.Distance(-Vector3.UnitZ, direction) < 1e-4f, direction.ToString());
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(400, 0)]
    [InlineData(0, 200)]
    public void ZeroSizedViewportGivesTheCameraPositionAndForwardNotNaN(float width, float height)
    {
        var camera = LookingDownMinusZ();
        var (origin, direction) = Camera3D.ProjectRay(camera, new Vector2(10, 10), new Vector2(width, height));
        Assert.Equal(camera.Position, origin);
        Assert.Equal(camera.Forward, direction);
        Assert.False(float.IsNaN(direction.X + direction.Y + direction.Z));
    }

    [Theory]
    [InlineData(0f, 0f, 0f)]
    [InlineData(2f, 1.5f, -3f)]
    [InlineData(-4f, -2f, 5f)]
    public void UnprojectPositionMatchesTheGizmoProjectionAndInvertsTheRay(float x, float y, float z)
    {
        var camera = LookingDownMinusZ();
        var viewport = new Vector2(400, 200);
        var point = new Vector3(x, y, z);

        var pixel = Camera3D.UnprojectPosition(camera, point, viewport);
        Assert.True(GizmoProjection.TryProject(point, camera.ViewMatrix * camera.ProjectionMatrix, viewport, out var gizmo));
        Assert.True(Vector2.Distance(gizmo, pixel) < 1e-3f, $"{pixel} vs {gizmo}");

        // The ray back through that pixel passes through the point.
        var (origin, direction) = Camera3D.ProjectRay(camera, pixel, viewport);
        var along = Vector3.Dot(point - origin, direction);
        Assert.True(Vector3.Distance(origin + direction * along, point) < 1e-3f);
    }

    [Fact]
    public void CameraNodeUnprojectsIntoItsViewportAndKnowsWhatIsBehindIt()
    {
        var tree = new SceneTree(new ServerRegistry());
        tree.Root.SetSize(new Vector2(400, 200));
        var camera = new Camera3D { Position = new Vector3(0, 0, 10), Fov = 60f };
        tree.Root.AddChild(camera);
        camera.LookAt(Vector3.Zero);

        Assert.True(Vector2.Distance(new Vector2(200, 100), camera.UnprojectPosition(Vector3.Zero)) < 1e-3f);
        var upLeft = camera.UnprojectPosition(new Vector3(-1, 1, 0));
        Assert.True(upLeft.X < 200 && upLeft.Y < 100, upLeft.ToString());

        Assert.False(camera.IsPositionBehind(Vector3.Zero));
        Assert.True(camera.IsPositionBehind(new Vector3(0, 0, 11)));
        Assert.True(camera.IsPositionBehind(new Vector3(0, 0, 10 - camera.Near * 0.5f))); // in front, but nearer than Near
    }

    [Fact]
    public void TopLeftPixelPointsUpAndLeft()
    {
        var (_, direction) = Camera3D.ProjectRay(LookingDownMinusZ(), new Vector2(0, 0), new Vector2(400, 200));
        Assert.True(direction.X < 0 && direction.Y > 0 && direction.Z < 0, direction.ToString());
        Assert.Equal(1f, direction.Length(), 4);
    }
}

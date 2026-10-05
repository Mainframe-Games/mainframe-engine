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

    [Fact]
    public void TopLeftPixelPointsUpAndLeft()
    {
        var (_, direction) = Camera3D.ProjectRay(LookingDownMinusZ(), new Vector2(0, 0), new Vector2(400, 200));
        Assert.True(direction.X < 0 && direction.Y > 0 && direction.Z < 0, direction.ToString());
        Assert.Equal(1f, direction.Length(), 4);
    }
}

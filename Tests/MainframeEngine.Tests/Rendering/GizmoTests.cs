using System.Numerics;

namespace MainframeEngine.Tests.Rendering;

public sealed class GizmoTests
{
    private static PerspectiveCamera Camera() => new()
    {
        Position = new Vector3(0, 0, 10),
        Forward = -Vector3.UnitZ,
        Up = Vector3.UnitY,
        AspectRatio = 2f,
    };

    [Fact]
    public void ProjectionPutsTheTargetAtTheViewportCentre()
    {
        var camera = Camera();
        Assert.True(GizmoProjection.TryProject(Vector3.Zero, camera.ViewMatrix * camera.ProjectionMatrix, new Vector2(400, 200), out var pixel));
        Assert.True(Vector2.Distance(new Vector2(200, 100), pixel) < 0.01f, pixel.ToString());
    }

    [Fact]
    public void PointLightBehindTheCameraDrawsNothing()
    {
        var lights = new LightEnvironment();
        lights.AddLight(new PointLight { Position = new Vector3(0, 0, 20), Range = 1f });
        var batch = new ScreenGizmoBatch();
        LightGizmos.Draw(batch, Camera(), new Vector2(400, 200), lights, scale: 1f);
        Assert.Equal(0, batch.VertexCount);
    }

    [Fact]
    public void PointLightInFrontDrawsAnIconAndRangeCircles()
    {
        var lights = new LightEnvironment();
        lights.AddLight(new PointLight { Position = Vector3.Zero, Range = 2f });
        var batch = new ScreenGizmoBatch();
        LightGizmos.Draw(batch, Camera(), new Vector2(400, 200), lights, scale: 1f);
        Assert.True(batch.VertexCount > 100);
    }

    [Fact]
    public void AxisGizmoXPointsRightAndYPointsUpForAFrontCamera()
    {
        var batch = new ScreenGizmoBatch();
        AxisGizmo.Draw(batch, Camera(), new Vector2(400, 200), scale: 1f);
        var (x, y) = (AxisGizmo.LastTips[0], AxisGizmo.LastTips[1]);
        Assert.True(x.X > AxisGizmo.LastOrigin.X, "X right");
        Assert.True(y.Y < AxisGizmo.LastOrigin.Y, "Y up (screen -Y)");
    }
}

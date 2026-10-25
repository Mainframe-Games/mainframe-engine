using System.Numerics;
using MainframeEngine;

namespace Demo.Tests;

[Collection(nameof(SerialRmlUi))]
public sealed class SpineSceneTests : IDisposable
{
    private readonly ServerRegistry _servers = new();
    private readonly SceneTree _tree;
    private uint _frame;

    public SpineSceneTests()
    {
        _servers.Register(new UiServer(options: new UiServerOptions { HotReload = false }));
        _tree = new SceneTree(_servers);
        _tree.ChangeScene(SpineScene.Build());
    }

    public void Dispose()
    {
        _tree.Shutdown();
        _servers.Dispose();
    }

    private Node Scene => _tree.CurrentScene!;

    private SpinePanel Panel => Scene.GetNode<SpinePanel>("Ui/Panel");

    private Camera2D Camera2D => Scene.GetNode<Camera2D>("Camera2D");

    private Node3D Pivot => Scene.GetNode<Node3D>("Pivot");

    private SpineNode Spine => Scene.GetNode<SpineNode>("Pivot/SpineBoy");

    private MeshInstance3D Backdrop => Scene.GetNode<MeshInstance3D>("Backdrop");

    private void Tick() => TestTime.Tick(_tree, ref _frame);

    [Fact]
    public void TheSceneStartsUnderTheCamera3D()
    {
        Tick();
        var viewport = _tree.Root;
        Assert.NotNull(viewport.ActiveCamera3D);
        Assert.True(viewport.ActiveCamera3D!.Current);
        Assert.False(Camera2D.Current);
        Assert.Equal(Vector3.Zero, Pivot.RotationDegrees);
        Assert.Equal(SpinePanel.Scale3D, Spine.SpineScale);
        Assert.True(Backdrop.Visible);
    }

    [Fact]
    public void The2DModeHandsTheViewportToTheCamera2DAndTurnsTheSkeletonOver()
    {
        Tick();
        Panel.Mode2D = true;
        Tick();

        var viewport = _tree.Root;
        Assert.Null(viewport.ActiveCamera3D);
        Assert.Same(Camera2D, viewport.ActiveCamera2D);
        Assert.True(Camera2D.Current);
        Assert.Equal(new Vector3(180, 0, 0), Pivot.RotationDegrees);
        Assert.Equal(SpinePanel.Scale2D, Spine.SpineScale);
        Assert.Equal(SpinePanel.SunRotation2D, Scene.GetNode<DirectionalLight3D>("Sun").RotationDegrees);
        Assert.False(Backdrop.Visible);
    }

    [Fact]
    public void TogglingBackRestoresTheCamera3D()
    {
        Tick();
        Panel.Mode2D = true;
        Tick();
        Panel.Mode2D = false;
        Tick();

        var viewport = _tree.Root;
        var camera3d = Scene.GetNode<Camera3D>("Camera3D");
        Assert.Same(camera3d, viewport.ActiveCamera3D);
        Assert.True(camera3d.Current);
        Assert.False(Camera2D.Current);
        Assert.Equal(Vector3.Zero, Pivot.RotationDegrees);
        Assert.Equal(SpinePanel.Scale3D, Spine.SpineScale);
        Assert.Equal(SpinePanel.SunRotation3D, Scene.GetNode<DirectionalLight3D>("Sun").RotationDegrees);
        Assert.True(Backdrop.Visible);
    }

    [Fact]
    public void ChoosingAnAnimationPlaysIt()
    {
        Tick();
        Panel.Animation = "run";
        Assert.Equal("run", Spine.CurrentAnimation?.Name);
    }

    [Fact]
    public void TheShadowIsShapedUnlessTurnedOff()
    {
        Tick();
        Assert.True(Panel.ShapedShadow);
        Assert.Equal(SpineNode.DefaultShadowAlphaThreshold, Spine.ShadowAlphaThreshold);
        Panel.ShapedShadow = false;
        Assert.Equal(0f, Spine.ShadowAlphaThreshold);
    }

    [Fact]
    public void ABillboardTurnsSpineBoyBackOnlyIn3D()
    {
        Tick();
        Panel.TurnedAway = true;
        Panel.Billboard = "fixed_y";
        Assert.Equal(new Vector3(0, SpinePanel.TurnedAwayYaw, 0), Spine.RotationDegrees);
        Assert.Equal(BillboardMode.FixedY, Spine.Billboard);

        Panel.Mode2D = true; // the pivot turns the skeleton over: no billboard, no turn
        Tick();
        Assert.Equal(BillboardMode.Disabled, Spine.Billboard);
        Assert.Equal(Vector3.Zero, Spine.RotationDegrees);

        Panel.Mode2D = false;
        Tick();
        Assert.Equal(BillboardMode.FixedY, Spine.Billboard);
        Assert.Equal(new Vector3(0, SpinePanel.TurnedAwayYaw, 0), Spine.RotationDegrees);
        Panel.Billboard = "enabled";
        Assert.Equal(BillboardMode.Enabled, Spine.Billboard);
    }

    [Fact]
    public void LeavingTheSceneIn2DModeFreesTheDetachedCamera3D()
    {
        Tick();
        var camera3d = Scene.GetNode<Camera3D>("Camera3D");
        Panel.Mode2D = true;
        Tick();
        _tree.ChangeScene(new Node3D { Name = "other" });
        Tick();
        Assert.True(camera3d.IsFreed);
    }
}

using System.Numerics;

namespace MainframeEngine.RenderTests.Host.Scenes;

/// <summary>SpineBoy under a Camera2D only (no Camera3D): upright and front-facing via a 180°-about-X pivot (ADR 0110).</summary>
public sealed class Spine2DScene(HostOptions host) : RenderTestGame(host)
{
    protected override void LoadScene()
    {
        Tree.Root.AddChild(new Camera2D { Name = "Camera", Current = true });
        var pivot = new Node3D { Name = "Pivot", RotationDegrees = new Vector3(180, 0, 0), Position = new Vector3(0, 90, 0) };
        Tree.Root.AddChild(pivot);
        pivot.AddChild(new SpineNode { Name = "SpineBoy", Folder = "Content/Models/Spine/SpineBoy", Animation = "idle", SpineScale = 0.35f });
        Tree.Root.AddChild(new DirectionalLight3D { Name = "Sun", RotationDegrees = new Vector3(-40, 160, 0) });
    }

    protected override void UpdateScene(in GameTime gameTime) { }

    protected override void DisposeScene() { }
}

using System.Numerics;

namespace MainframeEngine.RenderTests.Host.Scenes;

/// <summary>
/// SpineBoy's shadow under a low sun (about 20° up, CSM), seen from above so the long shadow lies on a plain floor
/// (ADR 0204). <paramref name="threshold"/> is <see cref="SpineNode.ShadowAlphaThreshold"/>: the default 0.5 casts the
/// silhouette, 0 the attachments' whole quads (the shadow before alpha testing).
/// </summary>
public sealed class SpineShadowScene(HostOptions host, float threshold) : ShadowSceneBase(host)
{
    protected override void Build(Node3D scene)
    {
        Camera.Position = new Vector3(1.5f, 7.5f, 3.5f);
        Camera.LookAt(new Vector3(0.6f, 0f, -2.6f));
        scene.AddChild(new MeshInstance3D { Name = "Floor", Mesh = new PlaneMesh { Size = new Vector2(24, 24) }, MaterialOverride = Plain(215, 210, 200) });
        scene.AddChild(Sun(new Vector3(0.3f, -0.38f, -0.95f)));
        scene.AddChild(SpineShadowScenes.Boy(threshold));
    }
}

/// <summary>
/// SpineBoy's alpha-tested shadow from a spot light (an atlas tile) or an omni light (a cube), with no sun. The light
/// stands in front of him, so the shadow falls on the floor behind. <c>--count 1</c> sets the threshold to 0 (quads).
/// </summary>
public sealed class SpineShadowLightsScene(HostOptions host, bool omni) : ShadowSceneBase(host)
{
    protected override bool HasSky => false;

    protected override void Build(Node3D scene)
    {
        Camera.Position = new Vector3(1.5f, 7.5f, 3.5f);
        Camera.LookAt(new Vector3(0.6f, 0f, -2.6f));
        Environment.AmbientColor = new Vector3(0.12f, 0.12f, 0.14f);
        scene.AddChild(new MeshInstance3D { Name = "Floor", Mesh = new PlaneMesh { Size = new Vector2(24, 24) }, MaterialOverride = Plain(215, 210, 200) });
        var position = new Vector3(0.6f, 3.2f, 2.5f);
        if (omni)
            scene.AddChild(new OmniLight3D { Name = "Torch", Position = position, Color = new Vector3(1f, 0.85f, 0.6f), Energy = 2.5f, Range = 14f });
        else
            scene.AddChild(Aim(new SpotLight3D { Name = "Spot", Position = position, Color = new Vector3(1f, 0.9f, 0.75f), Energy = 2.5f, Range = 16f, InnerConeAngle = 35f, OuterConeAngle = 50f },
                new Vector3(-0.15f, -0.8f, -1f)));
        scene.AddChild(SpineShadowScenes.Boy(Host.Count == 1 ? 0f : SpineNode.DefaultShadowAlphaThreshold));
    }
}

/// <summary>
/// <see cref="SpineNode.Billboard"/> (ADR 0204): two SpineBoys turned 90° from the camera, so that without billboarding it
/// sees them edge-on. Left: <see cref="BillboardMode.FixedY"/>; right: <see cref="BillboardMode.Enabled"/> with
/// <see cref="SpineNode.FlipX"/>. A fixed sun casts their shadows from the poses the camera sees.
/// <paramref name="yaw"/> orbits the camera about the pair (degrees); <c>--count 1</c> turns billboarding off.
/// </summary>
public sealed class SpineBillboardScene(HostOptions host, float yaw) : ShadowSceneBase(host)
{
    protected override void Build(Node3D scene)
    {
        var orbit = Matrix4x4.CreateRotationY(yaw * MathF.PI / 180f);
        Camera.Position = Vector3.Transform(new Vector3(0, 3.2f, 6.5f), orbit);
        Camera.LookAt(new Vector3(0, 1.0f, 0));
        scene.AddChild(new MeshInstance3D { Name = "Floor", Mesh = new PlaneMesh { Size = new Vector2(40, 40) }, MaterialOverride = Plain(215, 210, 200) });
        scene.AddChild(Sun(new Vector3(-0.35f, -0.6f, -0.75f))); // fixed in the world, behind the camera at both yaws

        var billboards = Host.Count != 1;
        var right = Vector3.Transform(Vector3.UnitX, orbit);
        var fixedY = SpineShadowScenes.Boy(SpineNode.DefaultShadowAlphaThreshold);
        fixedY.Name = "FixedY";
        fixedY.Position = -1.3f * right;
        fixedY.RotationDegrees = new Vector3(0, yaw + 90f, 0); // edge-on to the camera unless billboarded
        fixedY.Billboard = billboards ? BillboardMode.FixedY : BillboardMode.Disabled;
        scene.AddChild(fixedY);

        var enabled = SpineShadowScenes.Boy(SpineNode.DefaultShadowAlphaThreshold);
        enabled.Name = "EnabledFlipX";
        enabled.Position = 1.3f * right;
        enabled.RotationDegrees = new Vector3(0, yaw - 90f, 0);
        enabled.Billboard = billboards ? BillboardMode.Enabled : BillboardMode.Disabled;
        enabled.FlipX(true);
        scene.AddChild(enabled);
    }
}

internal static class SpineShadowScenes
{
    /// <summary>SpineBoy (about 2.2 m tall) walking; the fixed frame delta gives the same pose at a given frame every run.</summary>
    public static SpineNode Boy(float threshold) => new()
    {
        Name = "SpineBoy",
        Folder = SpineScene.SpineFolder,
        Scale = new Vector3(0.2f),
        Animation = "walk",
        ShadowAlphaThreshold = threshold,
    };
}

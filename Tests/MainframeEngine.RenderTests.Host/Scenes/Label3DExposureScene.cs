using System.Numerics;

namespace MainframeEngine.RenderTests.Host.Scenes;

using Color = System.Drawing.Color;

/// <summary>
/// <see cref="Label3D.IgnoreExposure"/> and <see cref="UiPanel3D.IgnoreExposure"/> (ADR 0257): at exposure
/// <see cref="HighExposure"/> (or 1 with <c>--count 1</c>), an orange label and a grey unshaded panel that ignore exposure
/// on the left, the same pair without it on the right, over the dark clear colour (no sky, no fog). The test compares
/// the two runs: the left pair keeps its exposure-1 colour (the tonemap still applies), the right pair brightens.
/// </summary>
public sealed class Label3DExposureScene(HostOptions host) : ShadowSceneBase(host)
{
    public const float HighExposure = 4f;
    public const uint ShotFrame = 8;

    /// <summary>Where each surface sits (world): left label, right label, left panel, right panel.</summary>
    public static readonly Vector3 LeftLabel = new(-1.3f, 0.8f, 0), RightLabel = new(1.3f, 0.8f, 0);
    public static readonly Vector3 LeftPanel = new(-1.3f, -0.8f, 0), RightPanel = new(1.3f, -0.8f, 0);

    private const float CameraZ = 5f, Fov = 60f;

    /// <summary>The framebuffer pixel of a point on the z = 0 plane (the camera looks down −Z from z = 5).</summary>
    public static Vector2 ToPixel(Vector3 point, int width, int height)
    {
        var halfHeight = CameraZ * MathF.Tan(Fov * MathF.PI / 360f);
        var halfWidth = halfHeight * width / height;
        return new Vector2((point.X / halfWidth + 1f) * 0.5f * width, (1f - point.Y / halfHeight) * 0.5f * height);
    }

    protected override bool HasSky => false;

    protected override void Build(Node3D scene)
    {
        Vulkan.Exposure = Host.Count == 1 ? 1f : HighExposure;
        Camera.Position = new Vector3(0, 0, CameraZ);
        Camera.Fov = Fov;
        foreach (var (position, ignore) in new[] { (LeftLabel, true), (RightLabel, false) })
            scene.AddChild(new Label3D
            {
                Name = ignore ? "IgnoresExposure" : "Exposed",
                Position = position,
                Text = "HIH",
                FontSize = 128,
                PixelSize = 0.006f,
                OutlineSize = 0,
                IgnoreExposure = ignore,
                Modulate = Color.FromArgb(255, 240, 140, 40),
            });
        foreach (var (position, ignore) in new[] { (LeftPanel, true), (RightPanel, false) })
            scene.AddChild(new UiPanel3D
            {
                Name = ignore ? "PanelIgnoresExposure" : "PanelExposed",
                Position = position,
                Resolution = new Vector2I(160, 100),
                PixelSize = 0.01f,
                Background = Color.FromArgb(255, 128, 128, 128),
                InputMode = PanelInputMode.None,
                IgnoreExposure = ignore,
            });
    }
}

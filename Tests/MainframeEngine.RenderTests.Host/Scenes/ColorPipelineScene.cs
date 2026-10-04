using System.Numerics;
using ImGuiNET;

namespace MainframeEngine.RenderTests.Host.Scenes;

/// <summary>
/// Numeric checks of the colour pipeline. The whole frame is a panoramic sky made from a solid sRGB texture
/// (<see cref="SkyColor"/>), so every scene pixel is <c>encode(aces(decode(c) · exposure))</c>, which the test
/// recomputes with <see cref="ColorSpace"/>. Two ImGui rectangles check the overlay: an opaque one must keep its
/// sRGB value exactly, and 50 % white over black must blend in sRGB space (≈128, as before the colour pipeline;
/// a linear blend would give ≈188). Exposure switches to <see cref="SecondExposure"/> on frame
/// <see cref="ExposureSwitchFrame"/>.
/// </summary>
public sealed class ColorPipelineScene(HostOptions host) : RenderTestGame(host)
{
    public static readonly byte[] SkyColor = [200, 120, 40, 255];
    public static readonly Vector4 OverlayColor = new(64 / 255f, 128 / 255f, 191 / 255f, 1f);
    public const float SecondExposure = 2.5f;
    public const uint ExposureSwitchFrame = 8;

    /// <summary>ImGui rectangles in points: opaque colour, then black with 50 % white over it.</summary>
    public static readonly (Vector2 Min, Vector2 Max) OpaqueRect = (new Vector2(10, 10), new Vector2(60, 60));
    public static readonly (Vector2 Min, Vector2 Max) BlendRect = (new Vector2(70, 10), new Vector2(120, 60));

    private readonly Camera3D _camera = new();
    private SkyEnvironment _sky = null!;

    protected override void LoadScene()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mf-color-pipeline-{Environment.ProcessId}.png");
        var pixels = new byte[16 * 8 * 4];
        for (var i = 0; i < pixels.Length; i += 4)
            SkyColor.CopyTo(pixels, i);
        Png.WriteRgba8(path, 16, 8, pixels);
        _sky = new SkyPanoramic(Renderer, path);
        File.Delete(path);

        _camera.Position = new Vector3(0, 1, 0);
        _camera.LookAt(new Vector3(0, 1, -1));
    }

    protected override void UpdateScene(in GameTime gameTime)
    {
        if (gameTime.FrameCount == ExposureSwitchFrame)
            Vulkan.Exposure = SecondExposure;
    }

    protected override void OnImGui(in GameTime gameTime)
    {
        var draw = ImGui.GetForegroundDrawList();
        draw.AddRectFilled(OpaqueRect.Min, OpaqueRect.Max, ImGui.ColorConvertFloat4ToU32(OverlayColor));
        draw.AddRectFilled(BlendRect.Min, BlendRect.Max, ImGui.ColorConvertFloat4ToU32(new Vector4(0, 0, 0, 1)));
        draw.AddRectFilled(BlendRect.Min, BlendRect.Max, ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, 0.5f)));
    }

    protected override void OnShadowPass(in GameTime gameTime)
    {
    }

    protected override void OnRenderMainPass(in GameTime gameTime)
    {
        _camera.AspectRatio = AspectRatio;
        _sky.Draw(_camera);
    }

    protected override void DisposeScene() => _sky.Dispose();
}

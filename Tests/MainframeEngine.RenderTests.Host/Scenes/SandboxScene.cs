using System.Globalization;
using System.Numerics;
using ImGuiNET;
using MainframeEngine.Gizmos;

namespace MainframeEngine.RenderTests.Host.Scenes;

/// <summary>
/// Mirrors the Sandbox's per-frame work — Spine, shadows, sky, grid and the ImGui debug window with
/// light and axis gizmos — for the steady-state allocation gate. Its ImGui text shows timings, so it
/// is not used for golden images.
/// </summary>
public sealed class SandboxScene(HostOptions host) : SpineScene(host)
{
    protected override void OnImGui(in GameTime gameTime)
    {
        Lights.DrawLightGizmos(Camera);

        ImGui.SetNextWindowPos(Vector2.Zero, ImGuiCond.Always, Vector2.Zero);
        if (ImGui.Begin("Game Window", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.Value("FrameCount", gameTime.FrameCount);
            ImGui.Value("DeltaTime", gameTime.DeltaTime);
            ImGui.Value("FPS", gameTime.FramesPerSecond);

            Span<char> text = stackalloc char[64];
            var p = Camera.Position;
            if (text.TryWrite(CultureInfo.InvariantCulture, $"Position: <{p.X:0.00}, {p.Y:0.00}, {p.Z:0.00}>", out var written))
                ImGui.TextUnformatted(text[..written]);
        }
        ImGui.End();

        ImGuiCoordGizmo.DrawCoordinateGizmo(Camera);
    }
}

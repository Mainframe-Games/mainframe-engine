using System.Drawing;
using System.Numerics;
using ImGuiNET;
using MainframeEngine.Utils;

namespace MainframeEngine.Gizmos;

public static class ImGuiCoordGizmo
{
    private static readonly Axis[] _axes =
    [
        new(new Vector3(1, 0, 0), Color.FromArgb(255, 220, 60, 60), "X+"),
        new(new Vector3(0, -1, 0), Color.FromArgb(255, 60, 200, 60), "Y+"),
        new(new Vector3(0, 0, 1), Color.FromArgb(255, 60, 120, 220), "Z+"),
    ];
    
    /// <summary>
    /// Draws a corner gizmo showing the XYZ axes of the right-handed world coordinate system,
    /// oriented by the current camera view. +X=red, +Y=green, +Z=blue (toward viewer).
    /// </summary>
    public static void DrawCoordinateGizmo(in ICamera camera)
    {
        var camForward = camera.Forward;
        var view   = camera.ViewMatrix;
        var vp     = ImGui.GetMainViewport();

        const float armLen  = 55f;
        const float padding = 80f;
        var origin = new Vector2(vp.Size.X - padding, /*vp.Pos.Y + vp.Size.Y -*/ padding);

        // Sort back-to-front so axes closer to the viewer draw on top.
        // A positive dot with Forward means the axis points into the scene (farther away).
        Array.Sort(_axes, (a, b) =>
            Vector3.Dot(a.Direction, camForward).CompareTo(Vector3.Dot(b.Direction, camForward)));

        var dl = ImGui.GetForegroundDrawList();

        foreach (var (Direction, Color, Label) in _axes)
        {
            var dir = ToScreenDir(Direction);
            var tip = origin + Vector2.Normalize(dir) * armLen;
            var colImGui = Color.ToImColor();
            dl.DrawArrow(origin, tip, colImGui);
            dl.AddText(tip + new Vector2(4, -6), colImGui, Label);
        }

        return;

        Vector2 ToScreenDir(in Vector3 axis)
        {
            var vx = axis.X * view.M11 + axis.Y * view.M21 + axis.Z * view.M31;
            var vy = axis.X * view.M12 + axis.Y * view.M22 + axis.Z * view.M32;
            return new Vector2(vx, vy);
        }
    }
    
    private readonly struct Axis(Vector3 direction, Color color, string label)
    {
        public Vector3 Direction => direction;
        public Color Color => color;
        public string Label => label;

        public void Deconstruct(out Vector3 dir, out Color col, out string lab)
        {
            dir = Direction;
            col = Color;
            lab = Label;
        }
    }
}
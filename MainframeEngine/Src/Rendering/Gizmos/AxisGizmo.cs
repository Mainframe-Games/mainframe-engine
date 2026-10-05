using System.Numerics;

namespace MainframeEngine;

/// <summary>The corner XYZ axes (top-right), drawn back to front with stroke labels.</summary>
public static class AxisGizmo
{
    private static readonly (Vector3 Axis, Vector4 Color, char Label)[] Axes =
    [
        (Vector3.UnitX, new Vector4(0.86f, 0.24f, 0.24f, 1), 'X'),
        (Vector3.UnitY, new Vector4(0.24f, 0.78f, 0.24f, 1), 'Y'),
        (Vector3.UnitZ, new Vector4(0.24f, 0.47f, 0.86f, 1), 'Z'),
    ];

    private static readonly int[] Order = new int[3];
    internal static readonly Vector2[] LastTips = new Vector2[3];
    internal static Vector2 LastOrigin;

    public static void Draw(ScreenGizmoBatch batch, ICamera camera, Vector2 viewport, float scale)
    {
        var view = camera.ViewMatrix;
        var origin = new Vector2(viewport.X - 80f * scale, 80f * scale);
        LastOrigin = origin;
        // Back to front: smallest view-space z first (System.Numerics view: camera looks down -Z).
        for (var i = 0; i < 3; i++)
            Order[i] = i;
        for (var i = 1; i < 3; i++)
            for (var j = i; j > 0 && ViewZ(view, Order[j]) < ViewZ(view, Order[j - 1]); j--)
                (Order[j], Order[j - 1]) = (Order[j - 1], Order[j]);

        foreach (var index in Order)
        {
            var (axis, color, label) = Axes[index];
            var vx = axis.X * view.M11 + axis.Y * view.M21 + axis.Z * view.M31;
            var vy = axis.X * view.M12 + axis.Y * view.M22 + axis.Z * view.M32;
            var screen = new Vector2(vx, -vy); // view +Y is screen up
            var tip = screen.LengthSquared() > 1e-8f ? origin + Vector2.Normalize(screen) * 55f * scale : origin;
            LastTips[index] = tip;
            batch.Arrow(origin, tip, color, 2f * scale, 10f * scale);
            batch.Glyph(label, tip + new Vector2(4f, -6f) * scale, 10f * scale, color, 1.5f * scale);
        }
    }

    private static float ViewZ(in Matrix4x4 view, int index)
    {
        var axis = Axes[index].Axis;
        return axis.X * view.M13 + axis.Y * view.M23 + axis.Z * view.M33;
    }
}

using System.Drawing;
using System.Numerics;
using ImGuiNET;

namespace MainframeEngine.Utils;

/// <summary>
/// Provides utility functions for rendering 2D gizmos in an ImGui context.
/// </summary>
public static class ImGuiGizmos
{
    /// <param name="dl">The ImDrawList instance used for rendering the sun icon.</param>
    extension(ImDrawListPtr dl)
    {
        public void DrawArrow(Vector2 from, Vector2 to, uint col, float arrowHeadLen = 10f, float thickness = 2f)
        {
            dl.AddLine(from, to, col, thickness);
            var dir  = Vector2.Normalize(to - from);
            var perp = new Vector2(-dir.Y, dir.X);
            dl.AddLine(to, to - dir * arrowHeadLen + perp * (arrowHeadLen * 0.5f), col, thickness);
            dl.AddLine(to, to - dir * arrowHeadLen - perp * (arrowHeadLen * 0.5f), col, thickness);
        }

        /// <summary>
        /// Renders a sun-shaped icon, typically used to visually represent a directional light source in debugging visuals.
        /// </summary>
        /// <param name="center">The screen-space coordinates for the center of the sun icon.</param>
        /// <param name="color">The color to use for rendering the sun icon.</param>
        public void DrawSunIcon(in Vector2 center, in Color color)
        {
            var colorPacked = color.ToImColor();
            dl.DrawSunIcon(center, colorPacked);
        }
        
        public void DrawSunIcon(in Vector2 center, in uint color)
        {
            const float innerR = 6f;
            const float outerR = 12f;

            dl.AddCircleFilled(center, innerR, color, 16);

            for (var i = 0; i < 8; i++)
            {
                var a = i * MathF.PI / 4f;
                var from = center + new Vector2(MathF.Cos(a) * (innerR + 2f), MathF.Sin(a) * (innerR + 2f));
                var to = center + new Vector2(MathF.Cos(a) * outerR, MathF.Sin(a) * outerR);
                dl.AddLine(from, to, color, 1.5f);
            }
        }
    }
}
using System.Diagnostics;
using System.Numerics;
using ImGuiNET;

namespace MainframeEngine;

public class LightEnvironment
{
    public const int MaxDirectional = 4;
    public const int MaxPoint = 16;
    public const int MaxSpot = 8;

    public Vector3 AmbientColor { get; set; } = new(0.08f, 0.08f, 0.10f);
    public List<DirectionalLight> DirectionalLights { get; } = [];
    public List<PointLight> PointLights { get; } = [];
    public List<SpotLight> SpotLights { get; } = [];

    #region Gizmos

    [Conditional("DEBUG")]
    public void DrawLightGizmos(in ICamera camera)
    {
        var drawList = ImGui.GetBackgroundDrawList();
        var screenSize = ImGui.GetIO().DisplaySize;
        var view = camera.ViewMatrix;
        var proj = camera.ProjectionMatrix;

        foreach (var pt in PointLights)
        {
            if (!TryProjectToScreen(pt.Position, view, proj, screenSize, out var sp))
                continue;
            
            var col = new Vector4(pt.Color, 1f);
            var packed = ImGui.ColorConvertFloat4ToU32(col);
            drawList.AddCircleFilled(sp, 8f, packed);
            drawList.AddCircle(sp, 10f, 0xFFFFFFFF, 0, 1.5f);

            // Three orthogonal circles showing the light's range radius.
            var fadedPacked = ImGui.ColorConvertFloat4ToU32(new Vector4(pt.Color, 0.5f));
            DrawCircle3d(drawList, pt.Position, Vector3.UnitX, Vector3.UnitY, pt.Range, view, proj, screenSize, fadedPacked);
            DrawCircle3d(drawList, pt.Position, Vector3.UnitX, Vector3.UnitZ, pt.Range, view, proj, screenSize, fadedPacked);
            DrawCircle3d(drawList, pt.Position, Vector3.UnitY, Vector3.UnitZ, pt.Range, view, proj, screenSize, fadedPacked);
        }

        foreach (var sl in SpotLights)
        {
            if (!TryProjectToScreen(sl.Position, view, proj, screenSize, out var sp))
                continue;
            
            var col = new Vector4(sl.Color, 1f);
            var packed = ImGui.ColorConvertFloat4ToU32(col);
            drawList.AddCircleFilled(sp, 8f, packed);
            drawList.AddCircle(sp, 10f, 0xFFFFFFFF, 0, 1.5f);

            // Cone gizmo: compute the two axes perpendicular to the spotlight direction.
            var perp1 = GetPerp(sl.Direction);
            var perp2 = Vector3.Normalize(Vector3.Cross(sl.Direction, perp1));
            var coneBase = sl.Position + sl.Direction * sl.Range;
            var outerRadius = sl.Range * MathF.Tan(sl.OuterConeAngle * MathF.PI / 180f);
            var innerRadius = sl.Range * MathF.Tan(sl.InnerConeAngle * MathF.PI / 180f);

            // Outer cone circle.
            DrawCircle3d(drawList, coneBase, perp1, perp2, outerRadius, view, proj, screenSize, packed);
            // Inner cone circle (faded).
            var fadedPacked = ImGui.ColorConvertFloat4ToU32(new Vector4(sl.Color, 0.5f));
            DrawCircle3d(drawList, coneBase, perp1, perp2, innerRadius, view, proj, screenSize, fadedPacked);

            // Four edge lines from the apex to the outer rim.
            for (int i = 0; i < 4; i++)
            {
                float angle = i * MathF.PI / 2f;
                var rimPoint = coneBase + perp1 * (MathF.Cos(angle) * outerRadius) + perp2 * (MathF.Sin(angle) * outerRadius);
                if (TryProjectToScreen(rimPoint, view, proj, screenSize, out var rp))
                    drawList.AddLine(sp, rp, packed, 1.2f);
            }
        }

        foreach (var dl in DirectionalLights)
        {
            if (!TryProjectToScreen(dl.Position, view, proj, screenSize, out var sp))
                continue;
            
            // var origin = new Vector2(screenSize.X - 60f, 60f);
            var origin = sp;
            var dir2d = new Vector2(dl.Direction.X, dl.Direction.Y);
            if (dir2d.LengthSquared() > 0.0001f)
                dir2d = Vector2.Normalize(dir2d);
            var col = new Vector4(dl.Color, 1f);
            var packed = ImGui.ColorConvertFloat4ToU32(col);
            drawList.AddLine(origin, origin + dir2d * 40f, packed, 2f);
            drawList.AddCircleFilled(origin, 5f, packed);
        }
    }
    
    // Returns a vector perpendicular to v.
    private static Vector3 GetPerp(Vector3 v)
    {
        var other = MathF.Abs(v.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY;
        return Vector3.Normalize(Vector3.Cross(v, other));
    }

    // Draws a world-space circle defined by center + right/up axes as a projected polyline.
    private static void DrawCircle3d(
        ImDrawListPtr drawList,
        Vector3 center,
        Vector3 right,
        Vector3 up,
        float radius,
        Matrix4x4 view,
        Matrix4x4 proj,
        Vector2 screenSize,
        uint color,
        int segments = 32)
    {
        Vector2? prev = null;
        for (int i = 0; i <= segments; i++)
        {
            float angle = i * MathF.Tau / segments;
            var worldPos = center + right * (MathF.Cos(angle) * radius) + up * (MathF.Sin(angle) * radius);
            if (!TryProjectToScreen(worldPos, view, proj, screenSize, out var sp))
            {
                prev = null;
                continue;
            }
            if (prev.HasValue)
                drawList.AddLine(prev.Value, sp, color, 1.2f);
            prev = sp;
        }
    }
    
    private static bool TryProjectToScreen(Vector3 worldPos, Matrix4x4 view, Matrix4x4 proj, Vector2 screenSize, out Vector2 screenPos)
    {
        var clip = Vector4.Transform(new Vector4(worldPos, 1f), view * proj);
        if (clip.W <= 0f)
        {
            screenPos = default;
            return false;
        }
        var ndc = new Vector3(clip.X / clip.W, clip.Y / clip.W, clip.Z / clip.W);
        screenPos = new Vector2(
            (ndc.X * 0.5f + 0.5f) * screenSize.X,
            (1.0f - (ndc.Y * 0.5f + 0.5f)) * screenSize.Y
        );
        return true;
    }
    
    #endregion
}

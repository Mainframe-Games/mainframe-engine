using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;
using MainframeEngine.Gizmos;
using MainframeEngine.Utils;

namespace MainframeEngine;

public class LightEnvironment
{
    // From Content/Shaders/limits.json (generated ShaderLimits / include/limits.glsl): C# and GLSL cannot drift.
    public const int MaxDirectional = ShaderLimits.MaxDirectionalLights;
    public const int MaxPoint = ShaderLimits.MaxPointLights;
    public const int MaxSpot = ShaderLimits.MaxSpotLights;

    /// <summary>
    /// Size in bytes of the std140 lights UBO read by <c>Shapes.vk.frag</c> and <c>SpineLit.vk.frag</c>:
    /// header (ambient, camera position, counts) then fixed-size directional, point and spot arrays.
    /// </summary>
    internal const int UboSize =
        UboHeaderSize +
        MaxDirectional * DirectionalStride +
        MaxPoint       * PointStride +
        MaxSpot        * SpotStride; // 1200 bytes

    private const int UboHeaderSize     = 48; // vec4 ambient, vec4 cameraPos, ivec4 counts
    private const int DirectionalStride = 32; // vec4 direction+intensity, vec4 color
    private const int PointStride       = 32; // vec4 position+range, vec4 color+intensity
    private const int SpotStride        = 64; // vec4 position+range, vec4 direction+intensity, vec4 color+cosInner, vec4 cosOuter

    public Vector3 AmbientColor { get; set; } = new(0.08f, 0.08f, 0.10f);
    internal List<DirectionalLight> DirectionalLights { get; } = [];
    internal List<PointLight> PointLights { get; } = [];
    internal List<SpotLight> SpotLights { get; } = [];

    /// <summary>
    /// Packs the lights into <paramref name="destination"/> using the std140 layout described by
    /// <see cref="UboSize"/>. Lights beyond the per-type maximum are ignored; unused slots are zeroed.
    /// </summary>
    internal void WriteUbo(Span<byte> destination, in Vector3 cameraPosition)
    {
        if (destination.Length < UboSize)
            throw new ArgumentException($"Lights UBO needs {UboSize} bytes.", nameof(destination));

        var ubo = destination[..UboSize];
        ubo.Clear();
        var f = MemoryMarshal.Cast<byte, float>(ubo);
        var i = MemoryMarshal.Cast<byte, int>(ubo);

        int numDir   = Math.Min(DirectionalLights.Count, MaxDirectional);
        int numPoint = Math.Min(PointLights.Count,       MaxPoint);
        int numSpot  = Math.Min(SpotLights.Count,        MaxSpot);

        // Header
        f[0] = AmbientColor.X; f[1] = AmbientColor.Y; f[2] = AmbientColor.Z;
        f[4] = cameraPosition.X; f[5] = cameraPosition.Y; f[6] = cameraPosition.Z;
        i[8] = numDir; i[9] = numPoint; i[10] = numSpot;

        var o = UboHeaderSize / sizeof(float);
        for (int n = 0; n < numDir; n++, o += DirectionalStride / sizeof(float))
        {
            var l = DirectionalLights[n];
            f[o + 0] = l.Direction.X; f[o + 1] = l.Direction.Y; f[o + 2] = l.Direction.Z;
            f[o + 3] = l.Intensity;
            f[o + 4] = l.Color.X; f[o + 5] = l.Color.Y; f[o + 6] = l.Color.Z;
        }

        o = (UboHeaderSize + MaxDirectional * DirectionalStride) / sizeof(float);
        for (int n = 0; n < numPoint; n++, o += PointStride / sizeof(float))
        {
            var l = PointLights[n];
            f[o + 0] = l.Position.X; f[o + 1] = l.Position.Y; f[o + 2] = l.Position.Z;
            f[o + 3] = l.Range;
            f[o + 4] = l.Color.X; f[o + 5] = l.Color.Y; f[o + 6] = l.Color.Z;
            f[o + 7] = l.Intensity;
        }

        o = (UboHeaderSize + MaxDirectional * DirectionalStride + MaxPoint * PointStride) / sizeof(float);
        for (int n = 0; n < numSpot; n++, o += SpotStride / sizeof(float))
        {
            var l = SpotLights[n];
            f[o + 0] = l.Position.X; f[o + 1] = l.Position.Y; f[o + 2] = l.Position.Z;
            f[o + 3] = l.Range;
            f[o + 4] = l.Direction.X; f[o + 5] = l.Direction.Y; f[o + 6] = l.Direction.Z;
            f[o + 7] = l.Intensity;
            f[o + 8] = l.Color.X; f[o + 9] = l.Color.Y; f[o + 10] = l.Color.Z;
            f[o + 11] = float.Cos(float.DegreesToRadians(l.InnerConeAngle));
            f[o + 12] = float.Cos(float.DegreesToRadians(l.OuterConeAngle));
        }
    }

    /// <summary>
    /// Adds a light to the corresponding collection based on its type.
    /// </summary>
    /// <param name="light">The light to add. Can be of type DirectionalLight, PointLight, or SpotLight.</param>
    public void AddLight(Light light)
    {
        switch (light)
        {
            case DirectionalLight dl:
                DirectionalLights.Add(dl);
                break;
            case PointLight pl:
                PointLights.Add(pl);
                break;
            case SpotLight sl:
                SpotLights.Add(sl);
                break;
        }
    }

    /// <summary>Removes a light added with <see cref="AddLight"/>; returns false if it was not present.</summary>
    public bool RemoveLight(Light light) => light switch
    {
        DirectionalLight dl => DirectionalLights.Remove(dl),
        PointLight pl => PointLights.Remove(pl),
        SpotLight sl => SpotLights.Remove(sl),
        _ => false,
    };

    /// <summary>Number of lights of every type (including ones beyond the per-type UBO limits).</summary>
    public int Count => DirectionalLights.Count + PointLights.Count + SpotLights.Count;


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
            if (!TryProjectToScreen(dl.Position, view, proj, screenSize, out var origin))
                continue;

            var dir2d = new Vector2(dl.Direction.X, -dl.Direction.Y);
            if (dir2d.LengthSquared() > 0.0001f)
                dir2d = Vector2.Normalize(dir2d);
            var col = new Vector4(dl.Color, 1f);
            var packed = ImGui.ColorConvertFloat4ToU32(col);
            drawList.DrawArrow(origin, origin + dir2d * 40f, packed);
            drawList.DrawSunIcon(origin, packed);
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

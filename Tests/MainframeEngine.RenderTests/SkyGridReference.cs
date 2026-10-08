using System.Numerics;

namespace MainframeEngine.RenderTests;

/// <summary>
/// CPU reference for the <c>sky-grid</c> scene: the procedural sky colour of a pixel (a mirror of
/// <c>include/sky.slang</c> + <c>Sky.Procedural.vk.frag</c> + <c>Tonemap.vk.frag</c>) and where the grid's lines
/// project (so the test knows which pixels must show the sky).
/// </summary>
internal static class SkyGridReference
{
    /// <summary>World-space view ray through the centre of pixel (<paramref name="x"/>, <paramref name="y"/>), as <c>skyRay</c> computes it.</summary>
    public static Vector3 Ray(in FrameData frame, int x, int y, int width, int height)
    {
        // The scene viewport is Y-flipped: NDC +y is the top row.
        var ndc = new Vector4((x + 0.5f) / width * 2f - 1f, 1f - (y + 0.5f) / height * 2f, 1f, 1f);
        var view = Vector4.Transform(ndc, frame.InverseProjection); // the shader's mul(v, M) on the uploaded matrix
        view /= view.W;
        return Vector3.Normalize(Vector3.TransformNormal(new Vector3(view.X, view.Y, view.Z), frame.InverseViewRotation));
    }

    /// <summary>The displayed sRGB colour (0..255) of the procedural sky along <paramref name="direction"/>.</summary>
    public static Vector3 SkyPixel(Sky sky, Vector3 direction, float exposure)
    {
        ArgumentNullException.ThrowIfNull(sky);
        var y = direction.Y;
        var sharpness = sky.HorizonSharpness;
        var skyColor = ColorSpace.SrgbToLinear(sky.SkyColor);
        var horizon = ColorSpace.SrgbToLinear(sky.HorizonColor);
        var ground = ColorSpace.SrgbToLinear(sky.GroundColor);

        var color = y >= 0f
            ? Vector3.Lerp(horizon, skyColor, Math.Clamp(y * sharpness, 0f, 1f))
            : Vector3.Lerp(horizon, ground, Math.Clamp(-y * sharpness, 0f, 1f));

        var sunSize = MathF.Cos(float.DegreesToRadians(sky.SunAngularRadius));
        var softEdge = MathF.Max(1f - sunSize, 0.0001f);
        var sunMask = SmoothStep(sunSize - softEdge * 0.05f, sunSize, Vector3.Dot(direction, Vector3.Normalize(sky.SunDirection)));
        color += ColorSpace.SrgbToLinear(sky.SunColor) * sky.SunIntensity * sunMask;

        return ColorSpace.LinearToSrgb(ColorSpace.AcesFitted(color * exposure)) * 255f;
    }

    /// <summary>
    /// The lines of <see cref="SceneGrid3d"/>(<paramref name="gridSize"/>) in pixels: each is clipped to the near and
    /// far planes in clip space, projected (Y-flipped viewport) and clipped to the image grown by <paramref name="margin"/>.
    /// Lines that miss the image are left out.
    /// </summary>
    public static List<(Vector2 Start, Vector2 End)> ProjectGridLines(in Matrix4x4 viewProjection, uint gridSize, int width, int height, float margin)
    {
        var lines = new List<(Vector2, Vector2)>();
        var half = (int)gridSize / 2;
        for (var i = -half; i <= half; i++)
        {
            Project(lines, viewProjection, new Vector3(half, 0, i), new Vector3(-half, 0, i), width, height, margin);  // along X
            Project(lines, viewProjection, new Vector3(i, 0, -half), new Vector3(i, 0, half), width, height, margin);  // along Z
        }

        Project(lines, viewProjection, new Vector3(0, half, 0), new Vector3(0, -half, 0), width, height, margin); // Y axis
        return lines;
    }

    /// <summary>Pixels whose centre is within <paramref name="radius"/> pixels of one of <paramref name="lines"/>.</summary>
    public static bool[] LineMask(List<(Vector2 Start, Vector2 End)> lines, int width, int height, float radius)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var mask = new bool[width * height];
        foreach (var (start, end) in lines)
            Stamp(mask, start, end, width, height, radius);
        return mask;
    }

    private static void Project(List<(Vector2, Vector2)> lines, in Matrix4x4 viewProjection, Vector3 a, Vector3 b, int width, int height, float margin)
    {
        var ca = Vector4.Transform(new Vector4(a, 1f), viewProjection);
        var cb = Vector4.Transform(new Vector4(b, 1f), viewProjection);

        // Near (z ≥ 0) and far (w − z ≥ 0) planes; x/y are bounded by the 2D clip below.
        float t0 = 0f, t1 = 1f;
        if (!ClipPlane(ca.Z, cb.Z, ref t0, ref t1) || !ClipPlane(ca.W - ca.Z, cb.W - cb.Z, ref t0, ref t1))
            return;
        var pa = ToPixel(Vector4.Lerp(ca, cb, t0), width, height);
        var pb = ToPixel(Vector4.Lerp(ca, cb, t1), width, height);

        var d = pb - pa;
        float s0 = 0f, s1 = 1f;
        var m = margin;
        if (!ClipPlane(pa.X + m, pb.X + m, ref s0, ref s1) || !ClipPlane(width + m - pa.X, width + m - pb.X, ref s0, ref s1) ||
            !ClipPlane(pa.Y + m, pb.Y + m, ref s0, ref s1) || !ClipPlane(height + m - pa.Y, height + m - pb.Y, ref s0, ref s1))
            return;
        lines.Add((pa + d * s0, pa + d * s1));
    }

    private static void Stamp(bool[] mask, Vector2 start, Vector2 end, int width, int height, float radius)
    {
        var length = Vector2.Distance(start, end);
        var steps = Math.Max(1, (int)MathF.Ceiling(length / 0.25f));
        var reach = (int)MathF.Ceiling(radius);
        for (var s = 0; s <= steps; s++)
        {
            var p = Vector2.Lerp(start, end, s / (float)steps);
            var px = (int)MathF.Floor(p.X);
            var py = (int)MathF.Floor(p.Y);
            for (var y = py - reach; y <= py + reach; y++)
            {
                if ((uint)y >= (uint)height) continue;
                for (var x = px - reach; x <= px + reach; x++)
                {
                    if ((uint)x >= (uint)width) continue;
                    if (Vector2.DistanceSquared(new Vector2(x + 0.5f, y + 0.5f), p) <= (radius + 0.25f) * (radius + 0.25f))
                        mask[y * width + x] = true;
                }
            }
        }
    }

    private static Vector2 ToPixel(Vector4 clip, int width, int height)
        => new((clip.X / clip.W + 1f) * 0.5f * width, (1f - clip.Y / clip.W) * 0.5f * height);

    /// <summary>Liang–Barsky against one plane: <paramref name="da"/>, <paramref name="db"/> are the signed distances (inside ≥ 0).</summary>
    private static bool ClipPlane(float da, float db, ref float t0, ref float t1)
    {
        if (da < 0f && db < 0f) return false;
        if (da < 0f) t0 = MathF.Max(t0, da / (da - db));
        else if (db < 0f) t1 = MathF.Min(t1, da / (da - db));
        return t0 <= t1;
    }

    private static float SmoothStep(float edge0, float edge1, float x)
    {
        var t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}

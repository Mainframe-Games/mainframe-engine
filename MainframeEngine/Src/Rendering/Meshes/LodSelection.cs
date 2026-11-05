using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// G6.4: picks a surface's level of detail by projected error. A level whose <see cref="LodRange.Error"/> (mesh units),
/// scaled by the instance's largest axis scale and projected at the instance's distance, covers at most
/// <c>threshold / bias</c> pixels may draw; the coarsest such level wins (Godot's <c>mesh_lod_threshold</c> and
/// <c>lod_bias</c>).
/// </summary>
internal static class LodSelection
{
    /// <summary>
    /// Pixels a length of 1 covers at distance 1 (perspective) or anywhere (orthographic): half the view's pixel height
    /// times the projection's vertical scale (<c>1 / tan(fovY / 2)</c>, or <c>2 / orthoHeight</c>).
    /// </summary>
    public static float PixelScale(in Matrix4x4 projection, float viewHeight) => viewHeight * 0.5f * MathF.Abs(projection.M22);

    /// <summary>True for a perspective projection (row-vector convention: w takes −z).</summary>
    public static bool IsPerspective(in Matrix4x4 projection) => projection.M34 != 0f;

    /// <summary>
    /// The error, in mesh units before scale, a level may have and still draw: <paramref name="thresholdPixels"/> /
    /// <paramref name="bias"/> pixels at <paramref name="distance"/> (perspective) or anywhere (orthographic). 0 (level 0
    /// draws) for a camera inside the bounds or a degenerate input.
    /// </summary>
    public static float AllowedError(float pixelScale, bool perspective, float distance, float maxScale, float thresholdPixels, float bias)
    {
        if (!(pixelScale > 0f) || !(maxScale > 0f) || !(bias > 0f) || !(thresholdPixels > 0f))
            return 0f;
        var pixelsPerUnit = pixelScale * maxScale;
        if (perspective)
        {
            if (!(distance > 0f))
                return 0f;
            pixelsPerUnit /= distance;
        }

        return thresholdPixels / bias / pixelsPerUnit;
    }

    /// <summary>The coarsest level (0 = the surface) whose error is at most <paramref name="allowedError"/>.</summary>
    public static int Select(LodRange[] levels, float allowedError)
    {
        var lod = 0;
        for (var l = 0; l < levels.Length; l++)
        {
            if (levels[l].Error > allowedError)
                break;
            lod = l + 1;
        }

        return lod;
    }

    /// <summary>The largest axis scale of a model matrix (lengths of its first three rows).</summary>
    public static float MaxScale(in Matrix4x4 model) =>
        MathF.Sqrt(MathF.Max(MathF.Max(
            model.M11 * model.M11 + model.M12 * model.M12 + model.M13 * model.M13,
            model.M21 * model.M21 + model.M22 * model.M22 + model.M23 * model.M23),
            model.M31 * model.M31 + model.M32 * model.M32 + model.M33 * model.M33));

    /// <summary>Distance from <paramref name="point"/> to the closest point of <paramref name="box"/> (0 inside).</summary>
    public static float Distance(Vector3 point, in Aabb box) =>
        Vector3.Distance(point, Vector3.Clamp(point, box.Min, box.Max));
}

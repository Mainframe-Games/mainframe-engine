using System.Numerics;

namespace MainframeEngine;

public static class GizmoProjection
{
    /// <summary>World → framebuffer pixel (top-left origin). False when the point is at or behind the camera plane.</summary>
    public static bool TryProject(Vector3 world, in Matrix4x4 viewProjection, Vector2 viewport, out Vector2 pixel)
    {
        var clip = Vector4.Transform(new Vector4(world, 1f), viewProjection);
        if (clip.W <= 1e-5f)
        {
            pixel = default;
            return false;
        }

        var ndc = new Vector2(clip.X / clip.W, clip.Y / clip.W);
        pixel = new Vector2((ndc.X * 0.5f + 0.5f) * viewport.X, (1f - (ndc.Y * 0.5f + 0.5f)) * viewport.Y);
        return true;
    }
}

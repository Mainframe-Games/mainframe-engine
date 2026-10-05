using System.Numerics;
using MainframeEngine;

namespace Demo;

/// <summary>Maps mouse positions (OS window points) into the root canvas (Godot: get_global_mouse_position).</summary>
public static class CanvasInput
{
    /// <summary>
    /// A window point as canvas units: scaled to framebuffer pixels by <paramref name="pixelScale"/>, then through the
    /// inverse of the viewport's stretch and camera (canvas) transforms.
    /// </summary>
    public static Vector2 WindowToCanvas(SceneViewport viewport, Vector2 windowPoint, float pixelScale)
    {
        var screen = viewport.StretchTransform * viewport.CanvasTransform;
        return screen.AffineInverse().TransformPoint(windowPoint * pixelScale);
    }

    public static Vector2 WindowToCanvas(Node node, Vector2 windowPoint) =>
        WindowToCanvas(node.Tree!.Root, windowPoint, node.Tree.Servers.Get<UiServer>()?.PixelScale ?? 1f);
}

using System.Numerics;
using SilkVulkanExamples.Vulkan.Utils;

namespace SilkVulkanExamples.Vulkan;

public class Camera
{
    public Vector3 Position { get; set; }
    public Vector3 Front { get; private set; }
    public Vector3 Up { get; }
    public float AspectRatio { get; set; }

    private float Yaw { get; set; } = -90f;
    private float Pitch { get; set; }
    private float Zoom = 45f;

    public Camera(Vector3 position, Vector3 front, Vector3 up, float aspectRatio)
    {
        Position = position;
        Front = front;
        Up = up;
        AspectRatio = aspectRatio;
    }

    public void ModifyZoom(float zoomAmount)
    {
        Zoom = Math.Clamp(Zoom - zoomAmount, 1.0f, 45f);
    }

    public void ModifyDirection(float xOffset, float yOffset)
    {
        Yaw += xOffset;
        Pitch -= yOffset;
        Pitch = Math.Clamp(Pitch, -89f, 89f);

        var dir = Vector3.Zero;
        dir.X = MathF.Cos(MathHelper.DegreesToRadians(Yaw)) * MathF.Cos(MathHelper.DegreesToRadians(Pitch));
        dir.Y = MathF.Sin(MathHelper.DegreesToRadians(Pitch));
        dir.Z = MathF.Sin(MathHelper.DegreesToRadians(Yaw)) * MathF.Cos(MathHelper.DegreesToRadians(Pitch));
        Front = Vector3.Normalize(dir);
    }

    public Matrix4x4 GetViewMatrix()
    {
        return Matrix4x4.CreateLookAt(Position, Position + Front, Up);
    }

    public Matrix4x4 GetProjectionMatrix()
    {
        // Vulkan clip space: Y is inverted, flip to correct
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(
            MathHelper.DegreesToRadians(Zoom),
            AspectRatio,
            0.1f,
            100.0f);
        // Flip Y for Vulkan (NDC Y is down)
        proj.M22 = -proj.M22;
        return proj;
    }
}

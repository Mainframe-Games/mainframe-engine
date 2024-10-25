using System.Numerics;
using SilkTest.Examples.Utils;

namespace SilkTest.Examples;

public class Camera(Vector3 position, Vector3 front, Vector3 up, float aspectRatio)
{
    public Vector3 Position { get; set; } = position;
    public Vector3 Front { get; private set; } = front;

    public Vector3 Up { get; } = up;
    public float AspectRatio { get; set; } = aspectRatio;

    private float Yaw { get; set; } = -90f;
    private float Pitch { get; set; }

    private float Zoom = 45f;

    public void ModifyZoom(float zoomAmount)
    {
        //We don't want to be able to zoom in too close or too far away so clamp to these values
        Zoom = Math.Clamp(Zoom - zoomAmount, 1.0f, 45f);
    }

    public void ModifyDirection(float xOffset, float yOffset)
    {
        Yaw += xOffset;
        Pitch -= yOffset;

        //We don't want to be able to look behind us by going over our head or under our feet so make sure it stays within these bounds
        Pitch = Math.Clamp(Pitch, -89f, 89f);

        var cameraDirection = Vector3.Zero;
        cameraDirection.X =
            MathF.Cos(MathHelper.DegreesToRadians(Yaw))
            * MathF.Cos(MathHelper.DegreesToRadians(Pitch));
        cameraDirection.Y = MathF.Sin(MathHelper.DegreesToRadians(Pitch));
        cameraDirection.Z =
            MathF.Sin(MathHelper.DegreesToRadians(Yaw))
            * MathF.Cos(MathHelper.DegreesToRadians(Pitch));

        Front = Vector3.Normalize(cameraDirection);
    }

    public Matrix4x4 GetViewMatrix()
    {
        return Matrix4x4.CreateLookAt(Position, Position + Front, Up);
    }

    public Matrix4x4 GetProjectionMatrix()
    {
        return Matrix4x4.CreatePerspectiveFieldOfView(
            MathHelper.DegreesToRadians(Zoom),
            AspectRatio,
            0.1f,
            100.0f
        );
    }
}

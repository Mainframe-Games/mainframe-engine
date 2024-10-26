using System.Numerics;

namespace Mainframe.Silk;

public class CameraPerspective : ICamera
{
    public Vector3 Position { get; set; } = new(0, 0, -10);
    public Vector3 Forward { get; set; } = -Vector3.UnitZ;
    public Vector3 Up { get; set; } = Vector3.UnitY;
    public float AspectRatio { get; set; }

    private float Yaw { get; set; } = -90f;
    private float Pitch { get; set; }

    public float FieldOfView { get; private set; } = 45f;
    
    public Matrix4x4 ViewMatrix
        => Matrix4x4.CreateLookAt(Position, Position + Forward, Up);

    public Matrix4x4 ProjectionMatrix =>
        Matrix4x4.CreatePerspectiveFieldOfView(
            Math.DegreesToRadiansF(FieldOfView),
            AspectRatio,
            0.1f,
            1000.0f
        );

    public void ModifyZoom(float zoomAmount)
    {
        //We don't want to be able to zoom in too close or too far away so clamp to these values
        FieldOfView = System.Math.Clamp(FieldOfView - zoomAmount, 1.0f, 45f);
    }

    public void ModifyDirection(float xOffset, float yOffset)
    {
        Yaw += xOffset;
        Pitch -= yOffset;

        //We don't want to be able to look behind us by going over our head or under our feet so make sure it stays within these bounds
        Pitch = System.Math.Clamp(Pitch, -89f, 89f);

        var cameraDirection = Vector3.Zero;
        cameraDirection.X =
            MathF.Cos(Math.DegreesToRadiansF(Yaw))
            * MathF.Cos(Math.DegreesToRadiansF(Pitch));
        cameraDirection.Y = MathF.Sin(Math.DegreesToRadiansF(Pitch));
        cameraDirection.Z =
            MathF.Sin(Math.DegreesToRadiansF(Yaw))
            * MathF.Cos(Math.DegreesToRadiansF(Pitch));

        Forward = Vector3.Normalize(cameraDirection);
    }
}
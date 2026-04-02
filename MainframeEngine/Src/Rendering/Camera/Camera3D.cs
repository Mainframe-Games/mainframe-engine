using System.Numerics;

namespace MainframeEngine;

public class Camera3D : ICamera
{
    public Vector3 Position { get; set; }
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
            float.DegreesToRadians(FieldOfView),
            AspectRatio,
            0.1f,
            1000.0f
        );

    public void ModifyZoom(float zoomAmount)
    {
        //We don't want to be able to zoom in too close or too far away so clamp to these values
        FieldOfView = Math.Clamp(FieldOfView - zoomAmount, 1.0f, 90f);
    }

    public void ModifyDirection(float xOffset, float yOffset)
    {
        Yaw += xOffset;
        Pitch -= yOffset;

        //We don't want to be able to look behind us by going over our head or under our feet so make sure it stays within these bounds
        Pitch = Math.Clamp(Pitch, -89f, 89f);

        var cameraDirection = Vector3.Zero;
        cameraDirection.X =
            MathF.Cos(float.DegreesToRadians(Yaw))
            * MathF.Cos(float.DegreesToRadians(Pitch));
        cameraDirection.Y = MathF.Sin( float.DegreesToRadians(Pitch));
        cameraDirection.Z =
            MathF.Sin(float.DegreesToRadians(Yaw))
            * MathF.Cos(float.DegreesToRadians(Pitch));

        Forward = Vector3.Normalize(cameraDirection);
    }
    
    /// <summary>
    /// Orients the camera to face the given world-space position.
    /// </summary>
    public virtual void LookAt(in Vector3 target)
    {
        var dir = target - Position;
        
        if (Vector3.Dot(dir, dir) < 1e-10f)
            return;
        
        Forward = Vector3.Normalize(dir);
        
        // 3D only - dont need for 2D
        Pitch = float.RadiansToDegrees(MathF.Asin(Math.Clamp(Forward.Y, -1f, 1f)));
        Yaw = float.RadiansToDegrees(MathF.Atan2(Forward.Z, Forward.X));
    }
}
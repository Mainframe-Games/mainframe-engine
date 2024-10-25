using System.Numerics;

namespace Mainframe.Silk;

public class CameraOrthographic : ICamera
{
    public Vector3 Position { get; set; } = new(0, 0, 10);
    public Vector3 Forward { get; private set; } = Vector3.UnitZ;
    
    public Vector3 Up { get; } = Vector3.UnitY;
    
    public Vector2 Size { get; set; }
    public float Zoom { get; set; } = 1f;
    
    public Matrix4x4 ViewMatrix 
        => Matrix4x4.CreateLookAt(Position, Position + Forward, Up);
    public Matrix4x4 ProjectionMatrix 
        => Matrix4x4.CreateOrthographic(Size.X * Zoom, Size.Y * Zoom, 0.1f, 1000f);
}
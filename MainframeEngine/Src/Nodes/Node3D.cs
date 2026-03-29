using System.Numerics;

namespace MainframeEngine;

public class Node3D
{
    public Vector3 Position { get; set; }
    public Vector3 Rotation { get; set; }
    public Vector3 Scale { get; set; } = Vector3.One;

    public Matrix4x4 ModelMatrix =>
        // scale
        Matrix4x4.CreateScale(Scale)
        // rotation
        * Matrix4x4.CreateRotationX(float.DegreesToRadians(Rotation.X))
        * Matrix4x4.CreateRotationY(float.DegreesToRadians(Rotation.Y))
        * Matrix4x4.CreateRotationZ(float.DegreesToRadians(Rotation.Z))
        // translation
        * Matrix4x4.CreateTranslation(Position);
}
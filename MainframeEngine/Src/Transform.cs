using System.Numerics;

namespace MainframeEngine;

public struct Transform
{
    public Vector3 Position { get; set; }
    public Vector3 Rotation { get; set; }
    public Vector3 Scale { get; set; }

    public Matrix4x4 ModelMatrix =>
        // scale
        Matrix4x4.CreateScale(Scale)
        // rotation
        * Matrix4x4.CreateRotationX(Mainframe.Math.DegreesToRadiansF(Rotation.X))
        * Matrix4x4.CreateRotationY(Mainframe.Math.DegreesToRadiansF(Rotation.Y))
        * Matrix4x4.CreateRotationZ(Mainframe.Math.DegreesToRadiansF(Rotation.Z))
        // translation
        * Matrix4x4.CreateTranslation(Position);
}
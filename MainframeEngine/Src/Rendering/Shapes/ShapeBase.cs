using System.Drawing;
using System.Numerics;
using Silk.NET.Vulkan;

namespace MainframeEngine;

public abstract class ShapeBase : IDisposable
{
    public Vector3 Position { get; set; }
    public Vector3 Rotation { get; set; }
    public Vector3 Scale { get; set; } = Vector3.One;
    public Color Color { get; set; } = Color.White;
    
    protected Matrix4x4 ModelMatrix =>
        // scale
        Matrix4x4.CreateScale(Scale)
        // rotation
        * Matrix4x4.CreateRotationX(float.DegreesToRadians(Rotation.X))
        * Matrix4x4.CreateRotationY(float.DegreesToRadians(Rotation.Y))
        * Matrix4x4.CreateRotationZ(float.DegreesToRadians(Rotation.Z))
        // translation
        * Matrix4x4.CreateTranslation(Position);

    public abstract void Draw(ICamera camera, LightEnvironment lights);

    public abstract void DrawShadow2D(CommandBuffer cb);
    public abstract void DrawShadowPoint(CommandBuffer cb, Vector3 lightPos, float lightRange);
    public abstract void Dispose();
}
using System.Drawing;
using System.Numerics;
using Silk.NET.Vulkan;

namespace MainframeEngine;

public abstract class ShapeBase : Node3D, IDisposable
{
    public Color Color { get; set; } = Color.White;
    
    public abstract void Draw(ICamera camera, LightEnvironment lights);

    public abstract void DrawShadow2D(CommandBuffer cb);
    public abstract void DrawShadowPoint(CommandBuffer cb, Vector3 lightPos, float lightRange);
    public abstract void Dispose();
}
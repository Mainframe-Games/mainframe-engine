using System.Drawing;
using System.Numerics;
using Silk.NET.Vulkan;

namespace MainframeEngine;

public abstract class ShapeBase : Node3D
{
    public Color Color { get; set; } = Color.White;
}
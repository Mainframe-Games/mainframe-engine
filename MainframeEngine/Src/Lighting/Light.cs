using System.Numerics;

namespace MainframeEngine;

public abstract class Light
{
    public Vector3 Position { get; set; }
    public Vector3 Color { get; set; } = Vector3.One;
    public float Intensity { get; set; } = 1f;
}
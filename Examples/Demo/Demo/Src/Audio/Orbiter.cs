using System.Numerics;
using MainframeEngine;

namespace Demo;

/// <summary>Moves itself (and so its children) around the parent's origin on a horizontal circle.</summary>
public sealed class Orbiter : Node3D
{
    private float _angle;

    [Export(Range = "0,50,0.1")] public float Radius { get; set; } = 6f;
    [Export(Range = "-360,360,1")] public float DegreesPerSecond { get; set; } = 40f;

    protected override void OnProcess(in GameTime gameTime)
    {
        _angle += DegreesPerSecond * gameTime.DeltaTime * MathF.PI / 180f;
        Position = new Vector3(MathF.Cos(_angle) * Radius, Position.Y, MathF.Sin(_angle) * Radius);
    }
}

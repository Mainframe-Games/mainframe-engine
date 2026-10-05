using System.Numerics;
using MainframeEngine;

namespace Demo;

/// <summary>Circles the origin at <see cref="Radius"/>, looking at <see cref="Target"/>.</summary>
public sealed class OrbitCamera : Camera3D
{
    private float _angle;

    [Export(Range = "1,100,0.1")] public float Radius { get; set; } = 11f;
    [Export(Range = "0,50,0.1")] public float Height { get; set; } = 4.5f;
    [Export(Range = "-90,90,0.5")] public float DegreesPerSecond { get; set; } = 6f;
    [Export] public Vector3 Target { get; set; } = new(0, 1, 0);
    [Export] public bool Paused { get; set; }

    protected override void OnReady() => Place();

    protected override void OnProcess(in GameTime gameTime)
    {
        if (!Paused)
            _angle += DegreesPerSecond * gameTime.DeltaTime * MathF.PI / 180f;
        Place();
    }

    private void Place()
    {
        Position = new Vector3(MathF.Sin(_angle) * Radius, Height, MathF.Cos(_angle) * Radius);
        LookAt(Target);
    }
}

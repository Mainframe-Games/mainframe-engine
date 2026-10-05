using MainframeEngine;

namespace Demo;

/// <summary>Rotates, so its children circle its position.</summary>
public sealed class Orbit2D : Node2D
{
    [Export(Range = "-360,360,1")] public float DegreesPerSecond { get; set; } = 30f;
    [Export] public bool Paused { get; set; }

    protected override void OnProcess(in GameTime gameTime)
    {
        if (!Paused) RotationDegrees += DegreesPerSecond * gameTime.DeltaTime;
    }
}

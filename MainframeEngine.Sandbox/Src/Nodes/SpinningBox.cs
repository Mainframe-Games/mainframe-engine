using System.Numerics;

namespace MainframeEngine.Sandbox;

/// <summary>A box that turns at a constant rate (Euler degrees per second).</summary>
public sealed class SpinningBox : Box3d
{
    [Export]
    public Vector3 DegreesPerSecond { get; set; } = new(20, 20, 0);

    protected override void OnProcess(in GameTime gameTime)
    {
        RotationDegrees += DegreesPerSecond * gameTime.DeltaTime;
    }
}

using System.Numerics;
using MainframeEngine;

namespace Demo;

/// <summary>Sweeps left and right across the screen (canvas units), carrying its children.</summary>
public sealed class Sweeper2D : Node2D
{
    private float _time;

    [Export(Range = "0,2000,1")] public float HalfWidth { get; set; } = 520f;
    [Export(Range = "0.01,4,0.01")] public float Speed { get; set; } = 0.35f;

    protected override void OnProcess(in GameTime gameTime)
    {
        _time += gameTime.DeltaTime;
        Position = new Vector2(MathF.Sin(_time * MathF.Tau * Speed) * HalfWidth, Position.Y);
    }
}

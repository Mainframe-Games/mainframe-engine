using System.Numerics;
using MainframeEngine;

namespace Demo;

/// <summary>A capsule that slides back and forth along X, shoving crates (CharacterBody3D.MoveAndSlide).</summary>
public sealed class Pusher3D : CharacterBody3D
{
    private float _time;

    [Export(Range = "0,20,0.1")] public float Amplitude { get; set; } = 4.5f;

    protected override void OnPhysicsProcess(float delta)
    {
        _time += delta;
        Velocity = new Vector3(MathF.Cos(_time * 0.6f) * Amplitude * 0.6f, Velocity.Y - 9.81f * delta, 0);
        MoveAndSlide();
    }
}

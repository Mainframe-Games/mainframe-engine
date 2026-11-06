using System.Numerics;
using MainframeEngine;

namespace Demo;

/// <summary>Walks back and forth along X and drains and refills the health bar of its nameplate marker.</summary>
public sealed class WorldUiWalker : Node3D
{
    private Vector3 _start;
    private float _time;
    private UiMarker3D? _plate;

    [Export] public float Range { get; set; } = 3f;

    [Export] public float Speed { get; set; } = 1f;

    protected override void OnReady()
    {
        _start = Position;
        _plate = GetNodeOrNull<UiMarker3D>("Nameplate");
    }

    protected override void OnProcess(in GameTime gameTime)
    {
        _time += gameTime.DeltaTime;
        Position = _start + new Vector3(MathF.Sin(_time * Speed / Range) * Range, 0, 0);
        if (_plate is not null)
            _plate.Value = 0.55f + 0.45f * MathF.Cos(_time * 0.35f * Speed);
    }
}

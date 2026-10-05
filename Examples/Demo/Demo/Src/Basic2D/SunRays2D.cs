using System.Numerics;
using MainframeEngine;

namespace Demo;

/// <summary>Rays that slowly rotate and pulse around the node's origin, drawn with <see cref="CanvasItem.DrawLine"/>.</summary>
public sealed class SunRays2D : Node2D
{
    private float _time;

    [Export(Range = "4,64,1")] public int Rays { get; set; } = 18;
    [Export(Range = "0,500,1")] public float Inner { get; set; } = 90f;
    [Export(Range = "0,800,1")] public float Outer { get; set; } = 180f;
    [Export] public Vector4 Color { get; set; } = new(1f, 0.85f, 0.4f, 0.55f);
    [Export] public bool Paused { get; set; }

    protected override void OnProcess(in GameTime gameTime)
    {
        if (Paused) return;
        _time += gameTime.DeltaTime;
        QueueRedraw();
    }

    protected override void OnDraw()
    {
        for (var i = 0; i < Rays; i++)
        {
            var angle = i * MathF.Tau / Rays + _time * 0.15f;
            var length = Outer + MathF.Sin(_time * 2f + i) * 18f;
            var dir = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            DrawLine(dir * Inner, dir * length, Color, width: 6f, antialiased: true);
        }
    }
}

using System.Numerics;
using MainframeEngine;

namespace Demo;

/// <summary>A debug-line circle (the emitter's audible range) around this node.</summary>
public sealed class DistanceRing : Node3D
{
    [Export(Range = "0,500,0.1")] public float Radius { get; set; } = 10f;
    [Export] public Vector4 Color { get; set; } = new(0.4f, 0.7f, 1f, 0.6f);

    protected override void OnProcess(in GameTime gameTime) =>
        GetViewport()?.DebugLines.AddCircle(Transform3D.Identity, GlobalPosition, Vector3.UnitX, Vector3.UnitZ, Radius, Color);
}

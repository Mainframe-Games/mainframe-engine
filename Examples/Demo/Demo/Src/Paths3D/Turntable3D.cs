using System.Numerics;
using MainframeEngine;

namespace Demo;

/// <summary>An <see cref="AnimatableBody3D"/> turning about Y on the physics tick: whoever stands on it turns with it (G13.5).</summary>
public sealed class Turntable3D : AnimatableBody3D
{
    [Export(Range = "-360,360,1")] public float DegreesPerSecond { get; set; } = 30f;

    /// <summary>Multiplies <see cref="DegreesPerSecond"/> (the panel's speed).</summary>
    public float SpeedScale { get; set; } = 1f;

    protected override void OnPhysicsProcess(float delta) =>
        Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, float.DegreesToRadians(DegreesPerSecond * SpeedScale * delta)) * Rotation;
}

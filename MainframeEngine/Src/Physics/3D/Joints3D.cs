using System.Numerics;
using Jitter2.Dynamics.Constraints;
using Jitter2.LinearMath;

namespace MainframeEngine;

/// <summary>
/// Pins two bodies together at the joint's position, letting them turn freely about it (Godot's <c>PinJoint3D</c>; a
/// Jitter2 ball socket).
/// </summary>
[EditorIcon("point", Family = EditorIconFamily.Physics)]
public class PinJoint3D : Joint3D
{
    internal override void CreateConstraints(JointRecord3D record) => record.Socket = record.Space.CreateBallSocket(record);

    internal override void DrawDebugShape(DebugLines lines, in Transform3D frame, Vector3 anchorB, float value, Vector4 color)
    {
        DrawAnchors(lines, frame, anchorB, color);
        lines.AddSphere(frame, 0.08f, color);
    }
}

/// <summary>
/// A hinge (Godot's <c>HingeJoint3D</c>): the bodies turn about the joint's local Z axis through its position. Optional
/// angle limits, measured from the pose the joint was created in, and a motor; both change live.
/// </summary>
[EditorIcon("door", Family = EditorIconFamily.Physics)]
public class HingeJoint3D : Joint3D
{
    /// <summary>Limit the angle to <see cref="LowerAngle"/>…<see cref="UpperAngle"/>.</summary>
    [ExportGroup("Limit")]
    [Export]
    public bool UseLimit
    {
        get;
        set => Live(ref field, value);
    }

    /// <summary>The lowest angle in degrees (from the authored pose, right-handed about the joint's Z).</summary>
    [Export(Range = "-180,180,0.1")]
    public float LowerAngleDegrees
    {
        get => float.RadiansToDegrees(LowerAngle);
        set => LowerAngle = float.DegreesToRadians(value);
    }

    /// <summary>The highest angle in degrees.</summary>
    [Export(Range = "-180,180,0.1")]
    public float UpperAngleDegrees
    {
        get => float.RadiansToDegrees(UpperAngle);
        set => UpperAngle = float.DegreesToRadians(value);
    }

    /// <summary>The lowest angle in radians (default −90°).</summary>
    public float LowerAngle
    {
        get;
        set => Live(ref field, Math.Clamp(value, -MathF.PI, MathF.PI));
    } = -MathF.PI / 2;

    /// <summary>The highest angle in radians (default 90°).</summary>
    public float UpperAngle
    {
        get;
        set => Live(ref field, Math.Clamp(value, -MathF.PI, MathF.PI));
    } = MathF.PI / 2;

    /// <summary>Drive body B about the axis at <see cref="MotorTargetVelocity"/>.</summary>
    [ExportGroup("Motor")]
    [Export]
    public bool MotorEnabled
    {
        get;
        set => Live(ref field, value);
    }

    /// <summary>The motor's target speed in radians per second (B relative to A, right-handed about Z).</summary>
    [Export(Range = "-100,100,0.01")]
    public float MotorTargetVelocity
    {
        get;
        set => Live(ref field, value);
    }

    /// <summary>The most torque the motor applies (N·m).</summary>
    [Export(Range = "0,100000,0.1")]
    public float MotorMaxTorque
    {
        get;
        set => Live(ref field, MathF.Max(0f, value));
    } = 100f;

    /// <summary>The current angle in radians: body B relative to A about the axis, from the authored pose (0 when inactive).</summary>
    public float GetAngle() => Record is { HasJoint: true, Hinge: { IsValid: true } hinge } ? (float)hinge.Angle : 0f;

    internal override void CreateConstraints(JointRecord3D record)
    {
        record.Socket = record.Space.CreateBallSocket(record);
        record.Hinge = record.Space.CreateHingeAngle(record, Vector3.UnitZ);
        // The motor always exists: off is a maximum torque of 0, so toggling it never re-creates the joint.
        record.AngularMotor = record.Space.CreateAngularMotor(record, Vector3.UnitZ);
    }

    internal override void ApplyLive(JointRecord3D record)
    {
        if (record.Hinge is { IsValid: true } hinge)
            hinge.Limit = Limit(UseLimit, LowerAngle, UpperAngle);
        if (record.AngularMotor is { IsValid: true } motor)
        {
            motor.TargetVelocity = MotorTargetVelocity;
            motor.MaximumForce = MotorEnabled ? MotorMaxTorque : 0f;
        }
    }

    internal override float GetDebugValue(JointRecord3D record) => GetAngle();

    internal override void DrawDebugShape(DebugLines lines, in Transform3D frame, Vector3 anchorB, float value, Vector4 color)
    {
        DrawAnchors(lines, frame, anchorB, color);
        lines.AddLine(frame, -Vector3.UnitZ * 0.3f, Vector3.UnitZ * 0.3f, color);
        const float radius = 0.2f;
        if (UseLimit)
        {
            var lower = MathF.Min(LowerAngle, UpperAngle);
            var upper = MathF.Max(LowerAngle, UpperAngle);
            DrawArc(lines, frame, Vector3.UnitX, Vector3.UnitY, radius, lower, upper, color);
            lines.AddLine(frame, Vector3.Zero, new Vector3(MathF.Cos(lower), MathF.Sin(lower), 0) * radius, color);
            lines.AddLine(frame, Vector3.Zero, new Vector3(MathF.Cos(upper), MathF.Sin(upper), 0) * radius, color);
        }
        else
        {
            lines.AddCircle(frame, Vector3.Zero, Vector3.UnitX, Vector3.UnitY, radius, color);
        }

        lines.AddLine(frame, Vector3.Zero, new Vector3(MathF.Cos(value), MathF.Sin(value), 0) * radius * 1.3f, color);
    }

    internal static AngularLimit Limit(bool use, float lower, float upper) =>
        use ? new AngularLimit(JAngle.FromRadian(MathF.Min(lower, upper)), JAngle.FromRadian(MathF.Max(lower, upper))) : AngularLimit.Full;
}

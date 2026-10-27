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
/// angle limits, measured from the pose the joint was created in, a motor and a spring; all of them change live.
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

    /// <summary>Pull the hinge toward <see cref="SpringTargetAngle"/> (a PD spring the engine applies each step).</summary>
    [ExportGroup("Spring")]
    [Export]
    public bool SpringEnabled
    {
        get;
        set => Live(ref field, value);
    }

    /// <summary>The spring's frequency in Hz: higher is stiffer (clamped to a quarter of the physics tick rate).</summary>
    [Export(Range = "0,60,0.01")]
    public float SpringFrequency
    {
        get;
        set => Live(ref field, MathF.Max(0f, value));
    } = 2f;

    /// <summary>The spring's damping ratio: 0 swings forever, 1 settles without overshoot.</summary>
    [Export(Range = "0,10,0.01")]
    public float SpringDampingRatio
    {
        get;
        set => Live(ref field, MathF.Max(0f, value));
    } = 0.5f;

    /// <summary>The angle the spring pulls toward, in degrees from the authored pose.</summary>
    [Export(Range = "-180,180,0.1")]
    public float SpringTargetAngleDegrees
    {
        get => float.RadiansToDegrees(SpringTargetAngle);
        set => SpringTargetAngle = float.DegreesToRadians(value);
    }

    /// <summary>The angle the spring pulls toward in radians.</summary>
    public float SpringTargetAngle
    {
        get;
        set => Live(ref field, value);
    }

    /// <summary>The current angle in radians: body B relative to A about the axis, from the authored pose (0 when inactive).</summary>
    public float GetAngle() => Record is { HasJoint: true, Hinge: { IsValid: true } hinge } ? (float)hinge.Angle : 0f;

    internal override bool HasSpring => SpringEnabled && SpringFrequency > 0f;

    internal override void ApplySpring(JointRecord3D record, float delta)
    {
        record.GetFrameB(out var anchor, out var frame);
        PhysicsSpace3D.ApplyAngularSpring(record, Vector3.Transform(Vector3.UnitZ, frame), anchor, GetAngle() - SpringTargetAngle, SpringFrequency,
            SpringDampingRatio, delta);
    }

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

/// <summary>
/// A slider (Godot's <c>SliderJoint3D</c>; a Jitter2 prismatic joint): body B's anchor moves along the joint's local X
/// axis, fixed in body A. Optional distance limits, a motor and a spring change live; <see cref="AllowTwist"/> lets B
/// also turn about the axis.
/// </summary>
[EditorIcon("arrows-right-left", Family = EditorIconFamily.Physics)]
public class SliderJoint3D : Joint3D
{
    /// <summary>B may turn about the slide axis (a cylinder joint); off keeps the relative rotation. Re-creates the joint.</summary>
    [Export]
    public bool AllowTwist
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            if (Record is { } record)
                record.Space.MarkJointDirty(record, recapture: false);
        }
    }

    /// <summary>Limit the position to <see cref="LowerDistance"/>…<see cref="UpperDistance"/>.</summary>
    [ExportGroup("Limit")]
    [Export]
    public bool UseLimit
    {
        get;
        set => Live(ref field, value);
    }

    /// <summary>The lowest position along the axis in metres (from the authored pose).</summary>
    [Export(Range = "-1000,1000,0.01")]
    public float LowerDistance
    {
        get;
        set => Live(ref field, value);
    } = -1f;

    /// <summary>The highest position along the axis in metres.</summary>
    [Export(Range = "-1000,1000,0.01")]
    public float UpperDistance
    {
        get;
        set => Live(ref field, value);
    } = 1f;

    /// <summary>Drive body B along the axis at <see cref="MotorTargetVelocity"/>.</summary>
    [ExportGroup("Motor")]
    [Export]
    public bool MotorEnabled
    {
        get;
        set => Live(ref field, value);
    }

    /// <summary>The motor's target speed in metres per second (B relative to A, along +X).</summary>
    [Export(Range = "-100,100,0.01")]
    public float MotorTargetVelocity
    {
        get;
        set => Live(ref field, value);
    }

    /// <summary>The most force the motor applies (N).</summary>
    [Export(Range = "0,100000,0.1")]
    public float MotorMaxForce
    {
        get;
        set => Live(ref field, MathF.Max(0f, value));
    } = 100f;

    /// <summary>Pull B toward <see cref="SpringTargetPosition"/> (a PD spring the engine applies each step).</summary>
    [ExportGroup("Spring")]
    [Export]
    public bool SpringEnabled
    {
        get;
        set => Live(ref field, value);
    }

    /// <summary>The spring's frequency in Hz (clamped to a quarter of the physics tick rate).</summary>
    [Export(Range = "0,60,0.01")]
    public float SpringFrequency
    {
        get;
        set => Live(ref field, MathF.Max(0f, value));
    } = 2f;

    /// <summary>The spring's damping ratio.</summary>
    [Export(Range = "0,10,0.01")]
    public float SpringDampingRatio
    {
        get;
        set => Live(ref field, MathF.Max(0f, value));
    } = 0.5f;

    /// <summary>The position the spring pulls toward, in metres along the axis.</summary>
    [Export(Range = "-1000,1000,0.01")]
    public float SpringTargetPosition
    {
        get;
        set => Live(ref field, value);
    }

    /// <summary>B's position along the axis in metres, from the authored pose (0 when inactive).</summary>
    public float GetPosition() => Record is { HasJoint: true, Line: { IsValid: true } line } ? line.Distance : 0f;

    internal override void CreateConstraints(JointRecord3D record)
    {
        record.Line = record.Space.CreatePointOnLine(record, Vector3.UnitX);
        if (AllowTwist)
            record.Hinge = record.Space.CreateHingeAngle(record, Vector3.UnitX);
        else
            record.Fixed = record.Space.CreateFixedAngle(record);
        record.LinearMotor = record.Space.CreateLinearMotor(record, Vector3.UnitX);
    }

    internal override void ApplyLive(JointRecord3D record)
    {
        if (record.Line is { IsValid: true } line)
        {
            ref var data = ref line.Data;
            data.Min = UseLimit ? MathF.Min(LowerDistance, UpperDistance) : float.NegativeInfinity;
            data.Max = UseLimit ? MathF.Max(LowerDistance, UpperDistance) : float.PositiveInfinity;
        }

        if (record.LinearMotor is { IsValid: true } motor)
        {
            motor.TargetVelocity = MotorTargetVelocity;
            motor.MaximumForce = MotorEnabled ? MotorMaxForce : 0f;
        }
    }

    internal override bool HasSpring => SpringEnabled && SpringFrequency > 0f;

    internal override void ApplySpring(JointRecord3D record, float delta)
    {
        record.GetFrameA(out var a, out var frame);
        record.GetFrameB(out var b, out _);
        PhysicsSpace3D.ApplyLinearSpring(record, Vector3.Transform(Vector3.UnitX, frame), GetPosition() - SpringTargetPosition, a, b,
            SpringFrequency, SpringDampingRatio, delta);
    }

    internal override bool IsStretched(JointRecord3D record, Vector3 anchorA, Vector3 anchorB) => false;

    internal override float GetDebugValue(JointRecord3D record) => GetPosition();

    internal override void DrawDebugShape(DebugLines lines, in Transform3D frame, Vector3 anchorB, float value, Vector4 color)
    {
        var lower = UseLimit ? MathF.Min(LowerDistance, UpperDistance) : -0.5f;
        var upper = UseLimit ? MathF.Max(LowerDistance, UpperDistance) : 0.5f;
        lines.AddLine(frame, Vector3.UnitX * lower, Vector3.UnitX * upper, color);
        if (UseLimit)
        {
            lines.AddLine(frame, new Vector3(lower, -0.08f, 0), new Vector3(lower, 0.08f, 0), color);
            lines.AddLine(frame, new Vector3(upper, -0.08f, 0), new Vector3(upper, 0.08f, 0), color);
        }

        lines.AddBox(Transform3D.FromTrs(frame.TransformPoint(Vector3.UnitX * value), frame.Basis.GetRotation(), Vector3.One),
            new Vector3(0.04f), color);
    }
}

/// <summary>
/// A ball joint with a swing cone and a twist range (Godot's <c>ConeTwistJoint3D</c>): body B's local X axis stays
/// within <see cref="SwingSpanDegrees"/> of A's, and its twist about it within ±<see cref="TwistSpanDegrees"/> of the
/// authored pose. Shoulders, hips, necks. Both spans change live.
/// </summary>
[EditorIcon("ripple", Family = EditorIconFamily.Physics)]
public class ConeTwistJoint3D : Joint3D
{
    /// <summary>How far B's X axis may swing from A's, in degrees (180: no limit).</summary>
    [Export(Range = "0,180,0.1")]
    public float SwingSpanDegrees
    {
        get => float.RadiansToDegrees(SwingSpan);
        set => SwingSpan = float.DegreesToRadians(value);
    }

    /// <summary>How far B may twist about its X axis either way, in degrees (180: no limit).</summary>
    [Export(Range = "0,180,0.1")]
    public float TwistSpanDegrees
    {
        get => float.RadiansToDegrees(TwistSpan);
        set => TwistSpan = float.DegreesToRadians(value);
    }

    /// <summary>The swing span in radians (default 45°).</summary>
    public float SwingSpan
    {
        get;
        set => Live(ref field, Math.Clamp(value, 0f, MathF.PI));
    } = MathF.PI / 4;

    /// <summary>The twist span in radians (default 180°: free).</summary>
    public float TwistSpan
    {
        get;
        set => Live(ref field, Math.Clamp(value, 0f, MathF.PI));
    } = MathF.PI;

    /// <summary>The angle between A's and B's X axes in radians (0 when inactive).</summary>
    public float GetSwingAngle() => Record is { HasJoint: true, Cone: { IsValid: true } cone } ? (float)cone.Angle : 0f;

    /// <summary>B's twist about the axis in radians, from the authored pose (0 when inactive).</summary>
    public float GetTwistAngle() => Record is { HasJoint: true, Twist: { IsValid: true } twist } ? (float)twist.Angle : 0f;

    internal override void CreateConstraints(JointRecord3D record)
    {
        record.Socket = record.Space.CreateBallSocket(record);
        record.Cone = record.Space.CreateConeLimit(record, Vector3.UnitX);
        record.Twist = record.Space.CreateTwistAngle(record, Vector3.UnitX);
    }

    internal override void ApplyLive(JointRecord3D record)
    {
        // A span of 180° is no limit: the constraint is switched off rather than asked to hold at ±π.
        const float free = MathF.PI - 1e-3f;
        if (record.Cone is { IsValid: true } cone)
        {
            cone.Limit = new AngularLimit(JAngle.FromRadian(0f), JAngle.FromRadian(Math.Clamp(SwingSpan, 1e-3f, free)));
            cone.IsEnabled = !record.Disabled && SwingSpan < free;
        }

        if (record.Twist is { IsValid: true } twist)
        {
            var span = MathF.Min(TwistSpan, free);
            twist.Limit = new AngularLimit(JAngle.FromRadian(-span), JAngle.FromRadian(span));
            twist.IsEnabled = !record.Disabled && TwistSpan < free;
        }
    }

    internal override void DrawDebugShape(DebugLines lines, in Transform3D frame, Vector3 anchorB, float value, Vector4 color)
    {
        DrawAnchors(lines, frame, anchorB, color);
        const float length = 0.3f;
        lines.AddLine(frame, Vector3.Zero, Vector3.UnitX * length * 1.3f, color);
        var swing = MathF.Min(SwingSpan, MathF.PI / 2 - 0.05f);
        var (sin, cos) = MathF.SinCos(swing);
        var rim = cos * length;
        var radius = sin * length;
        lines.AddCircle(frame, Vector3.UnitX * rim, Vector3.UnitY, Vector3.UnitZ, radius, color);
        lines.AddLine(frame, Vector3.Zero, new Vector3(rim, radius, 0), color);
        lines.AddLine(frame, Vector3.Zero, new Vector3(rim, -radius, 0), color);
        lines.AddLine(frame, Vector3.Zero, new Vector3(rim, 0, radius), color);
        lines.AddLine(frame, Vector3.Zero, new Vector3(rim, 0, -radius), color);
        if (TwistSpan < MathF.PI - 1e-3f)
            DrawArc(lines, frame, Vector3.UnitY, Vector3.UnitZ, length * 0.4f, -TwistSpan, TwistSpan, color);
    }
}

/// <summary>
/// A rope or a spring between the joint's position on body A and <see cref="AnchorB"/> on body B. The anchors stay
/// <see cref="MinLength"/>…<see cref="MaxLength"/> apart (a rope: slack up to the authored distance). With a
/// <see cref="SpringFrequency"/> the joint also pulls toward <see cref="RestLength"/>. Everything but the anchor is live.
/// </summary>
[EditorIcon("ruler-2", Family = EditorIconFamily.Physics)]
public class DistanceJoint3D : Joint3D
{
    /// <summary>Body B's anchor in the joint's local space. Changing it re-creates the joint like <see cref="Joint3D.Rebuild"/> (clears <see cref="Joint3D.IsBroken"/>).</summary>
    [Export]
    public Vector3 AnchorB
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            Rebuild();
        }
    } = new(0, -1, 0);

    /// <summary>The shortest the anchors get (m); 0 = no minimum (slack).</summary>
    [Export(Range = "0,1000,0.01")]
    public float MinLength
    {
        get;
        set => Live(ref field, MathF.Max(0f, value));
    }

    /// <summary>The longest the anchors get (m); 0 = the authored distance for a rope, no maximum for a spring.</summary>
    [Export(Range = "0,1000,0.01")]
    public float MaxLength
    {
        get;
        set => Live(ref field, MathF.Max(0f, value));
    }

    /// <summary>The spring's frequency in Hz; 0 = no spring (a rope or a rod).</summary>
    [ExportGroup("Spring")]
    [Export(Range = "0,60,0.01")]
    public float SpringFrequency
    {
        get;
        set => Live(ref field, MathF.Max(0f, value));
    }

    /// <summary>The spring's damping ratio.</summary>
    [Export(Range = "0,10,0.01")]
    public float SpringDampingRatio
    {
        get;
        set => Live(ref field, MathF.Max(0f, value));
    } = 0.5f;

    /// <summary>The length the spring pulls toward (m); 0 = the authored distance.</summary>
    [Export(Range = "0,1000,0.01")]
    public float RestLength
    {
        get;
        set => Live(ref field, MathF.Max(0f, value));
    }

    /// <summary>The current distance between the anchors in metres (0 when inactive).</summary>
    public float GetLength() => GetSeparation();

    internal override Vector3 GetAnchorB(in Transform3D pose) => pose.TransformPoint(AnchorB);

    internal override void CreateConstraints(JointRecord3D record) => record.Distance = record.Space.CreateDistanceLimit(record);

    internal override void ApplyLive(JointRecord3D record)
    {
        if (record.Distance is not { IsValid: true } distance)
            return;
        var max = Maximum(record);
        PhysicsSpace3D.SetDistanceLimit(distance, MathF.Min(MinLength, max), max);
    }

    private float Maximum(JointRecord3D record) =>
        MaxLength > 0f ? MaxLength : SpringFrequency > 0f ? float.PositiveInfinity : record.AuthoredLength;

    internal override bool HasSpring => SpringFrequency > 0f;

    internal override void ApplySpring(JointRecord3D record, float delta)
    {
        record.GetFrameA(out var a, out _);
        record.GetFrameB(out var b, out _);
        var offset = b - a;
        var length = offset.Length();
        if (length < 1e-5f)
            return;
        var rest = RestLength > 0f ? RestLength : record.AuthoredLength;
        PhysicsSpace3D.ApplyLinearSpring(record, offset / length, length - rest, a, b, SpringFrequency, SpringDampingRatio, delta);
    }

    internal override bool IsStretched(JointRecord3D record, Vector3 anchorA, Vector3 anchorB) =>
        Vector3.Distance(anchorA, anchorB) > Maximum(record) + 0.05f;

    internal override void DrawDebugShape(DebugLines lines, in Transform3D frame, Vector3 anchorB, float value, Vector4 color)
    {
        DrawAnchors(lines, frame, anchorB, color);
        if (SpringFrequency <= 0f || Vector3.DistanceSquared(frame.Origin, anchorB) <= 1e-6f)
            return; // a rope is the plain segment DrawAnchors drew
        // A spring is a zigzag along the segment.
        var axis = anchorB - frame.Origin;
        var side = Vector3.Cross(axis, MathF.Abs(axis.Y) < 0.9f * axis.Length() ? Vector3.UnitY : Vector3.UnitX);
        side = Vector3.Normalize(side) * 0.05f;
        const int turns = 8;
        var previous = frame.Origin;
        for (var i = 1; i <= turns; i++)
        {
            var next = frame.Origin + axis * ((i - 0.5f) / turns) + (i % 2 == 0 ? side : -side);
            lines.AddLine(previous, next, color);
            previous = next;
        }

        lines.AddLine(previous, anchorB, color);
    }
}

/// <summary>
/// Glues two bodies together at the authored relative pose (a Jitter2 weld: a ball socket and a fixed angle). Usually
/// with a <see cref="Joint3D.BreakForce"/> for breakable props.
/// </summary>
[EditorIcon("lock", Family = EditorIconFamily.Physics)]
public class WeldJoint3D : Joint3D
{
    internal override void CreateConstraints(JointRecord3D record)
    {
        record.Socket = record.Space.CreateBallSocket(record);
        record.Fixed = record.Space.CreateFixedAngle(record);
    }

    internal override void DrawDebugShape(DebugLines lines, in Transform3D frame, Vector3 anchorB, float value, Vector4 color)
    {
        DrawAnchors(lines, frame, anchorB, color);
        lines.AddBox(frame, new Vector3(0.06f), color);
    }
}

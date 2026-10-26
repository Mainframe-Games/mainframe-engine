using System.Numerics;
using Box2D.NET;
using static Box2D.NET.B2DistanceJoints;
using static Box2D.NET.B2Joints;
using static Box2D.NET.B2PrismaticJoints;
using static Box2D.NET.B2RevoluteJoints;
using static Box2D.NET.B2WeldJoints;
using static Box2D.NET.B2WheelJoints;

namespace MainframeEngine;

/// <summary>
/// Base of the 2D joints (Godot's <c>Joint2D</c>, ADR 0201): connects the <see cref="PhysicsBody2D"/>s at
/// <see cref="NodeA"/> and <see cref="NodeB"/> (an empty path is the static world) at the joint's position. The joint is
/// created at the next physics flush after both bodies are in the space, so the paths may be set before or after the
/// bodies enter the tree. Changing a path re-creates it there. A body that leaves the space drops its joints; they come
/// back, at the authored frames, when it returns. Softness is Box2D's: <see cref="ConstraintHertz"/> and
/// <see cref="ConstraintDampingRatio"/>, both live. Box2D has no joint enable flag: <see cref="Enabled"/> off (or a
/// disabled node) destroys the joint and on re-creates it at the authored frames.
/// </summary>
[EditorIcon("link", Family = EditorIconFamily.Physics)]
public abstract class Joint2D : Node2D
{
    internal JointRecord2D? Record { get; set; }

    /// <summary>The first body (empty: the static world).</summary>
    [Export(NodeType = typeof(PhysicsBody2D))]
    public NodePath NodeA
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            Rebuild();
        }
    }

    /// <summary>The second body (empty: the static world).</summary>
    [Export(NodeType = typeof(PhysicsBody2D))]
    public NodePath NodeB
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            Rebuild();
        }
    }

    /// <summary>Off: the joint is removed from Box2D (its frames are kept, so turning it on joins the bodies as authored).</summary>
    [Export]
    public bool Enabled
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
    } = true;

    /// <summary>The two bodies don't collide with each other (Godot's default).</summary>
    [Export]
    public bool DisableCollision
    {
        get;
        set => Live(ref field, value);
    } = true;

    /// <summary>Stiffness of the joint's constraint in Hz (Box2D's default 60): lower is softer, stretchier.</summary>
    [Export(Range = "0.1,240,0.1")]
    public float ConstraintHertz
    {
        get;
        set => Live(ref field, value);
    } = 60f;

    /// <summary>Damping of the joint's constraint (Box2D's default 2): how fast a stretched joint settles.</summary>
    [Export(Range = "0,20,0.01")]
    public float ConstraintDampingRatio
    {
        get;
        set => Live(ref field, value);
    } = 2f;

    /// <summary>The joint breaks when its force exceeds this (kg·px/s², the 2D pixel unit); 0 = never.</summary>
    [ExportGroup("Breaking")]
    [Export(Range = "0,100000000,1")]
    public float BreakForce
    {
        get;
        set => Live(ref field, value);
    }

    /// <summary>The joint breaks when its torque exceeds this (kg·px²/s²); 0 = never.</summary>
    [Export(Range = "0,100000000,1")]
    public float BreakTorque
    {
        get;
        set => Live(ref field, value);
    }

    /// <summary>The joint broke (<see cref="BreakForce"/>/<see cref="BreakTorque"/>), after the step, on the main thread.</summary>
    [Signal]
    public event Action? Broken;

    /// <summary>The joined first body (null: the world, or not created).</summary>
    public PhysicsBody2D? BodyA => Record?.A?.Node as PhysicsBody2D;

    /// <summary>The joined second body (null: the world, or not created).</summary>
    public PhysicsBody2D? BodyB => Record?.B?.Node as PhysicsBody2D;

    /// <summary>The joint exists in its space (both bodies are there, it is enabled and not broken).</summary>
    public bool IsActive => Record is { HasJoint: true };

    /// <summary>The joint broke; it stays broken until <see cref="Rebuild"/>.</summary>
    public bool IsBroken => Record is { Broken: true };

    /// <summary>The force the joint applied in the last step (kg·px/s²); when broken, the force that broke it.</summary>
    public float GetAppliedForce() => Record is { } r ? r.Broken ? r.BrokenForce : r.Space.GetJointForce(r).Length() : 0f;

    /// <summary>The torque the joint applied in the last step (kg·px²/s²); when broken, the torque that broke it.</summary>
    public float GetAppliedTorque() => Record is { } r ? r.Broken ? r.BrokenTorque : MathF.Abs(r.Space.GetJointTorque(r)) : 0f;

    /// <summary>How far apart the joint's two anchors are in pixels (0 when it holds exactly).</summary>
    public float GetSeparation() => Record is { } r ? r.Space.GetJointSeparation(r) : 0f;

    /// <summary>Re-creates the joint at the next physics flush from its current pose, and clears <see cref="IsBroken"/>.</summary>
    public void Rebuild()
    {
        if (Record is not { } record)
            return;
        record.Broken = false;
        record.Space.MarkJointDirty(record, recapture: true);
    }

    protected override void OnEnterTree()
    {
        base.OnEnterTree();
        Record = PhysicsServer2D.For(Tree!).AddJoint(this);
    }

    protected override void OnExitTree()
    {
        if (Record is { } record)
            record.Space.RemoveJoint(record);
        Record = null;
        base.OnExitTree();
    }

    private protected override void OnDisabledChanged(bool disabled)
    {
        if (Record is { } record)
            record.Space.MarkJointDirty(record, recapture: false);
    }

    /// <summary>Enabled and not under a disabled node: the joint should exist in Box2D.</summary>
    internal bool IsEffectivelyEnabled => Enabled && ResolvedProcessMode != ProcessMode.Disabled;

    internal void EmitBroken() => Broken?.Invoke();

    /// <summary>Creates the Box2D joint from the base definition (bodies, frames, tuning, thresholds already set).</summary>
    internal abstract B2JointId CreateJoint(B2WorldId world, B2JointDef baseDef, float pixelsPerMeter);

    /// <summary>Applies the properties Box2D can change in place.</summary>
    internal virtual void ApplyLive(B2JointId id, JointRecord2D record, float pixelsPerMeter)
    {
        b2Joint_SetCollideConnected(id, !DisableCollision);
        b2Joint_SetConstraintTuning(id, ConstraintHertz, ConstraintDampingRatio);
        b2Joint_SetForceThreshold(id, BreakForce > 0f ? BreakForce / pixelsPerMeter : float.MaxValue);
        b2Joint_SetTorqueThreshold(id, BreakTorque > 0f ? BreakTorque / (pixelsPerMeter * pixelsPerMeter) : float.MaxValue);
    }

    /// <summary>Where body B is anchored, given the joint's global pose (the joint's position unless a type says otherwise).</summary>
    internal virtual Vector2 GetAnchorB(in Transform2D pose) => pose.Origin;

    /// <summary>
    /// The frames' rotation relative to the joint's (radians): Box2D's prismatic and wheel axis is frame A's X, so a type
    /// whose axis is the joint's +Y (the groove) turns the frames a quarter turn.
    /// </summary>
    internal virtual float FrameAngle => 0f;

    /// <summary>The value the debug draw marks (a pin's angle, a slider's translation in pixels); 0 when there is none.</summary>
    internal virtual float GetDebugValue(B2JointId id, float pixelsPerMeter) => 0f;

    /// <summary>Draws the type's shape: <paramref name="frame"/> is its frame on body A (pixels, axis = frame X), <paramref name="anchorB"/> the anchor on B.</summary>
    internal virtual void DrawDebugShape(DebugLines lines, in Transform2D frame, Vector2 anchorB, float value, Vector4 color)
    {
    }

    /// <summary>Whether the debug draw shows the joint as overstretched, given its anchors' distance in pixels.</summary>
    internal virtual bool IsStretched(float anchorDistance) => anchorDistance > 5f;

    private protected void Live<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        if (Record is { } record)
            record.Space.OnJointPropertyChanged(record);
    }

    /// <summary>A rebuild-shaping property changed (a length, an offset): the joint is re-created from new frames.</summary>
    private protected void Reshape<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        Rebuild();
    }

    /// <summary>An arc of <paramref name="radius"/> pixels from angle <paramref name="from"/> to <paramref name="to"/> (radians, from frame +X).</summary>
    private protected static void DrawArc2D(DebugLines lines, in Transform2D frame, float radius, float from, float to, Vector4 color)
    {
        var segments = Math.Clamp((int)MathF.Ceiling(MathF.Abs(to - from) / (MathF.Tau / 32)), 1, 32);
        var previous = new Vector2(MathF.Cos(from), MathF.Sin(from)) * radius;
        for (var i = 1; i <= segments; i++)
        {
            var angle = from + (to - from) * i / segments;
            var next = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
            lines.AddLine2D(frame, previous, next, color);
            previous = next;
        }
    }

    /// <summary>A track along frame X from <paramref name="lower"/> to <paramref name="upper"/> pixels, end ticks, and a marker at <paramref name="value"/>.</summary>
    private protected static void DrawTrack2D(DebugLines lines, in Transform2D frame, float lower, float upper, float value, bool ticks, Vector4 color)
    {
        lines.AddLine2D(frame, new Vector2(lower, 0), new Vector2(upper, 0), color);
        if (ticks)
        {
            lines.AddLine2D(frame, new Vector2(lower, -5), new Vector2(lower, 5), color);
            lines.AddLine2D(frame, new Vector2(upper, -5), new Vector2(upper, 5), color);
        }

        lines.AddCircle2D(frame, new Vector2(value, 0), 3f, color);
    }
}

/// <summary>
/// Pins two bodies together at the joint's position, letting them turn about it (Godot's <c>PinJoint2D</c>; a Box2D
/// revolute joint). Optional angle limits, relative to the bodies' angle when the joint was created, a motor and a
/// spring. All of them can be changed live.
/// </summary>
[EditorIcon("point", Family = EditorIconFamily.Physics)]
public class PinJoint2D : Joint2D
{
    [Export]
    public bool AngularLimitEnabled
    {
        get;
        set => Live(ref field, value);
    }

    /// <summary>Lowest relative angle in radians (from the angle at creation).</summary>
    [Export]
    public float AngularLimitLower
    {
        get;
        set => Live(ref field, value);
    }

    /// <summary>Highest relative angle in radians (from the angle at creation).</summary>
    [Export]
    public float AngularLimitUpper
    {
        get;
        set => Live(ref field, value);
    }

    [Export]
    public bool MotorEnabled
    {
        get;
        set => Live(ref field, value);
    }

    /// <summary>The motor's target speed in radians per second.</summary>
    [Export]
    public float MotorTargetVelocity
    {
        get;
        set => Live(ref field, value);
    }

    /// <summary>
    /// The most torque the motor applies (kg·px²/s²; the default is 100 N·m). Keep it finite: Box2D's limits are soft,
    /// and an unbounded motor drives straight through them.
    /// </summary>
    [Export(Range = "0,100000000,1")]
    public float MotorMaxTorque
    {
        get;
        set => Live(ref field, value);
    } = 1_000_000f;

    internal override B2JointId CreateJoint(B2WorldId world, B2JointDef baseDef, float pixelsPerMeter)
    {
        var def = b2DefaultRevoluteJointDef();
        def.@base = baseDef;
        def.enableLimit = AngularLimitEnabled;
        def.lowerAngle = MathF.Min(AngularLimitLower, AngularLimitUpper);
        def.upperAngle = MathF.Max(AngularLimitLower, AngularLimitUpper);
        def.enableMotor = MotorEnabled;
        def.motorSpeed = MotorTargetVelocity;
        def.maxMotorTorque = MaxTorque(pixelsPerMeter);
        def.enableSpring = SpringEnabled;
        def.hertz = SpringHertz;
        def.dampingRatio = SpringDampingRatio;
        def.targetAngle = SpringTargetAngle;
        return b2CreateRevoluteJoint(world, in def);
    }

    internal override void ApplyLive(B2JointId id, JointRecord2D record, float pixelsPerMeter)
    {
        base.ApplyLive(id, record, pixelsPerMeter);
        b2RevoluteJoint_EnableLimit(id, AngularLimitEnabled);
        b2RevoluteJoint_SetLimits(id, MathF.Min(AngularLimitLower, AngularLimitUpper), MathF.Max(AngularLimitLower, AngularLimitUpper));
        b2RevoluteJoint_EnableMotor(id, MotorEnabled);
        b2RevoluteJoint_SetMotorSpeed(id, MotorTargetVelocity);
        b2RevoluteJoint_SetMaxMotorTorque(id, MaxTorque(pixelsPerMeter));
        b2RevoluteJoint_EnableSpring(id, SpringEnabled);
        b2RevoluteJoint_SetSpringHertz(id, SpringHertz);
        b2RevoluteJoint_SetSpringDampingRatio(id, SpringDampingRatio);
        b2RevoluteJoint_SetTargetAngle(id, SpringTargetAngle);
        b2Joint_WakeBodies(id);
    }

    /// <summary>Pull the pin toward <see cref="SpringTargetAngle"/> (Box2D's revolute spring).</summary>
    [ExportGroup("Spring")]
    [Export]
    public bool SpringEnabled
    {
        get;
        set => Live(ref field, value);
    }

    /// <summary>The spring's frequency in Hz.</summary>
    [Export(Range = "0,60,0.01")]
    public float SpringHertz
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

    /// <summary>The angle the spring pulls toward in radians (from the angle at creation).</summary>
    [Export]
    public float SpringTargetAngle
    {
        get;
        set => Live(ref field, value);
    }

    /// <summary>Body B's angle relative to A in radians, from the authored pose (0 when inactive).</summary>
    public float GetAngle() => Record is { HasJoint: true } r ? b2RevoluteJoint_GetAngle(r.Id) : 0f;

    private float MaxTorque(float pixelsPerMeter) => MathF.Max(0f, MotorMaxTorque) / (pixelsPerMeter * pixelsPerMeter);

    internal override float GetDebugValue(B2JointId id, float pixelsPerMeter) => b2RevoluteJoint_GetAngle(id);

    internal override void DrawDebugShape(DebugLines lines, in Transform2D frame, Vector2 anchorB, float value, Vector4 color)
    {
        const float radius = 10f;
        if (AngularLimitEnabled)
        {
            var lower = MathF.Min(AngularLimitLower, AngularLimitUpper);
            var upper = MathF.Max(AngularLimitLower, AngularLimitUpper);
            DrawArc2D(lines, frame, radius, lower, upper, color);
            lines.AddLine2D(frame, Vector2.Zero, new Vector2(MathF.Cos(lower), MathF.Sin(lower)) * radius, color);
            lines.AddLine2D(frame, Vector2.Zero, new Vector2(MathF.Cos(upper), MathF.Sin(upper)) * radius, color);
        }

        lines.AddLine2D(frame, Vector2.Zero, new Vector2(MathF.Cos(value), MathF.Sin(value)) * radius * 1.4f, color);
    }
}

/// <summary>
/// A spring between two bodies (Godot's <c>DampedSpringJoint2D</c>; a Box2D distance joint). Body A is anchored at the
/// joint's position and body B at <see cref="Length"/> along the joint's +Y (down). The spring pulls toward
/// <see cref="RestLength"/> and never stretches past <see cref="Length"/>. The spring is frequency and damping ratio
/// (Box2D's model); the defaults are close to Godot's stiffness 20 and damping 1 on a 1 kg body.
/// </summary>
[EditorIcon("wave-sine", Family = EditorIconFamily.Physics)]
public class DampedSpringJoint2D : Joint2D
{
    /// <summary>The longest the spring stretches, and where body B's anchor is (pixels along the joint's +Y). Re-creates the joint.</summary>
    [Export(Range = "1,100000,1")]
    public float Length
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            Rebuild();
        }
    } = 50f;

    /// <summary>The length the spring pulls toward in pixels; 0 = <see cref="Length"/>.</summary>
    [Export(Range = "0,100000,1")]
    public float RestLength
    {
        get;
        set => Live(ref field, value);
    }

    /// <summary>The spring's frequency in Hz: higher is stiffer.</summary>
    [Export(Range = "0.01,60,0.01")]
    public float SpringHertz
    {
        get;
        set => Live(ref field, value);
    } = 1f;

    /// <summary>The spring's damping ratio: 0 bounces forever, 1 settles without overshoot.</summary>
    [Export(Range = "0,10,0.01")]
    public float SpringDampingRatio
    {
        get;
        set => Live(ref field, value);
    } = 0.1f;

    private float Rest => RestLength > 0f ? MathF.Min(RestLength, Length) : Length;

    internal override B2JointId CreateJoint(B2WorldId world, B2JointDef baseDef, float pixelsPerMeter)
    {
        var def = b2DefaultDistanceJointDef();
        def.@base = baseDef;
        def.length = Rest / pixelsPerMeter;
        def.enableSpring = true;
        def.hertz = SpringHertz;
        def.dampingRatio = SpringDampingRatio;
        def.enableLimit = true;
        def.minLength = 0f;
        def.maxLength = Length / pixelsPerMeter;
        return b2CreateDistanceJoint(world, in def);
    }

    internal override void ApplyLive(B2JointId id, JointRecord2D record, float pixelsPerMeter)
    {
        base.ApplyLive(id, record, pixelsPerMeter);
        b2DistanceJoint_SetLength(id, Rest / pixelsPerMeter);
        b2DistanceJoint_SetSpringHertz(id, SpringHertz);
        b2DistanceJoint_SetSpringDampingRatio(id, SpringDampingRatio);
        b2Joint_WakeBodies(id);
    }

    internal override Vector2 GetAnchorB(in Transform2D pose) => pose * new Vector2(0f, Length);

    internal override bool IsStretched(float anchorDistance) => anchorDistance > Length + 5f;
}

/// <summary>
/// A slider (a Box2D prismatic joint): body B moves along the joint's local X axis, fixed in body A, without turning
/// relative to it. Optional translation limits, a motor and a spring, all live. Pixels throughout.
/// </summary>
[EditorIcon("arrows-right-left", Family = EditorIconFamily.Physics)]
public class SliderJoint2D : Joint2D
{
    /// <summary>Limit the translation to <see cref="LowerTranslation"/>…<see cref="UpperTranslation"/>.</summary>
    [ExportGroup("Limit")]
    [Export]
    public bool UseLimit
    {
        get;
        set => Live(ref field, value);
    }

    /// <summary>The lowest translation along the axis in pixels (from the authored pose).</summary>
    [Export(Range = "-100000,100000,1")]
    public float LowerTranslation
    {
        get;
        set => Live(ref field, value);
    } = -50f;

    /// <summary>The highest translation along the axis in pixels.</summary>
    [Export(Range = "-100000,100000,1")]
    public float UpperTranslation
    {
        get;
        set => Live(ref field, value);
    } = 50f;

    /// <summary>Drive B along the axis at <see cref="MotorTargetVelocity"/>.</summary>
    [ExportGroup("Motor")]
    [Export]
    public bool MotorEnabled
    {
        get;
        set => Live(ref field, value);
    }

    /// <summary>The motor's target speed in pixels per second.</summary>
    [Export]
    public float MotorTargetVelocity
    {
        get;
        set => Live(ref field, value);
    }

    /// <summary>The most force the motor applies (kg·px/s²; the default is 1000 N at 100 px/m).</summary>
    [Export(Range = "0,100000000,1")]
    public float MotorMaxForce
    {
        get;
        set => Live(ref field, MathF.Max(0f, value));
    } = 100_000f;

    /// <summary>Pull B toward <see cref="SpringTargetTranslation"/>.</summary>
    [ExportGroup("Spring")]
    [Export]
    public bool SpringEnabled
    {
        get;
        set => Live(ref field, value);
    }

    /// <summary>The spring's frequency in Hz.</summary>
    [Export(Range = "0,60,0.01")]
    public float SpringHertz
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

    /// <summary>The translation the spring pulls toward in pixels.</summary>
    [Export(Range = "-100000,100000,1")]
    public float SpringTargetTranslation
    {
        get;
        set => Live(ref field, value);
    }

    /// <summary>B's translation along the axis in pixels, from the authored pose (0 when inactive).</summary>
    public float GetTranslation() => Record is { HasJoint: true } r ? b2PrismaticJoint_GetTranslation(r.Id) * r.Space.PixelsPerMeter : 0f;

    internal override B2JointId CreateJoint(B2WorldId world, B2JointDef baseDef, float pixelsPerMeter)
    {
        var def = b2DefaultPrismaticJointDef();
        def.@base = baseDef;
        def.enableLimit = UseLimit;
        def.lowerTranslation = MathF.Min(LowerTranslation, UpperTranslation) / pixelsPerMeter;
        def.upperTranslation = MathF.Max(LowerTranslation, UpperTranslation) / pixelsPerMeter;
        def.enableMotor = MotorEnabled;
        def.motorSpeed = MotorTargetVelocity / pixelsPerMeter;
        def.maxMotorForce = MotorMaxForce / pixelsPerMeter;
        def.enableSpring = SpringEnabled;
        def.hertz = SpringHertz;
        def.dampingRatio = SpringDampingRatio;
        def.targetTranslation = SpringTargetTranslation / pixelsPerMeter;
        return b2CreatePrismaticJoint(world, in def);
    }

    internal override void ApplyLive(B2JointId id, JointRecord2D record, float pixelsPerMeter)
    {
        base.ApplyLive(id, record, pixelsPerMeter);
        b2PrismaticJoint_EnableLimit(id, UseLimit);
        b2PrismaticJoint_SetLimits(id, MathF.Min(LowerTranslation, UpperTranslation) / pixelsPerMeter,
            MathF.Max(LowerTranslation, UpperTranslation) / pixelsPerMeter);
        b2PrismaticJoint_EnableMotor(id, MotorEnabled);
        b2PrismaticJoint_SetMotorSpeed(id, MotorTargetVelocity / pixelsPerMeter);
        b2PrismaticJoint_SetMaxMotorForce(id, MotorMaxForce / pixelsPerMeter);
        b2PrismaticJoint_EnableSpring(id, SpringEnabled);
        b2PrismaticJoint_SetSpringHertz(id, SpringHertz);
        b2PrismaticJoint_SetSpringDampingRatio(id, SpringDampingRatio);
        b2PrismaticJoint_SetTargetTranslation(id, SpringTargetTranslation / pixelsPerMeter);
        b2Joint_WakeBodies(id);
    }

    internal override bool IsStretched(float anchorDistance) => false;

    internal override float GetDebugValue(B2JointId id, float pixelsPerMeter) => b2PrismaticJoint_GetTranslation(id) * pixelsPerMeter;

    internal override void DrawDebugShape(DebugLines lines, in Transform2D frame, Vector2 anchorB, float value, Vector4 color) =>
        DrawTrack2D(lines, frame, UseLimit ? MathF.Min(LowerTranslation, UpperTranslation) : -25f,
            UseLimit ? MathF.Max(LowerTranslation, UpperTranslation) : 25f, value, UseLimit, color);
}

/// <summary>
/// A groove (Godot's <c>GrooveJoint2D</c>; a Box2D wheel joint without its spring): body A holds a groove from the
/// joint's position <see cref="Length"/> pixels along its +Y (down), and body B's anchor, which starts
/// <see cref="InitialOffset"/> pixels along it, slides in the groove and turns freely.
/// </summary>
[EditorIcon("arrows-up-down", Family = EditorIconFamily.Physics)]
public class GrooveJoint2D : Joint2D
{
    /// <summary>The groove's length in pixels along the joint's +Y. Re-creates the joint.</summary>
    [Export(Range = "1,100000,1")]
    public float Length
    {
        get;
        set => Reshape(ref field, MathF.Max(1f, value));
    } = 50f;

    /// <summary>Where B's anchor starts along the groove, in pixels. Re-creates the joint.</summary>
    [Export(Range = "0,100000,1")]
    public float InitialOffset
    {
        get;
        set => Reshape(ref field, MathF.Max(0f, value));
    } = 25f;

    internal override float FrameAngle => MathF.PI / 2;

    internal override Vector2 GetAnchorB(in Transform2D pose) => pose * new Vector2(0f, MathF.Min(InitialOffset, Length));

    internal override B2JointId CreateJoint(B2WorldId world, B2JointDef baseDef, float pixelsPerMeter)
    {
        // Frame B starts InitialOffset along the groove, so translations are absolute positions in it: 0…Length.
        var def = b2DefaultWheelJointDef();
        def.@base = baseDef;
        def.enableSpring = false;
        def.enableLimit = true;
        def.lowerTranslation = 0f;
        def.upperTranslation = Length / pixelsPerMeter;
        return b2CreateWheelJoint(world, in def);
    }

    internal override bool IsStretched(float anchorDistance) => anchorDistance > Length + 5f;

    internal override float GetDebugValue(B2JointId id, float pixelsPerMeter) => 0f;

    internal override void DrawDebugShape(DebugLines lines, in Transform2D frame, Vector2 anchorB, float value, Vector4 color) =>
        DrawTrack2D(lines, frame, 0f, Length, 0f, true, color);
}

/// <summary>
/// Glues two bodies together (a Box2D weld joint). Rigid by default; <see cref="LinearHertz"/> and
/// <see cref="AngularHertz"/> make it springy (a bendy sign, a wobbling antenna). All live.
/// </summary>
[EditorIcon("lock", Family = EditorIconFamily.Physics)]
public class WeldJoint2D : Joint2D
{
    /// <summary>The linear spring's frequency in Hz; 0 = rigid.</summary>
    [Export(Range = "0,60,0.01")]
    public float LinearHertz
    {
        get;
        set => Live(ref field, MathF.Max(0f, value));
    }

    /// <summary>The angular spring's frequency in Hz; 0 = rigid.</summary>
    [Export(Range = "0,60,0.01")]
    public float AngularHertz
    {
        get;
        set => Live(ref field, MathF.Max(0f, value));
    }

    /// <summary>The linear spring's damping ratio.</summary>
    [Export(Range = "0,10,0.01")]
    public float LinearDampingRatio
    {
        get;
        set => Live(ref field, MathF.Max(0f, value));
    } = 1f;

    /// <summary>The angular spring's damping ratio.</summary>
    [Export(Range = "0,10,0.01")]
    public float AngularDampingRatio
    {
        get;
        set => Live(ref field, MathF.Max(0f, value));
    } = 1f;

    internal override B2JointId CreateJoint(B2WorldId world, B2JointDef baseDef, float pixelsPerMeter)
    {
        var def = b2DefaultWeldJointDef();
        def.@base = baseDef;
        def.linearHertz = LinearHertz;
        def.angularHertz = AngularHertz;
        def.linearDampingRatio = LinearDampingRatio;
        def.angularDampingRatio = AngularDampingRatio;
        return b2CreateWeldJoint(world, in def);
    }

    internal override void ApplyLive(B2JointId id, JointRecord2D record, float pixelsPerMeter)
    {
        base.ApplyLive(id, record, pixelsPerMeter);
        b2WeldJoint_SetLinearHertz(id, LinearHertz);
        b2WeldJoint_SetAngularHertz(id, AngularHertz);
        b2WeldJoint_SetLinearDampingRatio(id, LinearDampingRatio);
        b2WeldJoint_SetAngularDampingRatio(id, AngularDampingRatio);
        b2Joint_WakeBodies(id);
    }

    internal override void DrawDebugShape(DebugLines lines, in Transform2D frame, Vector2 anchorB, float value, Vector4 color)
    {
        lines.AddLine2D(frame, new Vector2(-5, -5), new Vector2(5, 5), color);
        lines.AddLine2D(frame, new Vector2(-5, 5), new Vector2(5, -5), color);
    }
}

/// <summary>
/// A wheel (a Box2D wheel joint): body B (the wheel) turns freely and rides a suspension spring along the joint's
/// local X axis, fixed in body A (the chassis); point X up the suspension (rotate the joint −90° for a car). A drive
/// motor turns the wheel; travel limits stop the suspension. All live.
/// </summary>
[EditorIcon("circle", Family = EditorIconFamily.Physics)]
public class WheelJoint2D : Joint2D
{
    /// <summary>The suspension spring along the axis (Box2D's default: on, 1 Hz, 0.7).</summary>
    [ExportGroup("Suspension")]
    [Export]
    public bool SpringEnabled
    {
        get;
        set => Live(ref field, value);
    } = true;

    /// <summary>The suspension's frequency in Hz.</summary>
    [Export(Range = "0,60,0.01")]
    public float SpringHertz
    {
        get;
        set => Live(ref field, MathF.Max(0f, value));
    } = 1f;

    /// <summary>The suspension's damping ratio.</summary>
    [Export(Range = "0,10,0.01")]
    public float SpringDampingRatio
    {
        get;
        set => Live(ref field, MathF.Max(0f, value));
    } = 0.7f;

    /// <summary>Limit the suspension travel to <see cref="LowerTranslation"/>…<see cref="UpperTranslation"/>.</summary>
    [Export]
    public bool UseLimit
    {
        get;
        set => Live(ref field, value);
    }

    /// <summary>The lowest suspension travel in pixels.</summary>
    [Export(Range = "-100000,100000,1")]
    public float LowerTranslation
    {
        get;
        set => Live(ref field, value);
    } = -10f;

    /// <summary>The highest suspension travel in pixels.</summary>
    [Export(Range = "-100000,100000,1")]
    public float UpperTranslation
    {
        get;
        set => Live(ref field, value);
    } = 10f;

    /// <summary>Turn the wheel at <see cref="MotorTargetVelocity"/>.</summary>
    [ExportGroup("Motor")]
    [Export]
    public bool MotorEnabled
    {
        get;
        set => Live(ref field, value);
    }

    /// <summary>The motor's target speed in radians per second.</summary>
    [Export]
    public float MotorTargetVelocity
    {
        get;
        set => Live(ref field, value);
    }

    /// <summary>The most torque the motor applies (kg·px²/s²; the default is 100 N·m at 100 px/m).</summary>
    [Export(Range = "0,100000000,1")]
    public float MotorMaxTorque
    {
        get;
        set => Live(ref field, MathF.Max(0f, value));
    } = 1_000_000f;

    internal override B2JointId CreateJoint(B2WorldId world, B2JointDef baseDef, float pixelsPerMeter)
    {
        var def = b2DefaultWheelJointDef();
        def.@base = baseDef;
        def.enableSpring = SpringEnabled;
        def.hertz = SpringHertz;
        def.dampingRatio = SpringDampingRatio;
        def.enableLimit = UseLimit;
        def.lowerTranslation = MathF.Min(LowerTranslation, UpperTranslation) / pixelsPerMeter;
        def.upperTranslation = MathF.Max(LowerTranslation, UpperTranslation) / pixelsPerMeter;
        def.enableMotor = MotorEnabled;
        def.motorSpeed = MotorTargetVelocity;
        def.maxMotorTorque = MotorMaxTorque / (pixelsPerMeter * pixelsPerMeter);
        return b2CreateWheelJoint(world, in def);
    }

    internal override void ApplyLive(B2JointId id, JointRecord2D record, float pixelsPerMeter)
    {
        base.ApplyLive(id, record, pixelsPerMeter);
        b2WheelJoint_EnableSpring(id, SpringEnabled);
        b2WheelJoint_SetSpringHertz(id, SpringHertz);
        b2WheelJoint_SetSpringDampingRatio(id, SpringDampingRatio);
        b2WheelJoint_EnableLimit(id, UseLimit);
        b2WheelJoint_SetLimits(id, MathF.Min(LowerTranslation, UpperTranslation) / pixelsPerMeter,
            MathF.Max(LowerTranslation, UpperTranslation) / pixelsPerMeter);
        b2WheelJoint_EnableMotor(id, MotorEnabled);
        b2WheelJoint_SetMotorSpeed(id, MotorTargetVelocity);
        b2WheelJoint_SetMaxMotorTorque(id, MotorMaxTorque / (pixelsPerMeter * pixelsPerMeter));
        b2Joint_WakeBodies(id);
    }

    internal override bool IsStretched(float anchorDistance) => false;

    internal override void DrawDebugShape(DebugLines lines, in Transform2D frame, Vector2 anchorB, float value, Vector4 color) =>
        DrawTrack2D(lines, frame, UseLimit ? MathF.Min(LowerTranslation, UpperTranslation) : -10f,
            UseLimit ? MathF.Max(LowerTranslation, UpperTranslation) : 10f, Vector2.Dot(anchorB - frame.Origin, frame.X), UseLimit, color);
}

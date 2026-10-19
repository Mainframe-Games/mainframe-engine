using System.Numerics;
using Box2D.NET;
using static Box2D.NET.B2DistanceJoints;
using static Box2D.NET.B2Joints;
using static Box2D.NET.B2RevoluteJoints;

namespace MainframeEngine;

/// <summary>
/// Base of the 2D joints (Godot's <c>Joint2D</c>, ADR 0201): connects the <see cref="PhysicsBody2D"/>s at
/// <see cref="NodeA"/> and <see cref="NodeB"/> (an empty path is the static world) at the joint's position. The joint is
/// created at the next physics flush after both bodies are in the space, so the paths may be set before or after the
/// bodies enter the tree. Changing a path re-creates it there. A body that leaves the space drops its joints; they come
/// back, at the authored frames, when it returns. Softness is Box2D's: <see cref="ConstraintHertz"/> and
/// <see cref="ConstraintDampingRatio"/>, both live.
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

    /// <summary>The joint exists in its space (both bodies are there, and it is not broken).</summary>
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
}

/// <summary>
/// Pins two bodies together at the joint's position, letting them turn about it (Godot's <c>PinJoint2D</c>; a Box2D
/// revolute joint). Optional angle limits, relative to the bodies' angle when the joint was created, and a motor. All
/// of them can be changed live.
/// </summary>
[EditorIcon("link", Family = EditorIconFamily.Physics)]
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
        b2Joint_WakeBodies(id);
    }

    private float MaxTorque(float pixelsPerMeter) => MathF.Max(0f, MotorMaxTorque) / (pixelsPerMeter * pixelsPerMeter);
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

using System.Numerics;
using Jitter2.Dynamics;
using Jitter2.Dynamics.Constraints;
using Jitter2.LinearMath;

namespace MainframeEngine;

/// <summary>The physics state of one <see cref="Joint3D"/> in a <see cref="PhysicsSpace3D"/> (ADR 0211).</summary>
internal sealed class JointRecord3D(Joint3D node, PhysicsSpace3D space, long sequence)
{
    public readonly Joint3D Node = node;
    public readonly PhysicsSpace3D Space = space;
    public readonly long Sequence = sequence;

    /// <summary>The bodies joined (null: the world).</summary>
    public BodyRecord3D? A, B;

    /// <summary>The Jitter2 bodies joined while <see cref="HasJoint"/> (the world's static <c>NullBody</c> for an empty path).</summary>
    public RigidBody? BodyA, BodyB;

    public bool HasJoint;
    public bool Removed;
    public bool Broken;
    public bool InDirtyList;
    public bool LoggedError;

    /// <summary>The constraints are disabled (<see cref="Joint3D.Enabled"/> off or the node disabled); they are kept.</summary>
    public bool Disabled;

    /// <summary>The two bodies are in each other's <see cref="BodyRecord3D.Excluded"/> list for this joint.</summary>
    public bool Excluding;

    /// <summary>
    /// The authored frames: the joint's pose (and, for a type with a second anchor, that anchor) in each body's local
    /// space, captured on the first creation and kept while the paths stay the same. Re-creation (a body returning,
    /// Dynamic ↔ Kinematic, <see cref="Joint3D.Enabled"/>) joins the bodies there, with limits measured from that pose.
    /// </summary>
    public Vector3 FramePositionA, FramePositionB;
    public Quaternion FrameRotationA = Quaternion.Identity, FrameRotationB = Quaternion.Identity;
    public CollisionObject3D? FramedA, FramedB;
    public bool HasFrames;

    /// <summary>The distance between the two anchors at the authored pose (m).</summary>
    public float AuthoredLength;

    /// <summary>Applied force (N) and torque (N·m) when the joint broke.</summary>
    public float BrokenForce, BrokenTorque;

    // The Jitter2 constraints a joint type is made of (null: not used by the type).
    public BallSocket? Socket;
    public HingeAngle? Hinge;
    public AngularMotor? AngularMotor;
    public ConeLimit? Cone;
    public TwistAngle? Twist;
    public FixedAngle? Fixed;
    public PointOnLine? Line;
    public LinearMotor? LinearMotor;
    public DistanceLimit? Distance;

    /// <summary>The rest rotation of B relative to A (Jitter2's <c>Q0</c>): <c>conj(qB) · qA</c> at the authored pose.</summary>
    public Quaternion RestRotation => FrameRotationB * Quaternion.Conjugate(FrameRotationA);

    public void SetEnabled(bool enabled)
    {
        SetEnabled(Socket, enabled);
        SetEnabled(Hinge, enabled);
        SetEnabled(AngularMotor, enabled);
        SetEnabled(Cone, enabled);
        SetEnabled(Twist, enabled);
        SetEnabled(Fixed, enabled);
        SetEnabled(Line, enabled);
        SetEnabled(LinearMotor, enabled);
        SetEnabled(Distance, enabled);
    }

    private static void SetEnabled(Constraint? constraint, bool enabled)
    {
        if (constraint is { IsValid: true })
            constraint.IsEnabled = enabled;
    }

    /// <summary>Removes the constraints Jitter2 still has (a motion-type change may have removed some already).</summary>
    public void RemoveConstraints(Jitter2.World world)
    {
        Remove(world, Socket);
        Remove(world, Hinge);
        Remove(world, AngularMotor);
        Remove(world, Cone);
        Remove(world, Twist);
        Remove(world, Fixed);
        Remove(world, Line);
        Remove(world, LinearMotor);
        Remove(world, Distance);
    }

    private static void Remove(Jitter2.World world, Constraint? constraint)
    {
        if (constraint is { IsValid: true })
            world.Remove(constraint);
    }

    public void ForgetConstraints()
    {
        HasJoint = false;
        Socket = null;
        Hinge = null;
        AngularMotor = null;
        Cone = null;
        Twist = null;
        Fixed = null;
        Line = null;
        LinearMotor = null;
        Distance = null;
        BodyA = BodyB = null;
    }

    /// <summary>Sum of the linear constraint impulses of the last substep (N·s).</summary>
    public float LinearImpulse()
    {
        var sum = 0f;
        if (Socket is { IsValid: true } socket)
            sum += socket.Impulse.Length();
        if (Line is { IsValid: true } line)
            sum += line.Impulse.Length();
        if (Distance is { IsValid: true } distance)
            sum += MathF.Abs(distance.Impulse);
        if (LinearMotor is { IsValid: true } motor)
            sum += MathF.Abs(motor.Impulse);
        return sum;
    }

    /// <summary>Sum of the angular constraint impulses of the last substep (N·m·s).</summary>
    public float AngularImpulse()
    {
        var sum = 0f;
        if (Hinge is { IsValid: true } hinge)
            sum += hinge.Impulse.Length();
        if (Fixed is { IsValid: true } fixedAngle)
            sum += fixedAngle.Impulse.Length();
        if (Cone is { IsValid: true } cone)
            sum += MathF.Abs(cone.Impulse);
        if (Twist is { IsValid: true } twist)
            sum += MathF.Abs(twist.Impulse);
        return sum;
    }

    /// <summary>The joint's frame on body A (world space, the physics pose).</summary>
    public void GetFrameA(out Vector3 position, out Quaternion rotation) => GetFrame(BodyA, FramePositionA, FrameRotationA, out position, out rotation);

    /// <summary>The joint's frame on body B (world space, the physics pose).</summary>
    public void GetFrameB(out Vector3 position, out Quaternion rotation) => GetFrame(BodyB, FramePositionB, FrameRotationB, out position, out rotation);

    private static void GetFrame(RigidBody? body, Vector3 localPosition, Quaternion localRotation, out Vector3 position, out Quaternion rotation)
    {
        if (body is null)
        {
            position = localPosition;
            rotation = localRotation;
            return;
        }

        var q = body.Orientation.ToNumerics();
        position = body.Position.ToNumerics() + Vector3.Transform(localPosition, q);
        rotation = q * localRotation;
    }
}

public sealed partial class PhysicsSpace3D
{
    private readonly List<JointRecord3D> _joints = [];
    private readonly List<JointRecord3D> _dirtyJoints = [];
    private readonly List<JointRecord3D> _brokenJoints = [];
    private readonly List<JointRecord3D> _breakable = [];
    private readonly List<JointRecord3D> _sprung = [];
    private bool _retryPendingJoints;
    private long _jointSequence;

    /// <summary>Joints in the space (created or waiting for their bodies).</summary>
    public int JointCount => _joints.Count;

    /// <summary>Joints that exist in Jitter2 now.</summary>
    public int ActiveJointCount
    {
        get
        {
            var n = 0;
            foreach (var joint in _joints)
                if (joint.HasJoint)
                    n++;
            return n;
        }
    }

    /// <summary>Joints that broke so far (the dev overlay shows the rate).</summary>
    public long BrokenJointTotal { get; private set; }

    internal JointRecord3D AddJoint(Joint3D node)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var record = new JointRecord3D(node, this, ++_jointSequence);
        _joints.Add(record);
        MarkJointDirty(record, recapture: true);
        return record;
    }

    internal void RemoveJoint(JointRecord3D record)
    {
        if (record.Removed)
            return;
        record.Removed = true;
        DestroyJoint(record);
        _joints.Remove(record);
    }

    /// <summary>Re-creates the joint at the next flush; <paramref name="recapture"/> takes new frames from the joint's pose.</summary>
    internal void MarkJointDirty(JointRecord3D record, bool recapture)
    {
        if (record.Removed)
            return;
        if (recapture)
            record.HasFrames = false;
        if (record.InDirtyList)
            return;
        record.InDirtyList = true;
        _dirtyJoints.Add(record);
    }

    /// <summary>A property Jitter2 can change in place (enabled, exclusion, thresholds, limits, motor, spring).</summary>
    internal void OnJointPropertyChanged(JointRecord3D record)
    {
        if (record.HasJoint)
            ApplyJointState(record);
    }

    /// <summary>Force (N) the joint applied in the last substep.</summary>
    internal float GetJointForce(JointRecord3D record) => record.HasJoint ? record.LinearImpulse() / SubstepDelta : 0f;

    /// <summary>Torque (N·m) the joint applied in the last substep.</summary>
    internal float GetJointTorque(JointRecord3D record) => record.HasJoint ? record.AngularImpulse() / SubstepDelta : 0f;

    private float SubstepDelta => _delta / _world.SubstepCount;

    // A body changed between static, kinematic and dynamic: Jitter2 drops constraints with no dynamic body, so its
    // joints are re-created from their frames at the next flush (or wait until a body is dynamic again).
    private void OnJointBodyKindChanged(BodyRecord3D body)
    {
        if (_joints.Count == 0)
            return;
        _retryPendingJoints = true; // joints waiting because neither body was dynamic
        if (body.Joints is not { Count: > 0 } joints)
            return;
        foreach (var joint in joints)
            MarkJointDirty(joint, recapture: false);
    }

    // A body leaving the space: its joints are destroyed now (before Jitter2 removes the body) and wait for it (or another
    // body at the same path) to join again.
    private void DetachJoints(BodyRecord3D body)
    {
        if (body.Joints is not { Count: > 0 } joints)
            return;
        for (var i = joints.Count - 1; i >= 0; i--)
        {
            var joint = joints[i];
            DestroyJoint(joint);
            MarkJointDirty(joint, recapture: false);
        }
    }

    private void DestroyJoint(JointRecord3D record)
    {
        if (!record.HasJoint)
            return;
        SetExclusion(record, false);
        record.A?.Joints?.Remove(record);
        record.B?.Joints?.Remove(record);
        if (!_disposed)
            record.RemoveConstraints(_world);
        record.ForgetConstraints();
        record.A = record.B = null;
        record.Disabled = false;
        _breakable.Remove(record);
        _sprung.Remove(record);
    }

    private void FlushJoints()
    {
        if (_retryPendingJoints)
        {
            _retryPendingJoints = false;
            foreach (var joint in _joints)
                if (!joint.HasJoint && !joint.Broken && !joint.InDirtyList)
                {
                    joint.InDirtyList = true;
                    _dirtyJoints.Add(joint);
                }
        }

        if (_dirtyJoints.Count == 0)
            return;
        // Index loop: creating a joint never adds to the list, but keep it robust to re-entrant marks.
        for (var i = 0; i < _dirtyJoints.Count; i++)
        {
            var joint = _dirtyJoints[i];
            joint.InDirtyList = false;
            if (joint.Removed || joint.Broken)
                continue;
            DestroyJoint(joint);
            TryCreate(joint);
        }

        _dirtyJoints.Clear();
    }

    private void TryCreate(JointRecord3D record)
    {
        var node = record.Node;
        if (!TryResolve(record, node.NodeA, out var a) || !TryResolve(record, node.NodeB, out var b))
            return;
        if (a is null && b is null)
            return; // nothing to join yet (the editor warns): silent, as in 2D
        if (ReferenceEquals(a, b))
        {
            LogOnce(record, $"[Physics] Joint '{node.Name}': NodeA and NodeB are the same body; not created.");
            return;
        }

        var bodyA = a?.Body ?? _world.NullBody;
        var bodyB = b?.Body ?? _world.NullBody;
        if (!record.HasFrames || !ReferenceEquals(record.FramedA, a?.Node) || !ReferenceEquals(record.FramedB, b?.Node))
        {
            // Captured as soon as both bodies are there, even if the joint must wait (below): the authored pose is the
            // one the scene starts in, not wherever kinematic bodies happen to be when one turns dynamic (ragdolls).
            node.GlobalTransform.Decompose(out var position, out var rotation, out _);
            rotation = Quaternion.Normalize(rotation);
            var anchorB = node.GetAnchorB(node.GlobalTransform);
            LocalFrame(a, bodyA, position, rotation, out record.FramePositionA, out record.FrameRotationA);
            LocalFrame(b, bodyB, anchorB, rotation, out record.FramePositionB, out record.FrameRotationB);
            record.AuthoredLength = Vector3.Distance(position, anchorB);
            record.FramedA = a?.Node;
            record.FramedB = b?.Node;
            record.HasFrames = true;
        }

        // Jitter2 keeps no constraint without a dynamic body: wait until one is dynamic (OnJointBodyKindChanged retries).
        if (a?.Kind != PhysicsBodyKind.Dynamic && b?.Kind != PhysicsBodyKind.Dynamic)
            return;

        record.BodyA = bodyA;
        record.BodyB = bodyB;
        try
        {
            node.CreateConstraints(record);
        }
        catch (ArgumentException e)
        {
            // Degenerate data (a zero axis, a non-finite pose): the joint stays pending until something changes.
            record.RemoveConstraints(_world);
            record.ForgetConstraints();
            LogOnce(record, $"[Physics] Joint '{node.Name}' is invalid ({e.Message}); not created.");
            return;
        }

        record.HasJoint = true;
        record.A = a;
        record.B = b;
        if (a is not null)
            (a.Joints ??= []).Add(record);
        if (b is not null)
            (b.Joints ??= []).Add(record);
        record.LoggedError = false;
        record.Disabled = false;
        ApplyJointState(record);
    }

    /// <summary>Enabled state, collision exclusion, breakable/spring membership and the type's live properties.</summary>
    private void ApplyJointState(JointRecord3D record)
    {
        var node = record.Node;
        var disabled = !node.Enabled || node.ResolvedProcessMode == ProcessMode.Disabled;
        if (disabled != record.Disabled)
        {
            record.Disabled = disabled;
            record.SetEnabled(!disabled);
        }

        SetExclusion(record, !disabled && node.ExcludeNodesFromCollision);
        SetMember(_breakable, record, node.BreakForce > 0f || node.BreakTorque > 0f);
        SetMember(_sprung, record, node.HasSpring);
        node.ApplyLive(record);
        WakeJointBodies(record);
    }

    private static void SetMember(List<JointRecord3D> list, JointRecord3D record, bool member)
    {
        var index = list.IndexOf(record);
        if (member && index < 0)
            list.Add(record);
        else if (!member && index >= 0)
            list.RemoveAt(index);
    }

    private static void WakeJointBodies(JointRecord3D record)
    {
        if (record.BodyA is { MotionType: not MotionType.Static } a)
            a.SetActivationState(true);
        if (record.BodyB is { MotionType: not MotionType.Static } b)
            b.SetActivationState(true);
    }

    // False: the path names something that cannot be joined yet (missing, not in this space, not a body).
    private bool TryResolve(JointRecord3D record, NodePath path, out BodyRecord3D? body)
    {
        body = null;
        if (path.IsEmpty)
            return true;
        switch (record.Node.GetNodeOrNull(path))
        {
            case null:
                return false; // not in the tree (yet): wait
            case PhysicsBody3D { Record: { Body: not null } found } when ReferenceEquals(found.Space, this):
                body = found;
                return true;
            case PhysicsBody3D { Record: null }:
                return false; // disabled or not yet added: wait for it to join
            case PhysicsBody3D:
                LogOnce(record, $"[Physics] Joint '{record.Node.Name}': '{path}' is in another world; not created.");
                return false;
            default:
                LogOnce(record, $"[Physics] Joint '{record.Node.Name}': '{path}' is not a PhysicsBody3D; not created.");
                return false;
        }
    }

    private static void LogOnce(JointRecord3D record, string message)
    {
        if (record.LoggedError)
            return;
        record.LoggedError = true;
        Log.Error(message);
    }

    // The joint's pose in the body's frame, where the body is authored to be: a kinematic body's node move is only a
    // target until the step applies it (a ragdoll moved to its spawn point right after it was added), so its node pose
    // counts, not where Jitter2 still has it.
    private static void LocalFrame(BodyRecord3D? record, RigidBody body, Vector3 position, Quaternion rotation, out Vector3 localPosition,
        out Quaternion localRotation)
    {
        Vector3 origin;
        Quaternion orientation;
        if (record is { HasTarget: true })
        {
            origin = record.TargetPosition;
            orientation = record.TargetRotation;
        }
        else if (record is { TransformDirty: true })
        {
            record.Node.GlobalTransform.Decompose(out origin, out orientation, out _);
        }
        else
        {
            origin = body.Position.ToNumerics();
            orientation = body.Orientation.ToNumerics();
        }

        var inverse = Quaternion.Conjugate(Quaternion.Normalize(orientation));
        localPosition = Vector3.Transform(position - origin, inverse);
        localRotation = Quaternion.Normalize(inverse * rotation);
    }

    // ---- collision exclusion ----------------------------------------------------------------------------------------

    private static void SetExclusion(JointRecord3D record, bool exclude)
    {
        if (record.Excluding == exclude)
            return;
        if (record.A is not { } a || record.B is not { } b)
        {
            record.Excluding = false; // the world collides with nothing anyway
            return;
        }

        record.Excluding = exclude;
        if (exclude)
        {
            a.Excluded = With(a.Excluded, b);
            b.Excluded = With(b.Excluded, a);
            // Resting contacts survive filter changes: drop them so the pair separates now (as layer changes do).
            a.Body?.ClearContactCache();
            b.Body?.ClearContactCache();
        }
        else
        {
            a.Excluded = Without(a.Excluded, b);
            b.Excluded = Without(b.Excluded, a);
        }
    }

    // Copy-on-write: Jitter2's worker threads may read the old array during a step (this runs between steps).
    private static BodyRecord3D[] With(BodyRecord3D[]? list, BodyRecord3D other)
    {
        var length = list?.Length ?? 0;
        var result = new BodyRecord3D[length + 1];
        list?.CopyTo(result, 0);
        result[length] = other;
        return result;
    }

    private static BodyRecord3D[]? Without(BodyRecord3D[]? list, BodyRecord3D other)
    {
        if (list is null)
            return null;
        var index = Array.IndexOf(list, other);
        if (index < 0)
            return list;
        if (list.Length == 1)
            return null;
        var result = new BodyRecord3D[list.Length - 1];
        Array.Copy(list, 0, result, 0, index);
        Array.Copy(list, index + 1, result, index, list.Length - index - 1);
        return result;
    }

    // ---- constraint factories (frames from the record) ----------------------------------------------------------

    internal BallSocket CreateBallSocket(JointRecord3D record)
    {
        var socket = _world.CreateConstraint<BallSocket>(record.BodyA!, record.BodyB!);
        socket.Initialize(record.BodyA!.Position);
        ref var data = ref socket.Data;
        data.LocalAnchor1 = record.FramePositionA.ToJ();
        data.LocalAnchor2 = record.FramePositionB.ToJ();
        return socket;
    }

    /// <summary>A hinge about <paramref name="axis"/> (joint space); angles are measured from the authored pose.</summary>
    internal HingeAngle CreateHingeAngle(JointRecord3D record, Vector3 axis)
    {
        var hinge = _world.CreateConstraint<HingeAngle>(record.BodyA!, record.BodyB!);
        hinge.Initialize(WorldAxisB(record, axis), AngularLimit.Full);
        ref var data = ref hinge.Data;
        data.Axis = Vector3.Transform(axis, record.FrameRotationB).ToJ();
        data.Q0 = record.RestRotation.ToJ();
        return hinge;
    }

    internal AngularMotor CreateAngularMotor(JointRecord3D record, Vector3 axis)
    {
        var motor = _world.CreateConstraint<AngularMotor>(record.BodyA!, record.BodyB!);
        motor.Initialize(WorldAxisB(record, axis));
        ref var data = ref motor.Data;
        data.LocalAxis1 = Vector3.Transform(axis, record.FrameRotationA).ToJ();
        data.LocalAxis2 = Vector3.Transform(axis, record.FrameRotationB).ToJ();
        return motor;
    }

    /// <summary>A cone limit on <paramref name="axis"/> (joint space): the angle between the axis on A and on B.</summary>
    internal ConeLimit CreateConeLimit(JointRecord3D record, Vector3 axis)
    {
        var cone = _world.CreateConstraint<ConeLimit>(record.BodyA!, record.BodyB!);
        var world = WorldAxisB(record, axis);
        cone.Initialize(world, world, new AngularLimit(JAngle.FromRadian(0f), JAngle.FromRadian(MathF.PI / 4)));
        ref var data = ref cone.Data;
        data.LocalAxis1 = Vector3.Transform(axis, record.FrameRotationA).ToJ();
        data.LocalAxis2 = Vector3.Transform(axis, record.FrameRotationB).ToJ();
        return cone;
    }

    /// <summary>A twist limit about <paramref name="axis"/> (joint space), measured from the authored pose.</summary>
    internal TwistAngle CreateTwistAngle(JointRecord3D record, Vector3 axis)
    {
        var twist = _world.CreateConstraint<TwistAngle>(record.BodyA!, record.BodyB!);
        var world = WorldAxisB(record, axis);
        twist.Initialize(world, world, AngularLimit.Full);
        ref var data = ref twist.Data;
        data.B = Vector3.Transform(axis, record.FrameRotationB).ToJ();
        data.Q0 = record.RestRotation.ToJ();
        return twist;
    }

    /// <summary>Locks the relative rotation at the authored one.</summary>
    internal FixedAngle CreateFixedAngle(JointRecord3D record)
    {
        var fixedAngle = _world.CreateConstraint<FixedAngle>(record.BodyA!, record.BodyB!);
        fixedAngle.Initialize();
        fixedAngle.Data.Q0 = record.RestRotation.ToJ();
        return fixedAngle;
    }

    /// <summary>B's anchor stays on the line through A's anchor along <paramref name="axis"/> (joint space, fixed in A).</summary>
    internal PointOnLine CreatePointOnLine(JointRecord3D record, Vector3 axis)
    {
        var line = _world.CreateConstraint<PointOnLine>(record.BodyA!, record.BodyB!);
        var anchor = record.BodyA!.Position;
        line.Initialize(WorldAxisB(record, axis), anchor, anchor, LinearLimit.Full);
        ref var data = ref line.Data;
        data.LocalAxis = Vector3.Transform(axis, record.FrameRotationA).ToJ();
        data.LocalAnchor1 = record.FramePositionA.ToJ();
        data.LocalAnchor2 = record.FramePositionB.ToJ();
        return line;
    }

    internal LinearMotor CreateLinearMotor(JointRecord3D record, Vector3 axis)
    {
        var motor = _world.CreateConstraint<LinearMotor>(record.BodyA!, record.BodyB!);
        var world = WorldAxisB(record, axis);
        motor.Initialize(world, world);
        ref var data = ref motor.Data;
        data.LocalAxis1 = Vector3.Transform(axis, record.FrameRotationA).ToJ();
        data.LocalAxis2 = Vector3.Transform(axis, record.FrameRotationB).ToJ();
        return motor;
    }

    /// <summary>The distance between the two anchors, limited (<see cref="SetDistanceLimit"/>); no limit until set.</summary>
    internal DistanceLimit CreateDistanceLimit(JointRecord3D record)
    {
        var distance = _world.CreateConstraint<DistanceLimit>(record.BodyA!, record.BodyB!);
        var anchor = record.BodyA!.Position;
        distance.Initialize(anchor, anchor + JVector.UnitX, LinearLimit.Full);
        ref var data = ref distance.Data;
        data.LocalAnchor1 = record.FramePositionA.ToJ();
        data.LocalAnchor2 = record.FramePositionB.ToJ();
        data.Distance = 0f; // the limits are absolute lengths
        return distance;
    }

    /// <summary>Keeps the anchors <paramref name="min"/>…<paramref name="max"/> metres apart (equal: a rod).</summary>
    internal static void SetDistanceLimit(DistanceLimit distance, float min, float max)
    {
        ref var data = ref distance.Data;
        data.LimitMin = min;
        data.LimitMax = max;
    }

    private static JVector WorldAxisB(JointRecord3D record, Vector3 axis) =>
        Vector3.Transform(Vector3.Transform(axis, record.FrameRotationB), record.BodyB!.Orientation.ToNumerics()).ToJ();

    // ---- per step ---------------------------------------------------------------------------------------------------

    // Before the step: the PD springs of hinge, slider and distance joints.
    private void ApplyJointSprings(float delta)
    {
        foreach (var joint in _sprung)
            if (!joint.Disabled && IsAwake(joint))
                joint.Node.ApplySpring(joint, delta);
    }

    /// <summary>
    /// An implicit (unconditionally stable) PD spring about <paramref name="axis"/> (world) through
    /// <paramref name="anchor"/>: the relative angular velocity the spring would reach after one step, turned into equal
    /// and opposite torques on the dynamic bodies. <paramref name="error"/> is the angle past the target (rad); the
    /// frequency is clamped to a quarter of the step rate.
    /// </summary>
    internal static void ApplyAngularSpring(JointRecord3D record, Vector3 axis, Vector3 anchor, float error, float frequency, float dampingRatio,
        float delta)
    {
        var a = record.BodyA!;
        var b = record.BodyB!;
        ref var dataA = ref a.Data;
        ref var dataB = ref b.Data;
        var axisJ = axis.ToJ();
        // Each body turns about the joint's anchor (the other constraints see to that): its inertia about the axis there.
        var inverseInertia = InverseInertiaAbout(a, axisJ, anchor) + InverseInertiaAbout(b, axisJ, anchor);
        if (!(inverseInertia > 0f))
            return;
        var speed = JVector.Dot(axisJ, dataB.AngularVelocity - dataA.AngularVelocity);
        var target = SpringVelocity(speed, error, frequency, dampingRatio, delta);
        var magnitude = (target - speed) / inverseInertia / delta;
        if (MathF.Abs(magnitude) < 1e-7f)
            return;
        var torque = axisJ * magnitude;
        if (b.MotionType == MotionType.Dynamic)
        {
            b.Torque += torque;
            if (!b.IsActive)
                b.SetActivationState(true);
        }

        if (a.MotionType == MotionType.Dynamic)
        {
            a.Torque -= torque;
            if (!a.IsActive)
                a.SetActivationState(true);
        }
    }

    /// <summary>
    /// An implicit PD spring along <paramref name="direction"/> (world, unit) between the anchors
    /// <paramref name="pointA"/> and <paramref name="pointB"/>; <paramref name="error"/> is the distance past the target (m).
    /// </summary>
    internal static void ApplyLinearSpring(JointRecord3D record, Vector3 direction, float error, Vector3 pointA, Vector3 pointB, float frequency,
        float dampingRatio, float delta)
    {
        var a = record.BodyA!;
        var b = record.BodyB!;
        ref var dataA = ref a.Data;
        ref var dataB = ref b.Data;
        var n = direction.ToJ();
        var rA = pointA.ToJ() - dataA.Position;
        var rB = pointB.ToJ() - dataB.Position;
        var rnA = JVector.Cross(rA, n);
        var rnB = JVector.Cross(rB, n);
        var inverseMass = JVector.Dot(JVector.Multiply(n, dataA.InverseMassVector), n) + JVector.Dot(JVector.Multiply(n, dataB.InverseMassVector), n) +
                          JVector.Dot(JVector.Transform(rnA, dataA.InverseInertiaWorld), rnA) +
                          JVector.Dot(JVector.Transform(rnB, dataB.InverseInertiaWorld), rnB);
        if (!(inverseMass > 0f))
            return;
        var velocityA = dataA.Velocity + JVector.Cross(dataA.AngularVelocity, rA);
        var velocityB = dataB.Velocity + JVector.Cross(dataB.AngularVelocity, rB);
        var speed = JVector.Dot(n, velocityB - velocityA);
        var target = SpringVelocity(speed, error, frequency, dampingRatio, delta);
        var force = n * ((target - speed) / inverseMass / delta);
        if (b.MotionType == MotionType.Dynamic)
            b.AddForce(force, pointB.ToJ());
        if (a.MotionType == MotionType.Dynamic)
            a.AddForce(-force, pointA.ToJ());
    }

    // 1 / (inertia about the axis at the centre of mass + m·d² to the anchor); 0 for a body that is not dynamic.
    private static float InverseInertiaAbout(RigidBody body, in JVector axis, Vector3 anchor)
    {
        if (body.MotionType != MotionType.Dynamic)
            return 0f;
        ref var data = ref body.Data;
        var inverse = JVector.Dot(JVector.Transform(axis, data.InverseInertiaWorld), axis);
        if (!(inverse > 0f))
            return 0f;
        var arm = JVector.Cross(axis, data.Position - anchor.ToJ());
        return 1f / (1f / inverse + body.Mass * arm.LengthSquared());
    }

    // Implicit Euler on x'' = −ω²x − 2ζωx': the velocity after one step, independent of the mass.
    private static float SpringVelocity(float speed, float error, float frequency, float dampingRatio, float delta)
    {
        var omega = MathF.Tau * Math.Clamp(frequency, 0f, 0.25f / delta);
        return (speed - delta * omega * omega * error) / (1f + 2f * delta * MathF.Max(0f, dampingRatio) * omega + delta * delta * omega * omega);
    }

    private static bool IsAwake(JointRecord3D record) =>
        record.BodyA is { MotionType: MotionType.Dynamic, IsActive: true } || record.BodyB is { MotionType: MotionType.Dynamic, IsActive: true };

    // After the step: joints over their force or torque threshold break (destroyed now, Broken dispatched after the
    // body signals). Reverse loop: breaking removes the joint from the list.
    private void CheckJointBreaks(float delta)
    {
        if (_breakable.Count == 0)
            return;
        var substep = delta / _world.SubstepCount;
        for (var i = _breakable.Count - 1; i >= 0; i--)
        {
            var joint = _breakable[i];
            if (joint.Disabled || !joint.HasJoint)
                continue;
            var node = joint.Node;
            var force = joint.LinearImpulse() / substep;
            var torque = joint.AngularImpulse() / substep;
            if (!(node.BreakForce > 0f && force > node.BreakForce) && !(node.BreakTorque > 0f && torque > node.BreakTorque))
                continue;
            joint.BrokenForce = force;
            joint.BrokenTorque = torque;
            DestroyJoint(joint);
            joint.Broken = true;
            BrokenJointTotal++;
            _brokenJoints.Add(joint);
        }
    }

    private void DispatchJointBreaks()
    {
        if (_brokenJoints.Count == 0)
            return;
        if (_brokenJoints.Count > 1)
            _brokenJoints.Sort(JointOrder.Instance); // creation order
        // Index loop: a handler may break or free other joints. A freed joint is skipped, and so is one a body signal
        // handler rebuilt since it broke (no longer broken).
        for (var i = 0; i < _brokenJoints.Count; i++)
            if (_brokenJoints[i] is { Removed: false, Broken: true } joint)
                joint.Node.EmitBroken();
        _brokenJoints.Clear();
    }

    private sealed class JointOrder : IComparer<JointRecord3D>
    {
        public static readonly JointOrder Instance = new();

        public int Compare(JointRecord3D? x, JointRecord3D? y) => x!.Sequence.CompareTo(y!.Sequence);
    }

    // ---- debug draw -------------------------------------------------------------------------------------------------

    private static readonly Vector4 JointColor = new(1f, 0.75f, 0.2f, 1f);
    private static readonly Vector4 StretchedJointColor = new(1f, 0.25f, 0.2f, 1f);
    private static readonly Vector4 InactiveJointColor = new(0.55f, 0.55f, 0.6f, 1f);

    private void DrawJointsDebug(DebugLines lines)
    {
        foreach (var joint in _joints)
        {
            var node = joint.Node;
            if (!node.IsVisibleInTree())
                continue;
            if (!joint.HasJoint)
            {
                node.DrawDebug(lines, InactiveJointColor); // pending (edit mode, missing bodies) or broken: greyed
                continue;
            }

            JointFrames(joint, out var frame, out var anchorB);
            var color = joint.Disabled ? InactiveJointColor : node.IsStretched(joint, frame.Origin, anchorB) ? StretchedJointColor : JointColor;
            node.DrawDebugShape(lines, frame, anchorB, node.GetDebugValue(joint), color);
        }
    }

    /// <summary>The joint's frame on body A and its anchor on body B at the bodies' interpolated (rendered) poses.</summary>
    internal static void JointFrames(JointRecord3D joint, out Transform3D frame, out Vector3 anchorB)
    {
        InterpolatedFrame(joint.A, joint.FramePositionA, joint.FrameRotationA, out var positionA, out var rotationA);
        InterpolatedFrame(joint.B, joint.FramePositionB, joint.FrameRotationB, out anchorB, out _);
        frame = Transform3D.FromTrs(positionA, rotationA, Vector3.One);
    }

    // A body's frame at its interpolated (rendered) pose, so the lines match what is drawn.
    private static void InterpolatedFrame(BodyRecord3D? body, Vector3 localPosition, Quaternion localRotation, out Vector3 position,
        out Quaternion rotation)
    {
        if (body is null)
        {
            position = localPosition;
            rotation = localRotation;
            return;
        }

        body.Node.GlobalTransform.Decompose(out var origin, out var q, out _);
        q = Quaternion.Normalize(q);
        position = origin + Vector3.Transform(localPosition, q);
        rotation = q * localRotation;
    }
}

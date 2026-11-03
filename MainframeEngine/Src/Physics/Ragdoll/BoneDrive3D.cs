using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// One active-ragdoll drive (G9.10c, ADR 0235): an internal joint node a <see cref="PhysicalBoneSimulator3D"/> creates
/// (no owner: never saved, not in the editor's tree) holding a <see cref="PoseDrive"/> between a bone's body and its
/// parent's body (a local drive) or the world (a hold). The simulator writes its target and strength every fixed step
/// through <see cref="Drive"/>, straight into the constraint, without re-applying the joint's state.
/// </summary>
/// <remarks>
/// Being a joint gives it deferred creation, bodies leaving and returning, and waiting while neither body is dynamic.
/// It never excludes collisions (the simulator's exclusion group does) and never breaks.
/// </remarks>
internal sealed class BoneDrive3D : Joint3D
{
    private uint _flags;
    private float _angularFrequency, _angularDampingRatio, _maxTorque;
    private float _linearFrequency, _linearDampingRatio, _maxForce;
    private Quaternion _targetRotation = Quaternion.Identity;
    private Vector3 _targetPosition;

    public BoneDrive3D()
    {
        ExcludeNodesFromCollision = false;
    }

    /// <summary>The drive is on (some part has a strength above 0).</summary>
    public bool IsDriving => _flags != 0;

    /// <summary>The simulator gave it a target this simulation (its readbacks mean something), driving or not.</summary>
    public bool HasTarget { get; private set; }

    /// <summary>Body B's target rotation in body A's frame (the world for a hold).</summary>
    public Quaternion TargetRotation => _targetRotation;

    /// <summary>
    /// Sets the drive for the next step: body B's target rotation (and, for a hold, centre) in body A's frame (the world
    /// for a hold), and each part's frequency, damping ratio and maximum; a frequency of 0 turns that part off.
    /// </summary>
    public void Drive(Quaternion targetRotation, Vector3 targetPosition, float angularFrequency, float angularDampingRatio, float maxTorque,
        float linearFrequency, float linearDampingRatio, float maxForce)
    {
        _targetRotation = targetRotation;
        _targetPosition = targetPosition;
        _angularFrequency = angularFrequency;
        _angularDampingRatio = angularDampingRatio;
        _maxTorque = maxTorque;
        _linearFrequency = linearFrequency;
        _linearDampingRatio = linearDampingRatio;
        _maxForce = maxForce;
        _flags = (angularFrequency > 0f && maxTorque > 0f ? PoseDrive.AngularOn : 0u) | (linearFrequency > 0f && maxForce > 0f ? PoseDrive.LinearOn : 0u);
        HasTarget = true;
        if (Record is { } record)
            Write(record, wake: _flags != 0);
    }

    /// <summary>Turns the drive off and forgets its target (a held bone, or the simulation stopped).</summary>
    public void Stop()
    {
        HasTarget = false;
        if (_flags == 0)
            return;
        _flags = 0;
        if (Record is { } record)
            Write(record, wake: true); // a body asleep in a perfect hold must wake to fall
    }

    /// <summary>How far body B's rotation is from its target, in degrees (0 while not created).</summary>
    public float GetErrorDegrees()
    {
        if (Record is not { HasJoint: true, BodyA: { } a, BodyB: { } b })
            return 0f;
        var target = Quaternion.Normalize(a.Orientation.ToNumerics() * _targetRotation);
        return float.RadiansToDegrees(PoseDrive.RotationError(b.Orientation.ToNumerics(), target).Length());
    }

    /// <summary>How far body B's centre is from its target position, in metres (0 while not created).</summary>
    public float GetPositionError()
    {
        if (Record is not { HasJoint: true, BodyA: { } a, BodyB: { } b })
            return 0f;
        var target = a.Position.ToNumerics() + Vector3.Transform(_targetPosition, a.Orientation.ToNumerics());
        return Vector3.Distance(b.Position.ToNumerics(), target);
    }

    internal override void CreateConstraints(JointRecord3D record) => record.PoseDrive = record.Space.CreatePoseDrive(record);

    internal override void ApplyLive(JointRecord3D record) => Write(record, wake: false);

    // No debug shape: a drive is not a joint a person placed.
    internal override void DrawDebugShape(DebugLines lines, in Transform3D frame, Vector3 anchorB, float value, Vector4 color)
    {
    }

    internal override bool IsStretched(JointRecord3D record, Vector3 anchorA, Vector3 anchorB) => false;

    private void Write(JointRecord3D record, bool wake)
    {
        if (record.PoseDrive is not { IsValid: true } drive)
            return;
        ref var data = ref drive.Data;
        data.TargetRotation = _targetRotation.ToJ();
        data.TargetPosition = _targetPosition.ToJ();
        data.Flags = _flags;
        data.AngularFrequency = _angularFrequency;
        data.AngularDampingRatio = _angularDampingRatio;
        data.MaxTorque = _maxTorque;
        data.LinearFrequency = _linearFrequency;
        data.LinearDampingRatio = _linearDampingRatio;
        data.MaxForce = _maxForce;
        // Waking also restarts Jitter2's sleep timer: a driven body follows a moving target and must not fall asleep in a
        // still hold, and one whose drive stops must fall even if it had been still for long enough to sleep.
        if (wake && record.BodyB is { } body)
            body.SetActivationState(true);
    }
}

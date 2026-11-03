using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Jitter2.Dynamics;
using Jitter2.Dynamics.Constraints;
using Jitter2.LinearMath;
using static MainframeEngine.SixDofMath;

namespace MainframeEngine;

/// <summary>
/// Pulls body 2 towards a target pose relative to body 1 with soft rows (<see cref="SoftRow"/>), solved with the other
/// constraints (G9.10c, ADR 0235): an angular part (a 3×3 block on the rotation error, its impulse capped by
/// <see cref="Values.MaxTorque"/>) and a linear part (body 2's centre towards a target point, capped by
/// <see cref="Values.MaxForce"/>). Active ragdolls use it for local drives (angular, between a bone's body and its
/// parent's) and world holds (both parts, against the world). The linear part works at the bodies' centres, without
/// arms: it is meant for a static body 1 (the world). A flag of 0 costs nothing.
/// </summary>
internal sealed unsafe class PoseDrive : Constraint<PoseDrive.Values>
{
    public const uint AngularOn = 1, LinearOn = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct Values
    {
        public JitterConstraintHeader Header;

        /// <summary>Body 2's target orientation in body 1's frame.</summary>
        public JQuaternion TargetRotation;

        /// <summary>Body 2's target centre in body 1's frame.</summary>
        public JVector TargetPosition;

        public uint Flags;
        public float AngularFrequency, AngularDampingRatio, MaxTorque;
        public float LinearFrequency, LinearDampingRatio, MaxForce;

        // Per substep.
        public JSymmetricMatrix AngularMass;
        public JVector AngularBias, AngularImpulse;
        public JVector LinearMass, LinearBias, LinearImpulse;
        public float AngularMassScale, LinearMassScale;
    }

    private static readonly uint s_dispatchId = RegisterFullConstraint(&Prepare, &Iterate);

    protected override void Create()
    {
        DispatchId = s_dispatchId;
        base.Create();
    }

    public override void ResetWarmStart()
    {
        ref var data = ref Data;
        data.AngularImpulse = JVector.Zero;
        data.LinearImpulse = JVector.Zero;
    }

    /// <summary>
    /// The rotation (world, axis × angle, at most π) that takes <paramref name="current"/> onto <paramref name="target"/>.
    /// </summary>
    internal static Vector3 RotationError(Quaternion current, Quaternion target)
    {
        var delta = Quaternion.Normalize(target * Quaternion.Conjugate(current));
        if (delta.W < 0f)
            delta = -delta; // the short way round
        var sin = MathF.Sqrt(delta.X * delta.X + delta.Y * delta.Y + delta.Z * delta.Z);
        if (sin < 1e-7f)
            return new Vector3(delta.X, delta.Y, delta.Z) * 2f;
        var angle = 2f * MathF.Atan2(sin, delta.W);
        return new Vector3(delta.X, delta.Y, delta.Z) * (angle / sin);
    }

    private static void Prepare(ref ConstraintData constraint, float idt)
    {
        ref var data = ref Unsafe.As<ConstraintData, Values>(ref constraint);
        if (data.Flags == 0)
        {
            data.AngularImpulse = JVector.Zero;
            data.LinearImpulse = JVector.Zero;
            return;
        }

        ref var body1 = ref data.Header.Body1.Data;
        ref var body2 = ref data.Header.Body2.Data;
        var h = 1f / idt;
        var q1 = Q(body1.Orientation);

        if ((data.Flags & AngularOn) != 0)
        {
            // C = the rotation still to go, negated: the row drives (ω2 − ω1) to close it.
            var error = RotationError(Q(body2.Orientation), Quaternion.Normalize(q1 * Q(data.TargetRotation)));
            SoftRow.Coefficients(data.AngularFrequency, data.AngularDampingRatio, h, out var biasRate, out data.AngularMassScale);
            V(ref data.AngularBias) = -error * biasRate;
            var k = body1.InverseInertiaWorld + body2.InverseInertiaWorld;
            if (!JSymmetricMatrix.Inverse(k, out data.AngularMass))
                data.AngularMass = default;
            ApplyAngular(ref body1, ref body2, V(ref data.AngularImpulse)); // warm start
        }
        else
        {
            data.AngularImpulse = JVector.Zero;
        }

        if ((data.Flags & LinearOn) != 0)
        {
            var target = V(ref body1.Position) + Vector3.Transform(V(ref data.TargetPosition), q1);
            SoftRow.Coefficients(data.LinearFrequency, data.LinearDampingRatio, h, out var biasRate, out data.LinearMassScale);
            V(ref data.LinearBias) = (V(ref body2.Position) - target) * biasRate;
            var k = V(ref body1.InverseMassVector) + V(ref body2.InverseMassVector);
            V(ref data.LinearMass) = new Vector3(k.X > 0f ? 1f / k.X : 0f, k.Y > 0f ? 1f / k.Y : 0f, k.Z > 0f ? 1f / k.Z : 0f);
            ApplyLinearCentres(ref body1, ref body2, V(ref data.LinearImpulse)); // warm start
        }
        else
        {
            data.LinearImpulse = JVector.Zero;
        }
    }

    private static void Iterate(ref ConstraintData constraint, float idt)
    {
        ref var data = ref Unsafe.As<ConstraintData, Values>(ref constraint);
        if (data.Flags == 0)
            return;
        ref var body1 = ref data.Header.Body1.Data;
        ref var body2 = ref data.Header.Body2.Data;

        if ((data.Flags & AngularOn) != 0)
        {
            var cdot = V(ref body2.AngularVelocity) - V(ref body1.AngularVelocity);
            var old = V(ref data.AngularImpulse);
            var lambda = -data.AngularMassScale * Mul(data.AngularMass, cdot + V(ref data.AngularBias)) - (1f - data.AngularMassScale) * old;
            var total = Cap(old + lambda, data.MaxTorque / idt);
            V(ref data.AngularImpulse) = total;
            ApplyAngular(ref body1, ref body2, total - old);
        }

        if ((data.Flags & LinearOn) != 0)
        {
            var cdot = V(ref body2.Velocity) - V(ref body1.Velocity);
            var old = V(ref data.LinearImpulse);
            var lambda = -data.LinearMassScale * V(ref data.LinearMass) * (cdot + V(ref data.LinearBias)) - (1f - data.LinearMassScale) * old;
            var total = Cap(old + lambda, data.MaxForce / idt);
            V(ref data.LinearImpulse) = total;
            ApplyLinearCentres(ref body1, ref body2, total - old);
        }
    }

    // An accumulated impulse no longer than max (the drive's strength limit).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector3 Cap(Vector3 impulse, float max)
    {
        var length = impulse.Length();
        return length > max ? impulse * (max / length) : impulse;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ApplyAngular(ref RigidBodyData body1, ref RigidBodyData body2, Vector3 impulse)
    {
        V(ref body1.AngularVelocity) -= Mul(body1.InverseInertiaWorld, impulse);
        V(ref body2.AngularVelocity) += Mul(body2.InverseInertiaWorld, impulse);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ApplyLinearCentres(ref RigidBodyData body1, ref RigidBodyData body2, Vector3 impulse)
    {
        V(ref body1.Velocity) -= V(ref body1.InverseMassVector) * impulse;
        V(ref body2.Velocity) += V(ref body2.InverseMassVector) * impulse;
    }
}

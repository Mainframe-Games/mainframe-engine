using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Jitter2.Dynamics;
using Jitter2.Dynamics.Constraints;
using Jitter2.LinearMath;
using static MainframeEngine.SixDofMath;

namespace MainframeEngine;

/// <summary>
/// The linear locks and limits of a <see cref="Generic6DofJoint3D"/> (ADR 0234): three rows along frame A's axes, each
/// measuring where B's anchor is from A's, solved like Jitter2's own constraints (its linear softness and bias).
/// </summary>
internal sealed unsafe class SixDofLinear : Constraint<SixDofLinear.Values>
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Values
    {
        public JitterConstraintHeader Header;
        public JVector LocalAnchor1, LocalAnchor2;
        public JQuaternion LocalFrame1;

        /// <summary><see cref="JointAxisMode"/> per axis, two bits each.</summary>
        public uint Modes;

        /// <summary><see cref="SixDofClamp"/> per axis this substep.</summary>
        public uint Clamps;

        public SixDofLimitRows Rows;

        // Per substep: the arms from each body to B's anchor, and frame A's axes in the world.
        public JVector R1, R2;
        public JVector Axis0, Axis1, Axis2;
    }

    /// <summary><see cref="Values.Modes"/> with every axis <see cref="JointAxisMode.Locked"/>.</summary>
    private const uint AllLocked = (uint)JointAxisMode.Locked * 0b010101;

    private static readonly uint s_dispatchId = RegisterFullConstraint(&Prepare, &Iterate);

    protected override void Create()
    {
        DispatchId = s_dispatchId;
        base.Create();
    }

    public override void ResetWarmStart()
    {
        ref var data = ref Data;
        for (var i = 0; i < 3; i++)
            data.Rows[i].Impulse = 0f;
    }

    /// <summary>The last substep's linear impulse (N·s), all rows.</summary>
    public float Impulse
    {
        get
        {
            ref var data = ref Data;
            return MathF.Abs(data.Rows[0].Impulse) + MathF.Abs(data.Rows[1].Impulse) + MathF.Abs(data.Rows[2].Impulse);
        }
    }

    private static ref Vector3 AxisOf(ref Values data, int i)
    {
        if (i == 0)
            return ref V(ref data.Axis0);
        if (i == 1)
            return ref V(ref data.Axis1);
        return ref V(ref data.Axis2);
    }

    private static void Prepare(ref ConstraintData constraint, float idt)
    {
        ref var data = ref Unsafe.As<ConstraintData, Values>(ref constraint);
        ref var body1 = ref data.Header.Body1.Data;
        ref var body2 = ref data.Header.Body2.Data;

        var frameA = Quaternion.Normalize(Q(body1.Orientation) * Q(data.LocalFrame1));
        var x1 = V(ref body1.Position);
        var x2 = V(ref body2.Position);
        var pA = x1 + Vector3.Transform(V(ref data.LocalAnchor1), Q(body1.Orientation));
        var pB = x2 + Vector3.Transform(V(ref data.LocalAnchor2), Q(body2.Orientation));
        var d = pB - pA;
        // Every axis locked is a ball socket: rows along world axes at each body's own anchor (as Jitter2's BallSocket),
        // which stay well conditioned however fast body 1 spins. Otherwise rows along frame A's axes, measured at B's
        // anchor (the axes turn with body 1, so its arm reaches B's anchor).
        var socket = data.Modes == AllLocked;
        var r1 = socket ? pA - x1 : pB - x1;
        var r2 = pB - x2;
        V(ref data.R1) = r1;
        V(ref data.R2) = r2;

        for (var i = 0; i < 3; i++)
        {
            var n = socket ? Axis(i) : Vector3.Transform(Axis(i), frameA);
            AxisOf(ref data, i) = n;
            ref var row = ref data.Rows[i];
            var clamp = SixDofMath.Clamp((JointAxisMode)Get(data.Modes, i), Vector3.Dot(d, n), row.Lower, row.Upper, angular: false, out var error, out var inside);
            data.Clamps = Set(data.Clamps, i, (uint)clamp);
            if (clamp == SixDofClamp.Inactive)
            {
                row.Impulse = 0f;
                continue;
            }

            var r1n = Vector3.Cross(r1, n);
            var r2n = Vector3.Cross(r2, n);
            var k = InverseMass(ref body1, n) + InverseMass(ref body2, n) + Vector3.Dot(r1n, Mul(body1.InverseInertiaWorld, r1n)) +
                    Vector3.Dot(r2n, Mul(body2.InverseInertiaWorld, r2n));
            var locked = clamp == SixDofClamp.Locked;
            var softness = locked ? Constraint.DefaultLinearSoftness : Constraint.DefaultLinearLimitSoftness;
            var bias = locked ? Constraint.DefaultLinearBias : Constraint.DefaultLinearLimitBias;
            row.EffectiveMass = 1f / (k + softness * idt);
            row.Bias = LimitBias(error, inside, bias, idt);
            row.Impulse = ClampImpulse(clamp, row.Impulse);
            ApplyLinear(ref body1, ref body2, n, r1n, r2n, row.Impulse); // warm start
        }
    }

    private static void Iterate(ref ConstraintData constraint, float idt)
    {
        ref var data = ref Unsafe.As<ConstraintData, Values>(ref constraint);
        ref var body1 = ref data.Header.Body1.Data;
        ref var body2 = ref data.Header.Body2.Data;
        var r1 = V(ref data.R1);
        var r2 = V(ref data.R2);
        for (var i = 0; i < 3; i++)
        {
            var clamp = (SixDofClamp)Get(data.Clamps, i);
            if (clamp == SixDofClamp.Inactive)
                continue;
            ref var row = ref data.Rows[i];
            var n = AxisOf(ref data, i);
            var r1n = Vector3.Cross(r1, n);
            var r2n = Vector3.Cross(r2, n);
            var softness = clamp == SixDofClamp.Locked ? Constraint.DefaultLinearSoftness : Constraint.DefaultLinearLimitSoftness;
            var cdot = LinearVelocity(ref body1, ref body2, n, r1n, r2n);
            var lambda = -row.EffectiveMass * (cdot + row.Bias + softness * idt * row.Impulse);
            var old = row.Impulse;
            row.Impulse = ClampImpulse(clamp, old + lambda);
            ApplyLinear(ref body1, ref body2, n, r1n, r2n, row.Impulse - old);
        }
    }
}

/// <summary>
/// The linear motors and springs of a <see cref="Generic6DofJoint3D"/> (ADR 0234): per axis of frame A, a velocity row
/// capped by the motor's force and a soft row (<see cref="SoftRow"/>) towards the spring's target. Rows with neither
/// cost nothing. The axes are recomputed each iteration from body 1 (the data has no room to keep them).
/// </summary>
internal sealed unsafe class SixDofLinearDrive : Constraint<SixDofLinearDrive.Values>
{
    /// <summary>Bit i: axis i's motor; bit 3 + i: its spring.</summary>
    public const uint MotorBits = 0b000111, SpringBits = 0b111000;

    [StructLayout(LayoutKind.Sequential)]
    public struct Values
    {
        public JitterConstraintHeader Header;
        public JVector LocalAnchor1, LocalAnchor2;
        public JQuaternion LocalFrame1;
        public uint Flags;
        public SixDofDriveRows Rows;
        public JVector R1, R2;
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
        for (var i = 0; i < 3; i++)
            data.Rows[i].MotorImpulse = data.Rows[i].SpringImpulse = 0f;
    }

    /// <summary>The last substep's motor and spring impulse (N·s), all rows.</summary>
    public float Impulse
    {
        get
        {
            ref var data = ref Data;
            var sum = 0f;
            for (var i = 0; i < 3; i++)
                sum += MathF.Abs(data.Rows[i].MotorImpulse) + MathF.Abs(data.Rows[i].SpringImpulse);
            return sum;
        }
    }

    private static void Prepare(ref ConstraintData constraint, float idt)
    {
        ref var data = ref Unsafe.As<ConstraintData, Values>(ref constraint);
        if (data.Flags == 0)
            return;
        ref var body1 = ref data.Header.Body1.Data;
        ref var body2 = ref data.Header.Body2.Data;

        var frameA = Quaternion.Normalize(Q(body1.Orientation) * Q(data.LocalFrame1));
        var x1 = V(ref body1.Position);
        var x2 = V(ref body2.Position);
        var pA = x1 + Vector3.Transform(V(ref data.LocalAnchor1), Q(body1.Orientation));
        var pB = x2 + Vector3.Transform(V(ref data.LocalAnchor2), Q(body2.Orientation));
        var r1 = pB - x1;
        var r2 = pB - x2;
        var d = pB - pA;
        V(ref data.R1) = r1;
        V(ref data.R2) = r2;
        var h = 1f / idt;

        for (var i = 0; i < 3; i++)
        {
            ref var row = ref data.Rows[i];
            var motor = (data.Flags & (1u << i)) != 0;
            var spring = (data.Flags & (8u << i)) != 0;
            if (!motor)
                row.MotorImpulse = 0f;
            if (!spring)
                row.SpringImpulse = 0f;
            if (!motor && !spring)
                continue;

            var n = Vector3.Transform(Axis(i), frameA);
            var r1n = Vector3.Cross(r1, n);
            var r2n = Vector3.Cross(r2, n);
            var k = InverseMass(ref body1, n) + InverseMass(ref body2, n) + Vector3.Dot(r1n, Mul(body1.InverseInertiaWorld, r1n)) +
                    Vector3.Dot(r2n, Mul(body2.InverseInertiaWorld, r2n));
            row.EffectiveMass = k > 0f ? 1f / k : 0f;
            if (spring)
            {
                SoftRow.Coefficients(row.SpringFrequency, row.SpringDampingRatio, h, out var biasRate, out row.SpringMassScale);
                row.SpringBias = biasRate * (Vector3.Dot(d, n) - row.SpringTarget);
            }

            ApplyLinear(ref body1, ref body2, n, r1n, r2n, row.MotorImpulse + row.SpringImpulse); // warm start
        }
    }

    private static void Iterate(ref ConstraintData constraint, float idt)
    {
        ref var data = ref Unsafe.As<ConstraintData, Values>(ref constraint);
        if (data.Flags == 0)
            return;
        ref var body1 = ref data.Header.Body1.Data;
        ref var body2 = ref data.Header.Body2.Data;
        var frameA = Quaternion.Normalize(Q(body1.Orientation) * Q(data.LocalFrame1));
        var r1 = V(ref data.R1);
        var r2 = V(ref data.R2);
        for (var i = 0; i < 3; i++)
        {
            var motor = (data.Flags & (1u << i)) != 0;
            var spring = (data.Flags & (8u << i)) != 0;
            if (!motor && !spring)
                continue;
            ref var row = ref data.Rows[i];
            var n = Vector3.Transform(Axis(i), frameA);
            var r1n = Vector3.Cross(r1, n);
            var r2n = Vector3.Cross(r2, n);
            if (motor)
            {
                var cdot = LinearVelocity(ref body1, ref body2, n, r1n, r2n);
                var max = row.MotorMaxForce / idt;
                var old = row.MotorImpulse;
                row.MotorImpulse = Math.Clamp(old - row.EffectiveMass * (cdot - row.MotorVelocity), -max, max);
                ApplyLinear(ref body1, ref body2, n, r1n, r2n, row.MotorImpulse - old);
            }

            if (spring)
            {
                var cdot = LinearVelocity(ref body1, ref body2, n, r1n, r2n);
                var lambda = -row.SpringMassScale * row.EffectiveMass * (cdot + row.SpringBias) - (1f - row.SpringMassScale) * row.SpringImpulse;
                row.SpringImpulse += lambda;
                ApplyLinear(ref body1, ref body2, n, r1n, r2n, lambda);
            }
        }
    }
}

using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Jitter2.Dynamics;
using Jitter2.Dynamics.Constraints;
using Jitter2.LinearMath;
using static MainframeEngine.SixDofMath;

namespace MainframeEngine;

/// <summary>
/// The angular locks and limits of a <see cref="Generic6DofJoint3D"/> (ADR 0234): the rotation of frame B in frame A
/// as XYZ Euler angles (<see cref="SixDofMath.EulerXYZ"/>), one row per angle about its axis
/// (<see cref="SixDofMath.AngularAxes"/>), solved like Jitter2's own constraints (its angular softness and bias).
/// </summary>
internal sealed unsafe class SixDofAngular : Constraint<SixDofAngular.Values>
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Values
    {
        public JitterConstraintHeader Header;
        public JQuaternion LocalFrame1, LocalFrame2;
        public uint Modes;
        public uint Clamps;
        public SixDofLimitRows Rows;
        public JVector Axis0, Axis1, Axis2;
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
            data.Rows[i].Impulse = 0f;
    }

    /// <summary>The last substep's angular impulse (N·m·s), all rows.</summary>
    public float Impulse
    {
        get
        {
            ref var data = ref Data;
            return MathF.Abs(data.Rows[0].Impulse) + MathF.Abs(data.Rows[1].Impulse) + MathF.Abs(data.Rows[2].Impulse);
        }
    }

    internal static ref Vector3 AxisOf(ref JVector axis0, ref JVector axis1, ref JVector axis2, int i)
    {
        if (i == 0)
            return ref V(ref axis0);
        if (i == 1)
            return ref V(ref axis1);
        return ref V(ref axis2);
    }

    private static void Prepare(ref ConstraintData constraint, float idt)
    {
        ref var data = ref Unsafe.As<ConstraintData, Values>(ref constraint);
        ref var body1 = ref data.Header.Body1.Data;
        ref var body2 = ref data.Header.Body2.Data;

        WorldFrames(ref body1, ref body2, data.LocalFrame1, data.LocalFrame2, out var frameA, out var frameB);
        var angles = EulerXYZ(Relative(frameA, frameB));
        AngularAxes(frameA, frameB, out V(ref data.Axis0), out V(ref data.Axis1), out V(ref data.Axis2));

        for (var i = 0; i < 3; i++)
        {
            ref var row = ref data.Rows[i];
            var clamp = SixDofMath.Clamp((JointAxisMode)Get(data.Modes, i), angles[i], row.Lower, row.Upper, angular: true, out var error, out var inside);
            data.Clamps = Set(data.Clamps, i, (uint)clamp);
            if (clamp == SixDofClamp.Inactive)
            {
                row.Impulse = 0f;
                continue;
            }

            var axis = AxisOf(ref data.Axis0, ref data.Axis1, ref data.Axis2, i);
            var k = Vector3.Dot(axis, Mul(body1.InverseInertiaWorld, axis) + Mul(body2.InverseInertiaWorld, axis));
            var locked = clamp == SixDofClamp.Locked;
            var softness = locked ? Constraint.DefaultAngularSoftness : Constraint.DefaultAngularLimitSoftness;
            var bias = locked ? Constraint.DefaultAngularBias : Constraint.DefaultAngularLimitBias;
            row.EffectiveMass = 1f / (k + softness * idt);
            row.Bias = LimitBias(error, inside, bias, idt);
            row.Impulse = ClampImpulse(clamp, row.Impulse);
            ApplyAngular(ref body1, ref body2, axis, row.Impulse); // warm start
        }
    }

    private static void Iterate(ref ConstraintData constraint, float idt)
    {
        ref var data = ref Unsafe.As<ConstraintData, Values>(ref constraint);
        ref var body1 = ref data.Header.Body1.Data;
        ref var body2 = ref data.Header.Body2.Data;
        for (var i = 0; i < 3; i++)
        {
            var clamp = (SixDofClamp)Get(data.Clamps, i);
            if (clamp == SixDofClamp.Inactive)
                continue;
            ref var row = ref data.Rows[i];
            var axis = AxisOf(ref data.Axis0, ref data.Axis1, ref data.Axis2, i);
            var softness = clamp == SixDofClamp.Locked ? Constraint.DefaultAngularSoftness : Constraint.DefaultAngularLimitSoftness;
            var cdot = Vector3.Dot(axis, V(ref body2.AngularVelocity) - V(ref body1.AngularVelocity));
            var lambda = -row.EffectiveMass * (cdot + row.Bias + softness * idt * row.Impulse);
            var old = row.Impulse;
            row.Impulse = ClampImpulse(clamp, old + lambda);
            ApplyAngular(ref body1, ref body2, axis, row.Impulse - old);
        }
    }
}

/// <summary>
/// The angular motors and springs of a <see cref="Generic6DofJoint3D"/> (ADR 0234): per Euler angle, a velocity row
/// capped by the motor's torque and a soft row (<see cref="SoftRow"/>) towards the spring's target angle.
/// </summary>
internal sealed unsafe class SixDofAngularDrive : Constraint<SixDofAngularDrive.Values>
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Values
    {
        public JitterConstraintHeader Header;
        public JQuaternion LocalFrame1, LocalFrame2;

        /// <summary>Bit i: axis i's motor; bit 3 + i: its spring.</summary>
        public uint Flags;

        public SixDofDriveRows Rows;
        public JVector Axis0, Axis1, Axis2;
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

    /// <summary>The last substep's spring impulse (N·m·s), all rows: motors don't count towards breaking (ADR 0211).</summary>
    public float SpringImpulse
    {
        get
        {
            ref var data = ref Data;
            return MathF.Abs(data.Rows[0].SpringImpulse) + MathF.Abs(data.Rows[1].SpringImpulse) + MathF.Abs(data.Rows[2].SpringImpulse);
        }
    }

    private static void Prepare(ref ConstraintData constraint, float idt)
    {
        ref var data = ref Unsafe.As<ConstraintData, Values>(ref constraint);
        if (data.Flags == 0)
            return;
        ref var body1 = ref data.Header.Body1.Data;
        ref var body2 = ref data.Header.Body2.Data;

        WorldFrames(ref body1, ref body2, data.LocalFrame1, data.LocalFrame2, out var frameA, out var frameB);
        var angles = EulerXYZ(Relative(frameA, frameB));
        AngularAxes(frameA, frameB, out V(ref data.Axis0), out V(ref data.Axis1), out V(ref data.Axis2));
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

            var axis = SixDofAngular.AxisOf(ref data.Axis0, ref data.Axis1, ref data.Axis2, i);
            var k = Vector3.Dot(axis, Mul(body1.InverseInertiaWorld, axis) + Mul(body2.InverseInertiaWorld, axis));
            row.EffectiveMass = k > 0f ? 1f / k : 0f;
            if (spring)
            {
                SoftRow.Coefficients(row.SpringFrequency, row.SpringDampingRatio, h, out var biasRate, out row.SpringMassScale);
                row.SpringBias = biasRate * AngleDifference(angles[i], row.SpringTarget);
            }

            ApplyAngular(ref body1, ref body2, axis, row.MotorImpulse + row.SpringImpulse); // warm start
        }
    }

    private static void Iterate(ref ConstraintData constraint, float idt)
    {
        ref var data = ref Unsafe.As<ConstraintData, Values>(ref constraint);
        if (data.Flags == 0)
            return;
        ref var body1 = ref data.Header.Body1.Data;
        ref var body2 = ref data.Header.Body2.Data;
        for (var i = 0; i < 3; i++)
        {
            var motor = (data.Flags & (1u << i)) != 0;
            var spring = (data.Flags & (8u << i)) != 0;
            if (!motor && !spring)
                continue;
            ref var row = ref data.Rows[i];
            var axis = SixDofAngular.AxisOf(ref data.Axis0, ref data.Axis1, ref data.Axis2, i);
            if (motor)
            {
                var cdot = Vector3.Dot(axis, V(ref body2.AngularVelocity) - V(ref body1.AngularVelocity));
                var max = row.MotorMaxForce / idt;
                var old = row.MotorImpulse;
                row.MotorImpulse = Math.Clamp(old - row.EffectiveMass * (cdot - row.MotorVelocity), -max, max);
                ApplyAngular(ref body1, ref body2, axis, row.MotorImpulse - old);
            }

            if (spring)
            {
                var cdot = Vector3.Dot(axis, V(ref body2.AngularVelocity) - V(ref body1.AngularVelocity));
                var lambda = -row.SpringMassScale * row.EffectiveMass * (cdot + row.SpringBias) - (1f - row.SpringMassScale) * row.SpringImpulse;
                row.SpringImpulse += lambda;
                ApplyAngular(ref body1, ref body2, axis, lambda);
            }
        }
    }
}

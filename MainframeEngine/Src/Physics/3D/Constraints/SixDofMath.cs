using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Jitter2.Dynamics;
using Jitter2.LinearMath;
using Jitter2.Unmanaged;

namespace MainframeEngine;

/// <summary>
/// Jitter2's <c>ConstraintData</c> header (2.9.0): every custom constraint's data starts with it, in this layout, so
/// Jitter2 can read its dispatch id and bodies (ADR 0234). 32 bytes; a constraint's own fields get the rest of
/// <c>Precision.ConstraintSizeFull</c> (256).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct JitterConstraintHeader
{
    private int _internal;
    private uint _dispatchId;
    private ulong _constraintId;
    public JHandle<RigidBodyData> Body1;
    public JHandle<RigidBodyData> Body2;
}

/// <summary>A lock or limit row of a 6-DOF constraint (one axis).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SixDofLimitRow
{
    public float Lower, Upper;
    public float EffectiveMass, Bias, Impulse;
}

/// <summary>A motor and spring row of a 6-DOF constraint (one axis).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SixDofDriveRow
{
    public float MotorVelocity, MotorMaxForce;
    public float SpringTarget, SpringFrequency, SpringDampingRatio;
    public float SpringMassScale, EffectiveMass, SpringBias;
    public float MotorImpulse, SpringImpulse;
}

[InlineArray(3)]
internal struct SixDofLimitRows
{
    private SixDofLimitRow _row;
}

[InlineArray(3)]
internal struct SixDofDriveRows
{
    private SixDofDriveRow _row;
}

/// <summary>The rows' state, two bits per axis: how a limit row is solved this substep.</summary>
internal enum SixDofClamp : uint
{
    Inactive = 0,
    Upper = 1,
    Lower = 2,
    Locked = 3,
}

/// <summary>
/// The math the 6-DOF constraints share: frames, bit-packed per-axis state, applying impulses, and the relative
/// rotation's XYZ Euler angles (as Bullet's generic 6-DOF joint, zlib licence: <c>matrixToEulerXYZ</c>) with each
/// angle's constraint axis. Every method is static and touches
/// only its arguments: Jitter2 calls the constraints from its worker threads.
/// </summary>
internal static class SixDofMath
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3 Axis(int index) => index switch { 0 => Vector3.UnitX, 1 => Vector3.UnitY, _ => Vector3.UnitZ };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Get(uint packed, int axis) => (packed >> (axis * 2)) & 3u;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Set(uint packed, int axis, uint value) => (packed & ~(3u << (axis * 2))) | ((value & 3u) << (axis * 2));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ref Vector3 V(ref JVector v) => ref Unsafe.As<JVector, Vector3>(ref v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Quaternion Q(in JQuaternion q) => new(q.X, q.Y, q.Z, q.W);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3 Mul(in JSymmetricMatrix m, Vector3 v) => JVector.Transform(v.ToJ(), m).ToNumerics();

    /// <summary>The inverse mass of <paramref name="body"/> along unit <paramref name="n"/> (Jitter2 keeps one per axis).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float InverseMass(ref RigidBodyData body, Vector3 n) => Vector3.Dot(n, V(ref body.InverseMassVector) * n);

    /// <summary>Applies a linear impulse <paramref name="lambda"/> along <paramref name="n"/>, at arms r1 (body 1) and r2 (body 2) crossed with n.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ApplyLinear(ref RigidBodyData body1, ref RigidBodyData body2, Vector3 n, Vector3 r1n, Vector3 r2n, float lambda)
    {
        V(ref body1.Velocity) -= V(ref body1.InverseMassVector) * n * lambda;
        V(ref body1.AngularVelocity) -= Mul(body1.InverseInertiaWorld, r1n) * lambda;
        V(ref body2.Velocity) += V(ref body2.InverseMassVector) * n * lambda;
        V(ref body2.AngularVelocity) += Mul(body2.InverseInertiaWorld, r2n) * lambda;
    }

    /// <summary>Applies an angular impulse <paramref name="lambda"/> about <paramref name="axis"/>: body 2 one way, body 1 the other.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ApplyAngular(ref RigidBodyData body1, ref RigidBodyData body2, Vector3 axis, float lambda)
    {
        V(ref body1.AngularVelocity) -= Mul(body1.InverseInertiaWorld, axis) * lambda;
        V(ref body2.AngularVelocity) += Mul(body2.InverseInertiaWorld, axis) * lambda;
    }

    /// <summary>The linear rows' relative velocity along <paramref name="n"/> (B's anchor relative to A's frame).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float LinearVelocity(ref RigidBodyData body1, ref RigidBodyData body2, Vector3 n, Vector3 r1n, Vector3 r2n) =>
        Vector3.Dot(n, V(ref body2.Velocity) - V(ref body1.Velocity)) + Vector3.Dot(V(ref body2.AngularVelocity), r2n) -
        Vector3.Dot(V(ref body1.AngularVelocity), r1n);

    /// <summary>
    /// The XYZ Euler angles (radians) of <paramref name="relative"/>, the rotation of frame B in frame A: the rotation is
    /// <c>Rx(x)·Ry(y)·Rz(z)</c> acting on column vectors. Y is in −π/2…π/2.
    /// </summary>
    public static Vector3 EulerXYZ(Quaternion relative)
    {
        var (x, y, z, w) = (relative.X, relative.Y, relative.Z, relative.W);
        var m00 = 1f - 2f * (y * y + z * z);
        var m01 = 2f * (x * y - w * z);
        var m02 = 2f * (x * z + w * y);
        var m10 = 2f * (x * y + w * z);
        var m11 = 1f - 2f * (x * x + z * z);
        var m12 = 2f * (y * z - w * x);
        var m22 = 1f - 2f * (x * x + y * y);
        if (m02 < 1f)
        {
            if (m02 > -1f)
                return new Vector3(MathF.Atan2(-m12, m22), MathF.Asin(m02), MathF.Atan2(-m01, m00));
            return new Vector3(-MathF.Atan2(m10, m11), -MathF.PI / 2f, 0f);
        }

        return new Vector3(MathF.Atan2(m10, m11), MathF.PI / 2f, 0f);
    }

    /// <summary>
    /// The angular rows' axes (world) for frames A and B in the world: each Euler angle's rate is
    /// <c>axis · (ωB − ωA)</c>. For <c>R = Rx·Ry·Rz</c> the relative angular velocity is <c>ẋ·U0 + ẏ·U1 + ż·U2</c> with
    /// U0 = A's X, U2 = B's Z and U1 = U2 × U0 (normalised), so the axes are that basis's dual: not unit, and growing
    /// as the Y angle nears ±90° (gimbal lock; the determinant is clamped).
    /// </summary>
    public static void AngularAxes(Quaternion frameA, Quaternion frameB, out Vector3 axis0, out Vector3 axis1, out Vector3 axis2)
    {
        var u0 = Vector3.Transform(Vector3.UnitX, frameA);
        var u2 = Vector3.Transform(Vector3.UnitZ, frameB);
        var u1 = SafeNormalize(Vector3.Cross(u2, u0), Vector3.Transform(Vector3.UnitY, frameA));
        var c12 = Vector3.Cross(u1, u2);
        var det = Vector3.Dot(u0, c12);
        det = det >= 0f ? MathF.Max(det, 1e-3f) : MathF.Min(det, -1e-3f);
        axis0 = c12 / det;
        axis1 = Vector3.Cross(u2, u0) / det;
        axis2 = Vector3.Cross(u0, u1) / det;
    }

    private static Vector3 SafeNormalize(Vector3 v, Vector3 fallback)
    {
        var length = v.Length();
        return length > 1e-6f ? v / length : fallback;
    }

    /// <summary>Frame A and frame B in the world for bodies at their current poses.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WorldFrames(ref RigidBodyData body1, ref RigidBodyData body2, in JQuaternion local1, in JQuaternion local2,
        out Quaternion frameA, out Quaternion frameB)
    {
        frameA = Quaternion.Normalize(Q(body1.Orientation) * Q(local1));
        frameB = Quaternion.Normalize(Q(body2.Orientation) * Q(local2));
    }

    /// <summary>The relative rotation of frame B in frame A.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Quaternion Relative(Quaternion frameA, Quaternion frameB) => Quaternion.Normalize(Quaternion.Conjugate(frameA) * frameB);

    /// <summary>The shortest signed difference <paramref name="a"/> − <paramref name="b"/> in −π…π.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float AngleDifference(float a, float b)
    {
        var d = a - b;
        if (d > MathF.PI)
            d -= MathF.Tau;
        else if (d < -MathF.PI)
            d += MathF.Tau;
        return d;
    }

    /// <summary>
    /// The limit row's state and position error for an offset <paramref name="value"/> under <paramref name="mode"/>
    /// (angles when <paramref name="angular"/>: errors wrap to −π…π). A limited axis is always solved against its
    /// nearer limit, speculatively (Box2D's limits): <paramref name="inside"/> is true while the offset is within its
    /// limits, and the row then only stops the body from crossing the limit this step. A free axis is inactive.
    /// </summary>
    public static SixDofClamp Clamp(JointAxisMode mode, float value, float lower, float upper, bool angular, out float error, out bool inside)
    {
        error = 0f;
        inside = false;
        switch (mode)
        {
            case JointAxisMode.Locked:
                error = angular ? AngleDifference(value, 0f) : value;
                return SixDofClamp.Locked;
            case JointAxisMode.Limited when lower >= upper:
                error = angular ? AngleDifference(value, lower) : value - lower;
                return SixDofClamp.Locked;
            case JointAxisMode.Limited when value - lower <= upper - value:
                error = angular ? AngleDifference(value, lower) : value - lower;
                inside = error > 0f;
                return SixDofClamp.Lower;
            case JointAxisMode.Limited:
                error = angular ? AngleDifference(value, upper) : value - upper;
                inside = error < 0f;
                return SixDofClamp.Upper;
            default:
                return SixDofClamp.Inactive;
        }
    }

    /// <summary>
    /// A limit or lock row's velocity bias for a position <paramref name="error"/>: inside the limits the error itself
    /// over the step (the body may close the gap, not cross it), outside Jitter2's Baumgarte <paramref name="biasFactor"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float LimitBias(float error, bool inside, float biasFactor, float idt) => inside ? error * idt : error * biasFactor * idt;

    /// <summary>Clamps a limit row's accumulated impulse to its side: a lower limit only pushes, an upper one only pulls.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float ClampImpulse(SixDofClamp clamp, float impulse) => clamp switch
    {
        SixDofClamp.Lower => MathF.Max(impulse, 0f),
        SixDofClamp.Upper => MathF.Min(impulse, 0f),
        _ => impulse,
    };
}

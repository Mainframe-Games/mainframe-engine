using System.Numerics;
using System.Runtime.CompilerServices;
using Jitter2.LinearMath;

namespace MainframeEngine;

/// <summary>Conversions between System.Numerics and Jitter2 math types (same conventions: right-handed, Hamilton quaternions).</summary>
internal static class JitterMath
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static JVector ToJ(this Vector3 v) => new(v.X, v.Y, v.Z);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3 ToNumerics(this in JVector v) => new(v.X, v.Y, v.Z);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static JQuaternion ToJ(this Quaternion q) => new(q.X, q.Y, q.Z, q.W);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Quaternion ToNumerics(this in JQuaternion q) => new(q.X, q.Y, q.Z, q.W);

    /// <summary>A basis (column vectors) as a Jitter matrix acting on column vectors.</summary>
    public static JMatrix ToJ(this in Basis b) => JMatrix.FromColumns(b.X.ToJ(), b.Y.ToJ(), b.Z.ToJ());

    public static bool IsIdentity(in Basis b) => b == Basis.Identity;
}

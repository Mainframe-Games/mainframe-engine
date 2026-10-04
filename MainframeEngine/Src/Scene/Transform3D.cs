using System.Numerics;
using System.Runtime.CompilerServices;

namespace MainframeEngine;

/// <summary>
/// A 3×3 linear transform stored as three column vectors: the images of the X, Y and Z unit axes (rotation
/// and scale). <c>-Z</c> is forward, <c>+Y</c> is up (see docs/design/coordinate-conventions.md).
/// </summary>
public struct Basis : IEquatable<Basis>
{
    public Vector3 X;
    public Vector3 Y;
    public Vector3 Z;

    public Basis(Vector3 x, Vector3 y, Vector3 z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public static Basis Identity => new(Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ);

    /// <summary>Rotation from a (normalized) quaternion.</summary>
    public static Basis FromQuaternion(Quaternion rotation)
    {
        var m = Matrix4x4.CreateFromQuaternion(rotation);
        return new Basis(new Vector3(m.M11, m.M12, m.M13), new Vector3(m.M21, m.M22, m.M23), new Vector3(m.M31, m.M32, m.M33));
    }

    /// <summary>Rotation then scale along the local axes: columns are the rotated axes times the scale.</summary>
    public static Basis FromRotationScale(Quaternion rotation, Vector3 scale)
    {
        var b = FromQuaternion(rotation);
        return new Basis(b.X * scale.X, b.Y * scale.Y, b.Z * scale.Z);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly Vector3 Transform(Vector3 v) => X * v.X + Y * v.Y + Z * v.Z;

    public static Basis operator *(Basis a, Basis b) => new(a.Transform(b.X), a.Transform(b.Y), a.Transform(b.Z));

    public static Vector3 operator *(Basis a, Vector3 v) => a.Transform(v);

    public static Basis Multiply(Basis a, Basis b) => a * b;

    /// <summary>Length of each column (the scale, if the basis has no shear).</summary>
    public readonly Vector3 GetScale() => new(X.Length(), Y.Length(), Z.Length());

    public readonly float Determinant() => Vector3.Dot(X, Vector3.Cross(Y, Z));

    /// <summary>General inverse (throws for a singular basis).</summary>
    public readonly Basis Inverse()
    {
        // Rows of the inverse are the cross products of the columns over the determinant.
        var det = Determinant();
        if (MathF.Abs(det) < 1e-12f)
            throw new InvalidOperationException("Basis is singular (zero scale) and cannot be inverted.");
        var r0 = Vector3.Cross(Y, Z) / det;
        var r1 = Vector3.Cross(Z, X) / det;
        var r2 = Vector3.Cross(X, Y) / det;
        return new Basis(new Vector3(r0.X, r1.X, r2.X), new Vector3(r0.Y, r1.Y, r2.Y), new Vector3(r0.Z, r1.Z, r2.Z));
    }

    /// <summary>Rotation part as a quaternion (scale removed by normalizing the columns).</summary>
    public readonly Quaternion GetRotation()
    {
        var x = Vector3.Normalize(X);
        var y = Vector3.Normalize(Y);
        var z = Vector3.Normalize(Z);
        if (Determinant() < 0)
            x = -x; // a negative scale on X keeps the rest a proper rotation
        var m = new Matrix4x4(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, 0, 0, 0, 1);
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(m));
    }

    public readonly bool Equals(Basis other) => X == other.X && Y == other.Y && Z == other.Z;

    public override readonly bool Equals(object? obj) => obj is Basis other && Equals(other);

    public override readonly int GetHashCode() => HashCode.Combine(X, Y, Z);

    public static bool operator ==(Basis left, Basis right) => left.Equals(right);

    public static bool operator !=(Basis left, Basis right) => !left.Equals(right);

    public override readonly string ToString() => $"[X: {X}, Y: {Y}, Z: {Z}]";
}

/// <summary>
/// An affine 3D transform: a <see cref="MainframeEngine.Basis"/> (rotation/scale/shear) and an origin, as in
/// Godot. <c>parent * child</c> composes child-in-parent; <see cref="ToMatrix4x4"/> gives the
/// <c>System.Numerics</c> (row-vector) model matrix: <c>Scale × Rotation × Translation</c>.
/// </summary>
public struct Transform3D : IEquatable<Transform3D>
{
    public Basis Basis;
    public Vector3 Origin;

    public Transform3D(Basis basis, Vector3 origin)
    {
        Basis = basis;
        Origin = origin;
    }

    public static Transform3D Identity => new(Basis.Identity, Vector3.Zero);

    /// <summary>Translation, rotation and scale (applied scale → rotation → translation).</summary>
    public static Transform3D FromTrs(Vector3 translation, Quaternion rotation, Vector3 scale) =>
        new(Basis.FromRotationScale(rotation, scale), translation);

    /// <summary>Builds a transform from a row-vector affine matrix (the inverse of <see cref="ToMatrix4x4"/>).</summary>
    public static Transform3D FromMatrix4x4(in Matrix4x4 m) =>
        new(new Basis(new Vector3(m.M11, m.M12, m.M13), new Vector3(m.M21, m.M22, m.M23), new Vector3(m.M31, m.M32, m.M33)),
            new Vector3(m.M41, m.M42, m.M43));

    /// <summary>Row-vector model matrix: rows are the X, Y, Z axes and the origin.</summary>
    public readonly Matrix4x4 ToMatrix4x4() => new(
        Basis.X.X, Basis.X.Y, Basis.X.Z, 0,
        Basis.Y.X, Basis.Y.Y, Basis.Y.Z, 0,
        Basis.Z.X, Basis.Z.Y, Basis.Z.Z, 0,
        Origin.X, Origin.Y, Origin.Z, 1);

    /// <summary>Transforms a point.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly Vector3 TransformPoint(Vector3 point) => Basis.Transform(point) + Origin;

    /// <summary>Transforms a direction (ignores the origin).</summary>
    public readonly Vector3 TransformDirection(Vector3 direction) => Basis.Transform(direction);

    public static Transform3D operator *(Transform3D parent, Transform3D child) =>
        new(parent.Basis * child.Basis, parent.TransformPoint(child.Origin));

    public static Vector3 operator *(Transform3D t, Vector3 point) => t.TransformPoint(point);

    public static Transform3D Multiply(Transform3D parent, Transform3D child) => parent * child;

    /// <summary>Inverse of an affine transform (any invertible basis).</summary>
    public readonly Transform3D AffineInverse()
    {
        var inv = Basis.Inverse();
        return new Transform3D(inv, -inv.Transform(Origin));
    }

    /// <summary>Splits into translation, rotation and scale. Shear is lost.</summary>
    public readonly void Decompose(out Vector3 translation, out Quaternion rotation, out Vector3 scale)
    {
        translation = Origin;
        scale = Basis.GetScale();
        if (Basis.Determinant() < 0)
            scale.X = -scale.X;
        rotation = Basis.GetRotation();
    }

    /// <summary>
    /// A transform at <paramref name="eye"/> whose <c>-Z</c> axis points at <paramref name="target"/>
    /// (Godot's <c>looking_at</c>). <paramref name="up"/> must not be parallel to the view direction.
    /// </summary>
    public static Transform3D LookingAt(Vector3 eye, Vector3 target, Vector3 up) =>
        new(BasisLookingAlong(target - eye, up), eye);

    /// <summary>Rotation whose <c>-Z</c> axis points along <paramref name="direction"/>.</summary>
    public static Basis BasisLookingAlong(Vector3 direction, Vector3 up)
    {
        if (direction.LengthSquared() < 1e-12f)
            throw new ArgumentException("Look direction is zero.", nameof(direction));
        var z = -Vector3.Normalize(direction);
        var x = Vector3.Cross(up, z);
        if (x.LengthSquared() < 1e-12f)
        {
            // Looking straight along `up`: pick any perpendicular axis.
            x = Vector3.Cross(MathF.Abs(z.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX, z);
        }

        x = Vector3.Normalize(x);
        var y = Vector3.Cross(z, x);
        return new Basis(x, y, z);
    }

    public readonly bool Equals(Transform3D other) => Basis == other.Basis && Origin == other.Origin;

    public override readonly bool Equals(object? obj) => obj is Transform3D other && Equals(other);

    public override readonly int GetHashCode() => HashCode.Combine(Basis, Origin);

    public static bool operator ==(Transform3D left, Transform3D right) => left.Equals(right);

    public static bool operator !=(Transform3D left, Transform3D right) => !left.Equals(right);

    public override readonly string ToString() => $"[Basis: {Basis}, Origin: {Origin}]";
}

/// <summary>
/// Euler conversions with the engine's convention: rotations in degrees applied about X, then Y, then Z
/// (extrinsic, i.e. <c>Rx × Ry × Rz</c> in <c>System.Numerics</c> row-vector form). This is the order
/// <c>Node3D.RotationDegrees</c> has always used, so existing scenes keep their look.
/// </summary>
public static class EulerAngles
{
    public static Quaternion ToQuaternion(Vector3 degrees)
    {
        var qx = Quaternion.CreateFromAxisAngle(Vector3.UnitX, float.DegreesToRadians(degrees.X));
        var qy = Quaternion.CreateFromAxisAngle(Vector3.UnitY, float.DegreesToRadians(degrees.Y));
        var qz = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, float.DegreesToRadians(degrees.Z));
        // Concatenate(a, b) = "a then b".
        return Quaternion.Concatenate(Quaternion.Concatenate(qx, qy), qz);
    }

    /// <summary>One Euler triple (degrees, X in [-180, 180], Y in [-90, 90]) for <paramref name="rotation"/>.</summary>
    public static Vector3 FromQuaternion(Quaternion rotation)
    {
        var m = Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(rotation));
        // m = Rx·Ry·Rz (row-vector). Its column-vector form R = mᵀ = Rz·Ry·Rx, so R[2][0] = -sin(y), etc.
        var sinY = Math.Clamp(-m.M13, -1f, 1f);
        float x, y, z;
        if (MathF.Abs(sinY) < 0.99999f)
        {
            y = MathF.Asin(sinY);
            x = MathF.Atan2(m.M23, m.M33);
            z = MathF.Atan2(m.M12, m.M11);
        }
        else
        {
            // Gimbal lock: Y is ±90°, fold Z into X.
            y = sinY > 0 ? MathF.PI / 2 : -MathF.PI / 2;
            x = MathF.Atan2(-m.M32, m.M22);
            z = 0;
        }

        return new Vector3(Tidy(float.RadiansToDegrees(x)), Tidy(float.RadiansToDegrees(y)), Tidy(float.RadiansToDegrees(z)));
    }

    // Decomposition noise (2.4E-06, -179.99998, -0) would end up in scene files: snap values within
    // 0.0005° of a whole degree (an invisible change) and drop negative zero.
    private static float Tidy(float degrees)
    {
        var whole = MathF.Round(degrees);
        return (MathF.Abs(degrees - whole) < 5e-4f ? whole : degrees) + 0f;
    }
}

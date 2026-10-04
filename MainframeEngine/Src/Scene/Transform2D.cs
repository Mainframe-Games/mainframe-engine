using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// An affine 2D transform: X and Y axis columns plus an origin, as in Godot. <see cref="ToMatrix3x2"/> gives the
/// <c>System.Numerics</c> row-vector form; <see cref="ToMatrix4x4"/> the same in the z = 0 plane.
/// </summary>
public struct Transform2D : IEquatable<Transform2D>
{
    public Vector2 X;
    public Vector2 Y;
    public Vector2 Origin;

    public Transform2D(Vector2 x, Vector2 y, Vector2 origin)
    {
        X = x;
        Y = y;
        Origin = origin;
    }

    public static Transform2D Identity => new(Vector2.UnitX, Vector2.UnitY, Vector2.Zero);

    /// <summary>Scale → rotation (radians, counter-clockwise) → translation.</summary>
    public static Transform2D FromTrs(Vector2 translation, float rotation, Vector2 scale)
    {
        var (sin, cos) = MathF.SinCos(rotation);
        return new Transform2D(new Vector2(cos, sin) * scale.X, new Vector2(-sin, cos) * scale.Y, translation);
    }

    public readonly Vector2 TransformPoint(Vector2 point) => X * point.X + Y * point.Y + Origin;

    public readonly Vector2 TransformDirection(Vector2 direction) => X * direction.X + Y * direction.Y;

    public static Transform2D operator *(Transform2D parent, Transform2D child) =>
        new(parent.TransformDirection(child.X), parent.TransformDirection(child.Y), parent.TransformPoint(child.Origin));

    public static Transform2D Multiply(Transform2D parent, Transform2D child) => parent * child;

    public readonly float Determinant() => X.X * Y.Y - X.Y * Y.X;

    public readonly Transform2D AffineInverse()
    {
        var det = Determinant();
        if (MathF.Abs(det) < 1e-12f)
            throw new InvalidOperationException("Transform2D is singular and cannot be inverted.");
        var ix = new Vector2(Y.Y, -X.Y) / det;
        var iy = new Vector2(-Y.X, X.X) / det;
        var inv = new Transform2D(ix, iy, Vector2.Zero);
        inv.Origin = -inv.TransformDirection(Origin);
        return inv;
    }

    public readonly float Rotation => MathF.Atan2(X.Y, X.X);

    public readonly Vector2 Scale => new(X.Length(), MathF.Sign(Determinant()) * Y.Length());

    public readonly Matrix3x2 ToMatrix3x2() => new(X.X, X.Y, Y.X, Y.Y, Origin.X, Origin.Y);

    public readonly Matrix4x4 ToMatrix4x4() => new(
        X.X, X.Y, 0, 0,
        Y.X, Y.Y, 0, 0,
        0, 0, 1, 0,
        Origin.X, Origin.Y, 0, 1);

    public readonly bool Equals(Transform2D other) => X == other.X && Y == other.Y && Origin == other.Origin;

    public override readonly bool Equals(object? obj) => obj is Transform2D other && Equals(other);

    public override readonly int GetHashCode() => HashCode.Combine(X, Y, Origin);

    public static bool operator ==(Transform2D left, Transform2D right) => left.Equals(right);

    public static bool operator !=(Transform2D left, Transform2D right) => !left.Equals(right);

    public override readonly string ToString() => $"[X: {X}, Y: {Y}, Origin: {Origin}]";
}

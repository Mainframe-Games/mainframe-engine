// Godot Engine 4.7.2's Vector2 API (modules/mono/glue/GodotSharp/GodotSharp/Core/Vector2.cs, MIT: Copyright (c)
// 2014-present Godot Engine contributors, (c) 2007-2014 Juan Linietsky, Ariel Manzur) as C# 14 extension members on
// System.Numerics.Vector2, the engine's one 2D vector type. Same formulas, same float order of operations; Normalized
// returns zero for a zero vector (System.Numerics' Normalize returns NaN).
using System.Numerics;

namespace MainframeEngine;

/// <summary>The engine's 2D vector helpers: Godot's <c>Vector2</c> methods and constants on <see cref="Vector2"/>.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1708", Justification = "CA1708 misreads the two C# 14 extension blocks (static and instance) as members differing only by case.")]
public static class Vector2Extensions
{
    /// <summary>The axis indices <see cref="MaxAxisIndex"/> and <see cref="MinAxisIndex"/> return.</summary>
    public enum Axis
    {
        X = 0,
        Y,
    }

    extension(Vector2)
    {
        /// <summary>Up in 2D (Y points down): (0, -1).</summary>
        public static Vector2 Up => new(0, -1);

        /// <summary>(0, 1).</summary>
        public static Vector2 Down => new(0, 1);

        /// <summary>(1, 0).</summary>
        public static Vector2 Right => new(1, 0);

        /// <summary>(-1, 0).</summary>
        public static Vector2 Left => new(-1, 0);

        /// <summary>Both components <see cref="Mathf.Inf"/>.</summary>
        public static Vector2 Inf => new(Mathf.Inf, Mathf.Inf);

        /// <summary>A unit vector at <paramref name="angle"/> radians from +X: <c>(cos, sin)</c>.</summary>
        public static Vector2 FromAngle(float angle)
        {
            (var sin, var cos) = Mathf.SinCos(angle);
            return new Vector2(cos, sin);
        }
    }

    extension(Vector2 v)
    {
        /// <summary>Each component's absolute value.</summary>
        public Vector2 Abs() => new(Mathf.Abs(v.X), Mathf.Abs(v.Y));

        /// <summary>The angle from +X in radians: <c>Atan2(Y, X)</c>.</summary>
        public float Angle() => Mathf.Atan2(v.Y, v.X);

        /// <summary>The signed angle to <paramref name="to"/>, in radians.</summary>
        public float AngleTo(Vector2 to) => Mathf.Atan2(v.Cross(to), v.Dot(to));

        /// <summary>The angle of the line from this point to <paramref name="to"/>, in radians.</summary>
        public float AngleToPoint(Vector2 to) => Mathf.Atan2(to.Y - v.Y, to.X - v.X);

        /// <summary>X / Y.</summary>
        public float Aspect() => v.X / v.Y;

        /// <summary>The vector bounced off a plane with the (normalized) <paramref name="normal"/>.</summary>
        public Vector2 Bounce(Vector2 normal) => -v.Reflect(normal);

        /// <summary>Each component rounded up.</summary>
        public Vector2 Ceil() => new(Mathf.Ceil(v.X), Mathf.Ceil(v.Y));

        /// <summary>Each component clamped between the components of <paramref name="min"/> and <paramref name="max"/>.</summary>
        public Vector2 Clamp(Vector2 min, Vector2 max) => new(Mathf.Clamp(v.X, min.X, max.X), Mathf.Clamp(v.Y, min.Y, max.Y));

        /// <summary>Each component clamped between <paramref name="min"/> and <paramref name="max"/>.</summary>
        public Vector2 Clamp(float min, float max) => new(Mathf.Clamp(v.X, min, max), Mathf.Clamp(v.Y, min, max));

        /// <summary>The 2D cross product (the Z of the 3D one).</summary>
        public float Cross(Vector2 with) => (v.X * with.Y) - (v.Y * with.X);

        /// <summary>Cubic interpolation between this vector and <paramref name="b"/>.</summary>
        public Vector2 CubicInterpolate(Vector2 b, Vector2 preA, Vector2 postB, float weight) => new(
            Mathf.CubicInterpolate(v.X, b.X, preA.X, postB.X, weight),
            Mathf.CubicInterpolate(v.Y, b.Y, preA.Y, postB.Y, weight));

        /// <summary>Cubic interpolation by time values.</summary>
        public Vector2 CubicInterpolateInTime(Vector2 b, Vector2 preA, Vector2 postB, float weight, float t, float preAT, float postBT) => new(
            Mathf.CubicInterpolateInTime(v.X, b.X, preA.X, postB.X, weight, t, preAT, postBT),
            Mathf.CubicInterpolateInTime(v.Y, b.Y, preA.Y, postB.Y, weight, t, preAT, postBT));

        /// <summary>The point at <paramref name="t"/> on the Bézier curve from this vector to <paramref name="end"/>.</summary>
        public Vector2 BezierInterpolate(Vector2 control1, Vector2 control2, Vector2 end, float t) => new(
            Mathf.BezierInterpolate(v.X, control1.X, control2.X, end.X, t),
            Mathf.BezierInterpolate(v.Y, control1.Y, control2.Y, end.Y, t));

        /// <summary>The derivative at <paramref name="t"/> on that Bézier curve.</summary>
        public Vector2 BezierDerivative(Vector2 control1, Vector2 control2, Vector2 end, float t) => new(
            Mathf.BezierDerivative(v.X, control1.X, control2.X, end.X, t),
            Mathf.BezierDerivative(v.Y, control1.Y, control2.Y, end.Y, t));

        /// <summary>The normalized direction from this point to <paramref name="to"/>.</summary>
        public Vector2 DirectionTo(Vector2 to) => new Vector2(to.X - v.X, to.Y - v.Y).Normalized();

        /// <summary>The squared distance to <paramref name="to"/>.</summary>
        public float DistanceSquaredTo(Vector2 to) => (v.X - to.X) * (v.X - to.X) + (v.Y - to.Y) * (v.Y - to.Y);

        /// <summary>The distance to <paramref name="to"/>.</summary>
        public float DistanceTo(Vector2 to) => Mathf.Sqrt((v.X - to.X) * (v.X - to.X) + (v.Y - to.Y) * (v.Y - to.Y));

        /// <summary>The dot product.</summary>
        public float Dot(Vector2 with) => (v.X * with.X) + (v.Y * with.Y);

        /// <summary>Each component rounded down.</summary>
        public Vector2 Floor() => new(Mathf.Floor(v.X), Mathf.Floor(v.Y));

        /// <summary>(1 / X, 1 / Y).</summary>
        public Vector2 Inverse() => new(1 / v.X, 1 / v.Y);

        /// <summary>Both components finite.</summary>
        public bool IsFinite() => Mathf.IsFinite(v.X) && Mathf.IsFinite(v.Y);

        /// <summary>Its length is 1 (within <see cref="Mathf.Epsilon"/>).</summary>
        public bool IsNormalized() => Mathf.IsEqualApprox(v.LengthSquared(), 1, Mathf.Epsilon);

        /// <summary>Linear interpolation to <paramref name="to"/> by <paramref name="weight"/>.</summary>
        public Vector2 Lerp(Vector2 to, float weight) => new(Mathf.Lerp(v.X, to.X, weight), Mathf.Lerp(v.Y, to.Y, weight));

        /// <summary>The vector with its length limited to <paramref name="length"/>.</summary>
        public Vector2 LimitLength(float length = 1.0f)
        {
            var r = v;
            var l = v.Length();
            if (l > 0 && length < l)
            {
                r /= l;
                r *= length;
            }

            return r;
        }

        /// <summary>The component-wise maximum.</summary>
        public Vector2 Max(Vector2 with) => new(Mathf.Max(v.X, with.X), Mathf.Max(v.Y, with.Y));

        /// <summary>The component-wise maximum with <paramref name="with"/>.</summary>
        public Vector2 Max(float with) => new(Mathf.Max(v.X, with), Mathf.Max(v.Y, with));

        /// <summary>The component-wise minimum.</summary>
        public Vector2 Min(Vector2 with) => new(Mathf.Min(v.X, with.X), Mathf.Min(v.Y, with.Y));

        /// <summary>The component-wise minimum with <paramref name="with"/>.</summary>
        public Vector2 Min(float with) => new(Mathf.Min(v.X, with), Mathf.Min(v.Y, with));

        /// <summary>The axis of the larger component (X when equal).</summary>
        public Axis MaxAxisIndex() => v.X < v.Y ? Axis.Y : Axis.X;

        /// <summary>The axis of the smaller component (Y when equal).</summary>
        public Axis MinAxisIndex() => v.X < v.Y ? Axis.X : Axis.Y;

        /// <summary>Moved toward <paramref name="to"/> by at most <paramref name="delta"/>, never past it.</summary>
        public Vector2 MoveToward(Vector2 to, float delta)
        {
            var vd = to - v;
            var len = vd.Length();
            if (len <= delta || len < Mathf.Epsilon)
                return to;
            return v + (vd / len * delta);
        }

        /// <summary>The vector scaled to length 1; a zero vector stays zero.</summary>
        public Vector2 Normalized()
        {
            var lengthsq = v.LengthSquared();
            if (lengthsq == 0)
                return Vector2.Zero;
            var length = Mathf.Sqrt(lengthsq);
            return new Vector2(v.X / length, v.Y / length);
        }

        /// <summary>Each component <see cref="Mathf.PosMod(float, float)"/> <paramref name="mod"/>.</summary>
        public Vector2 PosMod(float mod) => new(Mathf.PosMod(v.X, mod), Mathf.PosMod(v.Y, mod));

        /// <summary>Each component <see cref="Mathf.PosMod(float, float)"/> the matching one of <paramref name="modv"/>.</summary>
        public Vector2 PosMod(Vector2 modv) => new(Mathf.PosMod(v.X, modv.X), Mathf.PosMod(v.Y, modv.Y));

        /// <summary>The projection onto <paramref name="onNormal"/>.</summary>
        public Vector2 Project(Vector2 onNormal) => onNormal * (v.Dot(onNormal) / onNormal.LengthSquared());

        /// <summary>Reflected across the line with the (normalized) <paramref name="normal"/>.</summary>
        public Vector2 Reflect(Vector2 normal) => (2 * v.Dot(normal) * normal) - v;

        /// <summary>Rotated by <paramref name="angle"/> radians.</summary>
        public Vector2 Rotated(float angle)
        {
            (var sin, var cos) = Mathf.SinCos(angle);
            return new Vector2(v.X * cos - v.Y * sin, v.X * sin + v.Y * cos);
        }

        /// <summary>Each component rounded to the nearest integer (halves to even).</summary>
        public Vector2 Round() => new(Mathf.Round(v.X), Mathf.Round(v.Y));

        /// <summary>Each component's sign: 1, -1 or 0.</summary>
        public Vector2 Sign() => new(Mathf.Sign(v.X), Mathf.Sign(v.Y));

        /// <summary>Spherical interpolation to <paramref name="to"/> (lengths interpolated too).</summary>
        public Vector2 Slerp(Vector2 to, float weight)
        {
            var startLengthSquared = v.LengthSquared();
            var endLengthSquared = to.LengthSquared();
            if (startLengthSquared == 0.0 || endLengthSquared == 0.0)
                return v.Lerp(to, weight);
            var startLength = Mathf.Sqrt(startLengthSquared);
            var resultLength = Mathf.Lerp(startLength, Mathf.Sqrt(endLengthSquared), weight);
            var angle = v.AngleTo(to);
            return v.Rotated(angle * weight) * (resultLength / startLength);
        }

        /// <summary>Slid along the line with the (normalized) <paramref name="normal"/>.</summary>
        public Vector2 Slide(Vector2 normal) => v - (normal * v.Dot(normal));

        /// <summary>Each component snapped to the nearest multiple of the matching one of <paramref name="step"/>.</summary>
        public Vector2 Snapped(Vector2 step) => new(Mathf.Snapped(v.X, step.X), Mathf.Snapped(v.Y, step.Y));

        /// <summary>Each component snapped to the nearest multiple of <paramref name="step"/>.</summary>
        public Vector2 Snapped(float step) => new(Mathf.Snapped(v.X, step), Mathf.Snapped(v.Y, step));

        /// <summary>Rotated 90° counter-clockwise, same length: (Y, -X).</summary>
        public Vector2 Orthogonal() => new(v.Y, -v.X);

        /// <summary>Both components approximately equal (<see cref="Mathf.IsEqualApprox(float, float)"/>).</summary>
        public bool IsEqualApprox(Vector2 other) => Mathf.IsEqualApprox(v.X, other.X) && Mathf.IsEqualApprox(v.Y, other.Y);

        /// <summary>Both components approximately zero.</summary>
        public bool IsZeroApprox() => Mathf.IsZeroApprox(v.X) && Mathf.IsZeroApprox(v.Y);
    }
}

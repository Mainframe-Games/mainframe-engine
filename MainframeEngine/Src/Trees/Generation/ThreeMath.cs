// The three.js math that Ez Tree's generator calls (Vector3, Quaternion, Euler in XYZ order), ported in doubles so
// the C# port of Ez Tree (src/lib/tree.js; https://github.com/dgreenheck/ez-tree at commit
// dcf309bd86bd521083d9c70f01f2de45fdc7c457) computes what three.js 0.167.1 computes, operation for operation.
//
// The MIT License
//
// Copyright © 2010-2024 three.js authors
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
//
// Ez Tree is MIT licensed, Copyright (c) 2024 Daniel Greenheck (full text in THIRD_PARTY_NOTICES.md and EzRng.cs).

namespace MainframeEngine.Trees;

// Only what the generator calls, with three.js's exact operation order (floating-point addition is not associative,
// so "the same formula" written differently would drift in the last bits). System.Numerics is float-only and composes
// quaternions in another order, so it is not used here.

/// <summary>three.js <c>Vector3</c> in doubles (immutable; methods return the result).</summary>
internal readonly record struct Vec3d(double X, double Y, double Z)
{
    public static Vec3d Zero => default;

    public static Vec3d UnitY => new(0, 1, 0);

    public double Length() => Math.Sqrt(X * X + Y * Y + Z * Z);

    public double Dot(Vec3d v) => X * v.X + Y * v.Y + Z * v.Z;

    public Vec3d Add(Vec3d v) => new(X + v.X, Y + v.Y, Z + v.Z);

    public Vec3d Sub(Vec3d v) => new(X - v.X, Y - v.Y, Z - v.Z);

    public Vec3d MultiplyScalar(double s) => new(X * s, Y * s, Z * s);

    /// <summary><c>divideScalar</c> is <c>multiplyScalar(1 / s)</c> in three.js.</summary>
    public Vec3d DivideScalar(double s) => MultiplyScalar(1 / s);

    /// <summary><c>normalize()</c>: <c>divideScalar(length() || 1)</c> (a zero or NaN length divides by 1).</summary>
    public Vec3d Normalize()
    {
        var length = Length();
        return DivideScalar(length == 0 || double.IsNaN(length) ? 1 : length);
    }

    /// <summary><c>crossVectors(a, b)</c>.</summary>
    public static Vec3d Cross(Vec3d a, Vec3d b) => new(
        a.Y * b.Z - a.Z * b.Y,
        a.Z * b.X - a.X * b.Z,
        a.X * b.Y - a.Y * b.X);

    /// <summary><c>lerpVectors(v1, v2, alpha)</c>.</summary>
    public static Vec3d Lerp(Vec3d v1, Vec3d v2, double alpha) => new(
        v1.X + (v2.X - v1.X) * alpha,
        v1.Y + (v2.Y - v1.Y) * alpha,
        v1.Z + (v2.Z - v1.Z) * alpha);

    public double DistanceTo(Vec3d v)
    {
        var dx = X - v.X;
        var dy = Y - v.Y;
        var dz = Z - v.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary><c>applyQuaternion(q)</c> (the r150+ form: <c>t = 2 cross(q.xyz, v); v + w t + cross(q.xyz, t)</c>).</summary>
    public Vec3d ApplyQuaternion(Quatd q)
    {
        double vx = X, vy = Y, vz = Z;
        double qx = q.X, qy = q.Y, qz = q.Z, qw = q.W;

        var tx = 2 * (qy * vz - qz * vy);
        var ty = 2 * (qz * vx - qx * vz);
        var tz = 2 * (qx * vy - qy * vx);

        return new Vec3d(
            vx + qw * tx + qy * tz - qz * ty,
            vy + qw * ty + qz * tx - qx * tz,
            vz + qw * tz + qx * ty - qy * tx);
    }

    /// <summary><c>applyEuler(e)</c>: <c>applyQuaternion(quaternion.setFromEuler(e))</c>.</summary>
    public Vec3d ApplyEuler(EulerXyz e) => ApplyQuaternion(Quatd.FromEuler(e));

    public System.Numerics.Vector3 ToFloat() => new((float)X, (float)Y, (float)Z);
}

/// <summary>three.js <c>Euler</c> with the default <c>'XYZ'</c> order (the only order Ez Tree uses).</summary>
internal readonly record struct EulerXyz(double X, double Y, double Z)
{
    /// <summary>
    /// <c>Euler.setFromQuaternion(q)</c>: through <c>Matrix4.makeRotationFromQuaternion</c> (<c>compose</c> with unit
    /// scale) and <c>setFromRotationMatrix</c> for XYZ, including its <c>0.9999999</c> gimbal threshold.
    /// </summary>
    public static EulerXyz FromQuaternion(Quatd q)
    {
        double x = q.X, y = q.Y, z = q.Z, w = q.W;
        double x2 = x + x, y2 = y + y, z2 = z + z;
        double xx = x * x2, xy = x * y2, xz = x * z2;
        double yy = y * y2, yz = y * z2, zz = z * z2;
        double wx = w * x2, wy = w * y2, wz = w * z2;

        // compose(_zero, q, _one): the scale factors are exactly 1, so multiplying by them changes nothing.
        var m11 = 1 - (yy + zz);
        var m12 = xy - wz;
        var m13 = xz + wy;
        var m22 = 1 - (xx + zz);
        var m23 = yz - wx;
        var m32 = yz + wx;
        var m33 = 1 - (xx + yy);

        var ey = Math.Asin(ThreeMath.Clamp(m13, -1, 1));
        double ex, ez;
        if (Math.Abs(m13) < 0.9999999)
        {
            ex = Math.Atan2(-m23, m33);
            ez = Math.Atan2(-m12, m11);
        }
        else
        {
            ex = Math.Atan2(m32, m22);
            ez = 0;
        }

        return new EulerXyz(ex, ey, ez);
    }
}

/// <summary>three.js <c>Quaternion</c> in doubles (immutable; methods return the result).</summary>
internal readonly record struct Quatd(double X, double Y, double Z, double W)
{
    public static Quatd Identity => new(0, 0, 0, 1);

    /// <summary><c>setFromEuler(e)</c> for the XYZ order.</summary>
    public static Quatd FromEuler(EulerXyz e)
    {
        var c1 = Math.Cos(e.X / 2);
        var c2 = Math.Cos(e.Y / 2);
        var c3 = Math.Cos(e.Z / 2);

        var s1 = Math.Sin(e.X / 2);
        var s2 = Math.Sin(e.Y / 2);
        var s3 = Math.Sin(e.Z / 2);

        return new Quatd(
            s1 * c2 * c3 + c1 * s2 * s3,
            c1 * s2 * c3 - s1 * c2 * s3,
            c1 * c2 * s3 + s1 * s2 * c3,
            c1 * c2 * c3 - s1 * s2 * s3);
    }

    /// <summary><c>setFromAxisAngle(axis, angle)</c> (the axis is assumed normalized).</summary>
    public static Quatd FromAxisAngle(Vec3d axis, double angle)
    {
        var halfAngle = angle / 2;
        var s = Math.Sin(halfAngle);
        return new Quatd(axis.X * s, axis.Y * s, axis.Z * s, Math.Cos(halfAngle));
    }

    /// <summary><c>setFromUnitVectors(vFrom, vTo)</c> (both assumed normalized).</summary>
    public static Quatd FromUnitVectors(Vec3d vFrom, Vec3d vTo)
    {
        var r = vFrom.Dot(vTo) + 1;
        Quatd q;
        if (r < ThreeMath.NumberEpsilon)
        {
            // vFrom and vTo point in opposite directions
            r = 0;
            q = Math.Abs(vFrom.X) > Math.Abs(vFrom.Z)
                ? new Quatd(-vFrom.Y, vFrom.X, 0, r)
                : new Quatd(0, -vFrom.Z, vFrom.Y, r);
        }
        else
        {
            q = new Quatd(
                vFrom.Y * vTo.Z - vFrom.Z * vTo.Y,
                vFrom.Z * vTo.X - vFrom.X * vTo.Z,
                vFrom.X * vTo.Y - vFrom.Y * vTo.X,
                r);
        }

        return q.Normalize();
    }

    public double Length() => Math.Sqrt(X * X + Y * Y + Z * Z + W * W);

    public double Dot(Quatd q) => X * q.X + Y * q.Y + Z * q.Z + W * q.W;

    /// <summary><c>normalize()</c>: identity for a zero length, otherwise a multiply by <c>1 / length</c>.</summary>
    public Quatd Normalize()
    {
        var l = Length();
        if (l == 0)
            return Identity;
        l = 1 / l;
        return new Quatd(X * l, Y * l, Z * l, W * l);
    }

    /// <summary><c>multiplyQuaternions(a, b)</c>.</summary>
    public static Quatd Multiply(Quatd a, Quatd b)
    {
        double qax = a.X, qay = a.Y, qaz = a.Z, qaw = a.W;
        double qbx = b.X, qby = b.Y, qbz = b.Z, qbw = b.W;

        return new Quatd(
            qax * qbw + qaw * qbx + qay * qbz - qaz * qby,
            qay * qbw + qaw * qby + qaz * qbx - qax * qbz,
            qaz * qbw + qaw * qbz + qax * qby - qay * qbx,
            qaw * qbw - qax * qbx - qay * qby - qaz * qbz);
    }

    /// <summary><c>this.multiply(q)</c>: this × q.</summary>
    public Quatd Multiply(Quatd q) => Multiply(this, q);

    /// <summary><c>this.premultiply(q)</c>: q × this.</summary>
    public Quatd Premultiply(Quatd q) => Multiply(q, this);

    /// <summary><c>this.slerp(qb, t)</c>, edge cases included (t 0 and 1, the shorter arc, nearly equal quaternions).</summary>
    public Quatd Slerp(Quatd qb, double t)
    {
        if (t == 0)
            return this;
        if (t == 1)
            return qb;

        double x = X, y = Y, z = Z, w = W;

        var cosHalfTheta = w * qb.W + x * qb.X + y * qb.Y + z * qb.Z;

        double rx, ry, rz, rw;
        if (cosHalfTheta < 0)
        {
            rw = -qb.W;
            rx = -qb.X;
            ry = -qb.Y;
            rz = -qb.Z;
            cosHalfTheta = -cosHalfTheta;
        }
        else
        {
            (rx, ry, rz, rw) = (qb.X, qb.Y, qb.Z, qb.W);
        }

        if (cosHalfTheta >= 1.0)
            return this;

        var sqrSinHalfTheta = 1.0 - cosHalfTheta * cosHalfTheta;

        if (sqrSinHalfTheta <= ThreeMath.NumberEpsilon)
        {
            var s = 1 - t;
            return new Quatd(s * x + t * rx, s * y + t * ry, s * z + t * rz, s * w + t * rw).Normalize();
        }

        var sinHalfTheta = Math.Sqrt(sqrSinHalfTheta);
        var halfTheta = Math.Atan2(sinHalfTheta, cosHalfTheta);
        var ratioA = Math.Sin((1 - t) * halfTheta) / sinHalfTheta;
        var ratioB = Math.Sin(t * halfTheta) / sinHalfTheta;

        return new Quatd(
            x * ratioA + rx * ratioB,
            y * ratioA + ry * ratioB,
            z * ratioA + rz * ratioB,
            w * ratioA + rw * ratioB);
    }

    /// <summary><c>angleTo(q)</c>.</summary>
    public double AngleTo(Quatd q) => 2 * Math.Acos(Math.Abs(ThreeMath.Clamp(Dot(q), -1, 1)));

    /// <summary><c>rotateTowards(q, step)</c>.</summary>
    public Quatd RotateTowards(Quatd q, double step)
    {
        var angle = AngleTo(q);
        if (angle == 0)
            return this;
        var t = Math.Min(1, step / angle);
        return Slerp(q, t);
    }
}

/// <summary>JavaScript and three.js helpers whose .NET counterparts behave differently.</summary>
internal static class ThreeMath
{
    /// <summary>JavaScript's <c>Number.EPSILON</c> (2^-52); .NET's <see cref="double.Epsilon"/> is the smallest subnormal.</summary>
    public const double NumberEpsilon = 2.220446049250313e-16;

    /// <summary><c>MathUtils.clamp</c>: <c>Math.max(min, Math.min(max, value))</c> (NaN propagates, as in JavaScript).</summary>
    public static double Clamp(double value, double min, double max) => Math.Max(min, Math.Min(max, value));

    /// <summary>
    /// JavaScript's <c>Math.round</c>: halves round up (towards +∞). .NET's <see cref="Math.Round(double)"/> rounds them to
    /// even, so <c>round(6 × 0.75)</c> would be 4 instead of 5.
    /// </summary>
    public static double JsRound(double x) => Math.Floor(x + 0.5);
}

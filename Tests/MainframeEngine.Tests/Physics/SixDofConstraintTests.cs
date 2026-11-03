using System.Numerics;
using System.Runtime.CompilerServices;
using Jitter2;

namespace MainframeEngine.Tests.Physics;

/// <summary>The engine's own Jitter2 constraints (ADR 0234): their data sizes, the 6-DOF angle math and soft rows.</summary>
public sealed class SixDofConstraintTests
{
    [Fact]
    public void EveryCustomConstraintsDataFitsJitter2sConstraintData()
    {
        Assert.Equal(32, Unsafe.SizeOf<JitterConstraintHeader>());
        Assert.InRange(Unsafe.SizeOf<SixDofLinear.Values>(), 1, Precision.ConstraintSizeFull);
        Assert.InRange(Unsafe.SizeOf<SixDofAngular.Values>(), 1, Precision.ConstraintSizeFull);
        Assert.InRange(Unsafe.SizeOf<SixDofLinearDrive.Values>(), 1, Precision.ConstraintSizeFull);
        Assert.InRange(Unsafe.SizeOf<SixDofAngularDrive.Values>(), 1, Precision.ConstraintSizeFull);
    }

    [Theory]
    [InlineData(0.3f, -0.6f, 1.2f)]
    [InlineData(-2.5f, 1.2f, -0.1f)]
    [InlineData(0f, 0f, 3f)]
    [InlineData(1f, -1.5f, 0f)]
    public void TheEulerAnglesRecoverTheRotationTheyCameFrom(float x, float y, float z)
    {
        // Rx·Ry·Rz acting on column vectors: z applied first, then y, then x.
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, x) * Quaternion.CreateFromAxisAngle(Vector3.UnitY, y) *
                       Quaternion.CreateFromAxisAngle(Vector3.UnitZ, z);
        var angles = SixDofMath.EulerXYZ(rotation);
        Assert.Equal(x, angles.X, 3);
        Assert.Equal(y, angles.Y, 3);
        Assert.Equal(z, angles.Z, 3);
    }

    [Theory]
    [InlineData(0.3f, -0.6f, 1.2f)]
    [InlineData(-1f, 0.4f, -2f)]
    [InlineData(0f, 0f, 0f)]
    public void EachAnglesRateIsItsAxisDottedWithTheRelativeAngularVelocity(float x, float y, float z)
    {
        var frameA = Quaternion.Normalize(new Quaternion(0.1f, 0.2f, -0.3f, 0.9f));
        var relative = Quaternion.CreateFromAxisAngle(Vector3.UnitX, x) * Quaternion.CreateFromAxisAngle(Vector3.UnitY, y) *
                       Quaternion.CreateFromAxisAngle(Vector3.UnitZ, z);
        var frameB = frameA * relative;
        SixDofMath.AngularAxes(frameA, frameB, out var axis0, out var axis1, out var axis2);
        var before = SixDofMath.EulerXYZ(SixDofMath.Relative(frameA, frameB));

        // Turn B by a small world angular velocity ω for dt: each angle changes by axis · ω · dt.
        var omega = new Vector3(0.3f, -0.2f, 0.5f);
        const float dt = 1e-3f;
        var turned = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.Normalize(omega), omega.Length() * dt) * frameB);
        var after = SixDofMath.EulerXYZ(SixDofMath.Relative(frameA, turned));
        var rates = (after - before) / dt;
        Assert.Equal(Vector3.Dot(axis0, omega), rates.X, 2);
        Assert.Equal(Vector3.Dot(axis1, omega), rates.Y, 2);
        Assert.Equal(Vector3.Dot(axis2, omega), rates.Z, 2);
    }

    [Fact]
    public void ASoftRowsCoefficientsAreBox2DsAndAZeroFrequencyIsRigid()
    {
        SoftRow.Coefficients(0f, 1f, 1f / 240, out var biasRate, out var massScale);
        Assert.Equal((0f, 1f), (biasRate, massScale));

        // ω = 2π·2, ζ = 0.5, h = 1/240: a1 = 2ζ + hω, a2 = hω·a1, biasRate = ω/a1, massScale = a2/(1 + a2).
        const float h = 1f / 240;
        var omega = 2f * MathF.PI * 2f;
        var a1 = 2f * 0.5f + h * omega;
        var a2 = h * omega * a1;
        SoftRow.Coefficients(2f, 0.5f, h, out biasRate, out massScale);
        Assert.Equal(omega / a1, biasRate, 4);
        Assert.Equal(a2 / (1f + a2), massScale, 5);
        Assert.InRange(massScale, 0f, 1f);
    }
}

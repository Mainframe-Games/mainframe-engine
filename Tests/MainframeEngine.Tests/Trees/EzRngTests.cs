using MainframeEngine.Trees;

namespace MainframeEngine.Tests.Trees;

/// <summary><see cref="EzRng"/> is bit-exact with Ez Tree's <c>rng.js</c>, and <see cref="ThreeMath"/> keeps JavaScript's rules.</summary>
public sealed class EzRngTests
{
    [Theory]
    [InlineData(0, 0.732297654031, 0.058062777622, 0.821864804020)]
    [InlineData(35729, 0.925737335347, 0.001972383121, 0.291409873404)]
    public void FirstDrawsMatchTheProposalVectors(int seed, double a, double b, double c)
    {
        var rng = new EzRng(seed);
        Assert.Equal(a, rng.Next(), 1e-12);
        Assert.Equal(b, rng.Next(), 1e-12);
        Assert.Equal(c, rng.Next(), 1e-12);
    }

    [Fact]
    public void DrawsAreBitExactWithEzTree()
    {
        foreach (var entry in TreeFixture.Root.GetProperty("rng").EnumerateObject())
        {
            var rng = new EzRng(int.Parse(entry.Name, System.Globalization.CultureInfo.InvariantCulture));
            foreach (var expected in entry.Value.EnumerateArray())
                Assert.Equal(expected.GetDouble(), rng.Next()); // exact: the draws are integers / 2^32
        }
    }

    [Fact]
    public void OneMillionDrawsHashLikeEzTree()
    {
        var million = TreeFixture.Root.GetProperty("rngMillion");
        var rng = new EzRng(million.GetProperty("seed").GetInt32());
        var hash = TreeFixture.StartHash;
        for (var i = 0; i < 1_000_000; i++)
            hash = TreeFixture.Fnv(hash, (uint)Math.Round(rng.Next() * 4294967296.0));
        Assert.Equal(million.GetProperty("hash").GetUInt32(), hash);
    }

    [Fact]
    public void MaxAndMinFollowEzTreesArgumentOrder()
    {
        var a = new EzRng(7);
        var b = new EzRng(7);
        var unit = a.Next();
        Assert.Equal(unit * (0.5 - -0.5) + -0.5, b.Next(0.5, -0.5));
    }

    [Theory]
    [InlineData(4.5, 5)]   // Math.Round(4.5) is 4 in .NET: round(6 × 0.75) must be 5, as in JavaScript
    [InlineData(2.5, 3)]
    [InlineData(-2.5, -2)]
    [InlineData(1.4999, 1)]
    public void JsRoundRoundsHalvesUp(double value, double expected) => Assert.Equal(expected, ThreeMath.JsRound(value));

    [Theory]
    [InlineData(0.3, -0.2, 0.9)]
    [InlineData(-1.1, 0.7, -2.4)]
    [InlineData(0.0, 1.2, 0.0)]
    public void EulerQuaternionRoundTrip(double x, double y, double z)
    {
        var euler = new EulerXyz(x, y, z);
        var back = EulerXyz.FromQuaternion(Quatd.FromEuler(euler));
        Assert.Equal(x, back.X, 1e-12);
        Assert.Equal(y, back.Y, 1e-12);
        Assert.Equal(z, back.Z, 1e-12);
    }

    [Fact]
    public void EulerAtTheGimbalThresholdFoldsZIntoX()
    {
        // |m13| ≥ 0.9999999: three.js sets z = 0 and puts the whole roll in x. The rotation is unchanged.
        var q = Quatd.FromEuler(new EulerXyz(0.4, Math.PI / 2, 0.3));
        var euler = EulerXyz.FromQuaternion(q);
        Assert.Equal(0, euler.Z);
        var v = new Vec3d(0.2, 0.5, -0.7);
        var expected = v.ApplyQuaternion(q);
        var got = v.ApplyEuler(euler);
        Assert.Equal(expected.X, got.X, 1e-6);
        Assert.Equal(expected.Y, got.Y, 1e-6);
        Assert.Equal(expected.Z, got.Z, 1e-6);
    }

    [Fact]
    public void SlerpEdgeCases()
    {
        var a = Quatd.FromAxisAngle(new Vec3d(0, 1, 0), 0.5);
        var b = Quatd.FromAxisAngle(new Vec3d(0, 1, 0), 1.5);

        Assert.Equal(a, a.Slerp(b, 0));
        Assert.Equal(b, a.Slerp(b, 1));
        var same = a.Slerp(a, 0.5); // cos(θ/2) ≈ 1: itself, or the normalized lerp of two equal quaternions
        Assert.Equal(a.Y, same.Y, 1e-15);
        Assert.Equal(a.W, same.W, 1e-15);

        var half = a.Slerp(b, 0.5);
        var expected = Quatd.FromAxisAngle(new Vec3d(0, 1, 0), 1.0);
        Assert.Equal(expected.Y, half.Y, 1e-12);
        Assert.Equal(expected.W, half.W, 1e-12);

        // The shorter arc: slerping towards -b is slerping towards b.
        var negated = new Quatd(-b.X, -b.Y, -b.Z, -b.W);
        var viaNegated = a.Slerp(negated, 0.5);
        Assert.Equal(half.Y, viaNegated.Y, 1e-12);
        Assert.Equal(half.W, viaNegated.W, 1e-12);
    }

    [Fact]
    public void NormalizeOfZeroDividesByOne()
    {
        Assert.Equal(Vec3d.Zero, Vec3d.Zero.Normalize());
        Assert.Equal(Quatd.Identity, new Quatd(0, 0, 0, 0).Normalize());
    }
}

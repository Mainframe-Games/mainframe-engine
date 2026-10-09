using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// Real spherical harmonics up to band 2 (Ramamoorthi and Hanrahan 2001) for the light probes (ADR 0170): projection of
/// a function sampled on the sphere, evaluation and the cosine-lobe convolution. The shaders' <c>include/probes.slang</c>
/// mirrors <see cref="L1Irradiance"/> and <see cref="L1Evaluate"/>.
/// </summary>
/// <remarks>
/// Band 1 is stored as one vector: the coefficients of <c>Y₁ · (x, y, z)</c>, not in the usual (y, z, x) order. An L2
/// set is <c>c0, c1 = (x, y, z), then xy, yz, 3z²−1, xz, x²−y²</c>. "Irradiance" here is the cosine-weighted integral
/// divided by π, which is what the engine calls an ambient colour: a uniform environment of radiance L gives L.
/// </remarks>
public static class SphericalHarmonics
{
    /// <summary>Y₀₀ = 1 / (2√π).</summary>
    public const float Y0 = 0.282094792f;

    /// <summary>Y₁ₘ = √3 / (2√π) · (x, y, z).</summary>
    public const float Y1 = 0.488602512f;

    /// <summary>Band 2 normalisation: xy, yz, xz terms (√15 / (2√π)).</summary>
    public const float Y2a = 1.092548431f;

    /// <summary>Band 2: the 3z² − 1 term (√5 / (4√π)).</summary>
    public const float Y2b = 0.315391565f;

    /// <summary>Band 2: the x² − y² term (√15 / (4√π)).</summary>
    public const float Y2c = 0.546274215f;

    /// <summary>Clamped-cosine convolution over π: band 0 → 1, band 1 → 2/3, band 2 → 1/4.</summary>
    public const float A0 = 1f, A1 = 2f / 3f, A2 = 0.25f;

    /// <summary>
    /// The cosine-convolved value over π at <paramref name="normal"/> of an L1 set (<paramref name="c0"/>, band 1
    /// <paramref name="c1"/>): for a visibility set the visible fraction of the hemisphere, for radiance the ambient
    /// colour. Not clamped.
    /// </summary>
    public static float L1Irradiance(float c0, Vector3 c1, Vector3 normal) =>
        A0 * Y0 * c0 + A1 * Y1 * Vector3.Dot(c1, normal);

    /// <summary>The L1 set's value in <paramref name="direction"/> (the reconstructed function).</summary>
    public static float L1Evaluate(float c0, Vector3 c1, Vector3 direction) => Y0 * c0 + Y1 * Vector3.Dot(c1, direction);

    /// <summary>The nine L2 basis values in <paramref name="d"/> (unit), in this class's order.</summary>
    public static void L2Basis(Vector3 d, Span<float> basis)
    {
        basis[0] = Y0;
        basis[1] = Y1 * d.X;
        basis[2] = Y1 * d.Y;
        basis[3] = Y1 * d.Z;
        basis[4] = Y2a * d.X * d.Y;
        basis[5] = Y2a * d.Y * d.Z;
        basis[6] = Y2b * (3f * d.Z * d.Z - 1f);
        basis[7] = Y2a * d.X * d.Z;
        basis[8] = Y2c * (d.X * d.X - d.Y * d.Y);
    }

    /// <summary>
    /// The <paramref name="index"/>-th of <paramref name="count"/> Fibonacci-sphere directions: near-uniform, so a sum
    /// over them × 4π / count integrates over the sphere.
    /// </summary>
    public static Vector3 FibonacciDirection(int index, int count)
    {
        const float golden = 2.39996323f; // π (3 − √5)
        var y = 1f - (index + 0.5f) * 2f / count;
        var r = MathF.Sqrt(MathF.Max(0f, 1f - y * y));
        var phi = index * golden;
        return new Vector3(MathF.Cos(phi) * r, y, MathF.Sin(phi) * r);
    }
}

/// <summary>An RGB function on the sphere projected onto L2 (nine coefficients per channel): the bake's sky.</summary>
public readonly struct ShL2Rgb
{
    private readonly Vector3[] _c;

    private ShL2Rgb(Vector3[] coefficients) => _c = coefficients;

    /// <summary>The nine RGB coefficients (a copy).</summary>
    public Vector3[] Coefficients => (Vector3[])(_c ?? new Vector3[9]).Clone();

    /// <summary>A uniform environment of <paramref name="radiance"/>.</summary>
    public static ShL2Rgb Uniform(Vector3 radiance)
    {
        var c = new Vector3[9];
        c[0] = radiance * (4f * MathF.PI * SphericalHarmonics.Y0);
        return new ShL2Rgb(c);
    }

    /// <summary>Projects <paramref name="radiance"/> sampled in <paramref name="samples"/> Fibonacci directions.</summary>
    public static ShL2Rgb Project(Func<Vector3, Vector3> radiance, int samples = 1024)
    {
        ArgumentNullException.ThrowIfNull(radiance);
        ArgumentOutOfRangeException.ThrowIfLessThan(samples, 16);
        var c = new Vector3[9];
        Span<float> basis = stackalloc float[9];
        for (var i = 0; i < samples; i++)
        {
            var d = SphericalHarmonics.FibonacciDirection(i, samples);
            var value = radiance(d);
            SphericalHarmonics.L2Basis(d, basis);
            for (var k = 0; k < 9; k++)
                c[k] += value * basis[k];
        }

        var weight = 4f * MathF.PI / samples;
        for (var k = 0; k < 9; k++)
            c[k] *= weight;
        return new ShL2Rgb(c);
    }

    /// <summary>The cosine-convolved value over π at <paramref name="normal"/>: the ambient colour a surface facing it sees.</summary>
    public Vector3 Irradiance(Vector3 normal)
    {
        if (_c is null)
            return Vector3.Zero;
        Span<float> basis = stackalloc float[9];
        SphericalHarmonics.L2Basis(normal, basis);
        var sum = _c[0] * (basis[0] * SphericalHarmonics.A0);
        for (var k = 1; k < 4; k++)
            sum += _c[k] * (basis[k] * SphericalHarmonics.A1);
        for (var k = 4; k < 9; k++)
            sum += _c[k] * (basis[k] * SphericalHarmonics.A2);
        return Vector3.Max(sum, Vector3.Zero);
    }

    /// <summary>The reconstructed radiance in <paramref name="direction"/>.</summary>
    public Vector3 Evaluate(Vector3 direction)
    {
        if (_c is null)
            return Vector3.Zero;
        Span<float> basis = stackalloc float[9];
        SphericalHarmonics.L2Basis(direction, basis);
        var sum = Vector3.Zero;
        for (var k = 0; k < 9; k++)
            sum += _c[k] * basis[k];
        return sum;
    }
}

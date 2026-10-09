using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// The split-sum environment BRDF of the PBR shading (ADR 0150, <c>include/environment.slang</c> <c>iblBrdf</c>): for a
/// view angle and a perceptual roughness, the scale <c>A</c> and bias <c>B</c> such that the GGX specular lobe (Smith
/// height-correlated visibility, Schlick Fresnel) integrated over a white environment reflects <c>F0·A + B</c>.
/// Computed on the CPU once per process (a few milliseconds) by GGX importance sampling, and uploaded as a
/// <see cref="Size"/>² <c>R16G16_SFLOAT</c> texture by <see cref="FrameContext"/>: unit-testable, and nothing for other
/// backends to port.
/// </summary>
/// <remarks>Texel (i, j) holds N·V = (i + 0.5) / Size and roughness = (j + 0.5) / Size, so linear filtering at uv = (N·V,
/// roughness) reads the table exactly at texel centres.</remarks>
internal static class BrdfLut
{
    /// <summary>Texels along each axis.</summary>
    public const int Size = 64;

    /// <summary>Importance samples per texel.</summary>
    public const int SampleCount = 256;

    private static readonly Lazy<Vector2[]> s_table = new(() => Compute(Size, SampleCount));

    /// <summary>The table, row-major by roughness: <c>[j * Size + i]</c> = (A, B) at N·V (i) and roughness (j).</summary>
    public static ReadOnlySpan<Vector2> Table => s_table.Value;

    /// <summary>The table as <c>R16G16_SFLOAT</c> texels, ready to upload.</summary>
    public static byte[] ToHalfPixels()
    {
        var table = Table;
        var bytes = new byte[table.Length * 4];
        for (var k = 0; k < table.Length; k++)
        {
            BitConverter.TryWriteBytes(bytes.AsSpan(k * 4), (Half)table[k].X);
            BitConverter.TryWriteBytes(bytes.AsSpan(k * 4 + 2), (Half)table[k].Y);
        }

        return bytes;
    }

    /// <summary>(A, B) at a texel-centre-exact position, bilinear between texel centres (as the GPU samples it).</summary>
    public static Vector2 Sample(float nDotV, float roughness)
    {
        var table = Table;
        float x = Math.Clamp(nDotV * Size - 0.5f, 0f, Size - 1f), y = Math.Clamp(roughness * Size - 0.5f, 0f, Size - 1f);
        int x0 = (int)x, y0 = (int)y, x1 = Math.Min(x0 + 1, Size - 1), y1 = Math.Min(y0 + 1, Size - 1);
        float fx = x - x0, fy = y - y0;
        var top = Vector2.Lerp(table[y0 * Size + x0], table[y0 * Size + x1], fx);
        var bottom = Vector2.Lerp(table[y1 * Size + x0], table[y1 * Size + x1], fx);
        return Vector2.Lerp(top, bottom, fy);
    }

    /// <summary>Computes a <paramref name="size"/>² table with <paramref name="samples"/> GGX samples per texel.</summary>
    public static Vector2[] Compute(int size, int samples)
    {
        var table = new Vector2[size * size];
        for (var j = 0; j < size; j++)
        {
            var roughness = (j + 0.5f) / size;
            for (var i = 0; i < size; i++)
                table[j * size + i] = Integrate((i + 0.5f) / size, roughness, samples);
        }

        return table;
    }

    /// <summary>(A, B) for one view angle and perceptual roughness (α = roughness², clamped like the shader's).</summary>
    public static Vector2 Integrate(float nDotV, float roughness, int samples)
    {
        roughness = Math.Clamp(roughness, 0.045f, 1f);
        var alpha = roughness * roughness;
        var a2 = alpha * alpha;
        nDotV = Math.Max(nDotV, 1e-4f);
        var v = new Vector3(MathF.Sqrt(1f - nDotV * nDotV), 0f, nDotV); // N = +Z
        float a = 0f, b = 0f;
        for (var k = 0; k < samples; k++)
        {
            var (u1, u2) = Hammersley((uint)k, (uint)samples);
            var phi = 2f * MathF.PI * u1;
            var cosTheta = MathF.Sqrt((1f - u2) / (1f + (a2 - 1f) * u2));
            var sinTheta = MathF.Sqrt(1f - cosTheta * cosTheta);
            var h = new Vector3(MathF.Cos(phi) * sinTheta, MathF.Sin(phi) * sinTheta, cosTheta);
            var vDotH = Vector3.Dot(v, h);
            var l = 2f * vDotH * h - v;
            var nDotL = l.Z;
            if (nDotL <= 0f || vDotH <= 0f)
                continue;

            // pdf(L) = D·N·H / (4·V·H): the estimator of ∫ D·Vis·F·N·L dL is Vis·F·N·L·4·V·H / N·H.
            var weight = Visibility(nDotV, nDotL, a2) * nDotL * 4f * vDotH / cosTheta;
            var fc = MathF.Pow(1f - vDotH, 5f);
            a += (1f - fc) * weight;
            b += fc * weight;
        }

        return new Vector2(a, b) / samples;
    }

    /// <summary>Smith height-correlated visibility G / (4·N·L·N·V), as <c>pbrVisibility</c> in <c>lights.slang</c>.</summary>
    public static float Visibility(float nDotV, float nDotL, float a2)
    {
        var gv = nDotL * MathF.Sqrt(nDotV * nDotV * (1f - a2) + a2);
        var gl = nDotV * MathF.Sqrt(nDotL * nDotL * (1f - a2) + a2);
        return 0.5f / MathF.Max(gv + gl, 1e-5f);
    }

    private static (float, float) Hammersley(uint i, uint n)
    {
        var bits = (i << 16) | (i >> 16);
        bits = ((bits & 0x55555555u) << 1) | ((bits & 0xAAAAAAAAu) >> 1);
        bits = ((bits & 0x33333333u) << 2) | ((bits & 0xCCCCCCCCu) >> 2);
        bits = ((bits & 0x0F0F0F0Fu) << 4) | ((bits & 0xF0F0F0F0u) >> 4);
        bits = ((bits & 0x00FF00FFu) << 8) | ((bits & 0xFF00FF00u) >> 8);
        return ((float)i / n, bits * 2.3283064365386963e-10f);
    }
}

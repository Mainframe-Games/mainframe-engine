namespace Forest;

/// <summary>
/// Deterministic noise for the valley: integer hashes, smooth value noise and fBm. Only scalar float arithmetic (no
/// SIMD, no transcendental functions in the hash), so the same seed gives the same terrain on every run.
/// </summary>
public static class ValleyNoise
{
    /// <summary>A 32-bit hash of two integers and a seed.</summary>
    public static uint Hash(int x, int y, int seed)
    {
        var h = (uint)x * 374761393u + (uint)y * 668265263u + (uint)seed * 2246822519u;
        h = (h ^ (h >> 13)) * 1274126177u;
        h ^= h >> 16;
        h *= 2654435761u;
        return h ^ (h >> 15);
    }

    /// <summary>A uniform value in [0, 1) from two integers and a seed.</summary>
    public static float Hash01(int x, int y, int seed) => (Hash(x, y, seed) & 0xFFFFFF) / 16777216f;

    /// <summary>Smooth value noise in [0, 1] on a unit lattice.</summary>
    public static float Value(float x, float y, int seed)
    {
        var fx0 = MathF.Floor(x);
        var fy0 = MathF.Floor(y);
        var ix = (int)fx0;
        var iy = (int)fy0;
        var fx = x - fx0;
        var fy = y - fy0;
        fx = fx * fx * (3f - 2f * fx);
        fy = fy * fy * (3f - 2f * fy);
        var a = Hash01(ix, iy, seed);
        var b = Hash01(ix + 1, iy, seed);
        var c = Hash01(ix, iy + 1, seed);
        var d = Hash01(ix + 1, iy + 1, seed);
        var top = a + (b - a) * fx;
        var bottom = c + (d - c) * fx;
        return top + (bottom - top) * fy;
    }

    /// <summary>fBm of <see cref="Value"/> in [0, 1]: <paramref name="octaves"/> octaves from a period of <paramref name="period"/> metres.</summary>
    public static float Fbm(float x, float y, float period, int octaves, int seed)
    {
        float sum = 0f, amplitude = 0.5f, total = 0f, frequency = 1f / period;
        for (var o = 0; o < octaves; o++)
        {
            sum += amplitude * Value(x * frequency, y * frequency, seed + o * 1013);
            total += amplitude;
            amplitude *= 0.5f;
            frequency *= 2.03f;
        }

        return sum / total;
    }

    /// <summary>Ridged fBm in [0, 1] (sharp crests): rocky outcrops.</summary>
    public static float Ridged(float x, float y, float period, int octaves, int seed)
    {
        float sum = 0f, amplitude = 0.5f, total = 0f, frequency = 1f / period;
        for (var o = 0; o < octaves; o++)
        {
            var n = 1f - MathF.Abs(2f * Value(x * frequency, y * frequency, seed + o * 917) - 1f);
            sum += amplitude * n * n;
            total += amplitude;
            amplitude *= 0.5f;
            frequency *= 2.1f;
        }

        return sum / total;
    }

    public static float SmoothStep(float edge0, float edge1, float x)
    {
        var t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}

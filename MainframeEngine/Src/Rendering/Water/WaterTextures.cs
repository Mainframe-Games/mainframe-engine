namespace MainframeEngine;

/// <summary>
/// <see cref="WaterMaterial3D"/>'s built-in textures, generated in C# on first use (256², tileable, deterministic,
/// cached per process): no texture files and no licences to track (ADR 0149: CC0 or procedural). All are linear,
/// mipmapped, bilinear and repeating.
/// </summary>
public static class WaterTextures
{
    /// <summary>Texels per side.</summary>
    public const int Size = 256;

    /// <summary>Root-mean-square slope of the built-in ripples.</summary>
    public const float RmsSlope = 0.2f;

    private static readonly Lock Gate = new();
    private static Texture2D? _normal;
    private static Texture2D? _foam;
    private static Texture2D? _caustics;
    private static Texture2D? _mist;

    private static readonly TextureImportSettings Settings = new()
    {
        ColorSpace = TextureImportColorSpace.Linear,
        Mipmaps = true,
        Filter = TextureFilter.Linear,
        Wrap = TextureWrap.Repeat,
    };

    /// <summary>The ripple normal map (tangent space, +Y up): a sum of 32 sine waves whose wave vectors are whole periods per tile.</summary>
    public static Texture2D Normal
    {
        get
        {
            lock (Gate)
                return _normal ??= Create(GenerateNormalPixels(), "Water normal (built-in)");
        }
    }

    /// <summary>The foam mask (R = G = B = A): the edges of two scales of tileable Worley cells, broken up.</summary>
    public static Texture2D Foam
    {
        get
        {
            lock (Gate)
                return _foam ??= Create(GenerateFoamPixels(), "Water foam (built-in)");
        }
    }

    /// <summary>
    /// The caustic pattern (R = G = B = A, ADR 0173): bright, thin, curved lines around tileable Worley cells whose
    /// domain is warped by whole-period sines, the web light focused by a rippled surface casts on a bed.
    /// </summary>
    public static Texture2D Caustics
    {
        get
        {
            lock (Gate)
                return _caustics ??= Create(GenerateCausticsPixels(), "Water caustics (built-in)");
        }
    }

    /// <summary>Soft billows for spray and mist (R = G = B = A, ADR 0173): four octaves of tileable value noise.</summary>
    public static Texture2D Mist
    {
        get
        {
            lock (Gate)
                return _mist ??= Create(GenerateMistPixels(), "Water mist (built-in)");
        }
    }

    /// <summary>The mist noise's RGBA8 pixels.</summary>
    public static byte[] GenerateMistPixels()
    {
        ReadOnlySpan<int> periods = [4, 8, 16, 32];
        ReadOnlySpan<float> weights = [0.5f, 0.27f, 0.15f, 0.08f];
        var lattice = new float[32 * 32];
        var random = new Lcg(0x5EED_0A15);
        for (var i = 0; i < lattice.Length; i++)
            lattice[i] = random.NextFloat();
        var pixels = new byte[Size * Size * 4];
        for (var v = 0; v < Size; v++)
            for (var u = 0; u < Size; u++)
            {
                var value = 0f;
                for (var o = 0; o < periods.Length; o++)
                {
                    var n = periods[o];
                    var x = (u + 0.5f) / Size * n;
                    var y = (v + 0.5f) / Size * n;
                    var x0 = (int)MathF.Floor(x);
                    var y0 = (int)MathF.Floor(y);
                    var fx = Smooth(x - x0);
                    var fy = Smooth(y - y0);
                    var v00 = Lattice(lattice, x0, y0, n, o);
                    var v10 = Lattice(lattice, x0 + 1, y0, n, o);
                    var v01 = Lattice(lattice, x0, y0 + 1, n, o);
                    var v11 = Lattice(lattice, x0 + 1, y0 + 1, n, o);
                    var a = v00 + (v10 - v00) * fx;
                    var b = v01 + (v11 - v01) * fx;
                    value += (a + (b - a) * fy) * weights[o];
                }

                var c = (byte)Math.Clamp((int)MathF.Round(Smooth((value - 0.2f) / 0.6f) * 255f), 0, 255);
                var p = (v * Size + u) * 4;
                pixels[p] = pixels[p + 1] = pixels[p + 2] = pixels[p + 3] = c;
            }

        return pixels;
    }

    // A lattice value wrapping at the octave's period (an offset per octave decorrelates the octaves).
    private static float Lattice(float[] lattice, int x, int y, int period, int octave)
    {
        var wx = ((x % period) + period) % period;
        var wy = ((y % period) + period) % period;
        return lattice[(wy + octave * 7) % 32 * 32 + (wx + octave * 13) % 32];
    }

    /// <summary>The caustic pattern's RGBA8 pixels.</summary>
    public static byte[] GenerateCausticsPixels()
    {
        var cells = new CellNoise(6, 0xCA05);
        var pixels = new byte[Size * Size * 4];
        for (var v = 0; v < Size; v++)
            for (var u = 0; u < Size; u++)
            {
                var fu = (u + 0.5f) / Size;
                var fv = (v + 0.5f) / Size;
                // Whole periods per tile keep the warp tileable.
                var wu = fu + 0.035f * MathF.Sin(MathF.Tau * (2f * fv + 0.3f)) + 0.02f * MathF.Sin(MathF.Tau * (3f * fu + 5f * fv));
                var wv = fv + 0.035f * MathF.Sin(MathF.Tau * (2f * fu + 0.7f)) + 0.02f * MathF.Sin(MathF.Tau * (4f * fu - 3f * fv));
                wu -= MathF.Floor(wu);
                wv -= MathF.Floor(wv);
                var edge = cells.Edge(wu, wv); // 0 on the boundary between two cells
                var line = Smooth(1f - edge / 0.22f);
                var value = line * line * (0.75f + 0.25f * Smooth(1f - cells.Distance(wu, wv)));
                var b = (byte)Math.Clamp((int)MathF.Round(Math.Clamp(value, 0f, 1f) * 255f), 0, 255);
                var o = (v * Size + u) * 4;
                pixels[o] = pixels[o + 1] = pixels[o + 2] = pixels[o + 3] = b;
            }

        return pixels;
    }

    /// <summary>The normal map's RGBA8 pixels (row by row, v down).</summary>
    public static byte[] GenerateNormalPixels()
    {
        // Waves with integer wave vectors (k cycles per tile along u and v) tile exactly; amplitude ∝ 1 / |k|^1.2.
        const int waves = 32;
        Span<float> kx = stackalloc float[waves];
        Span<float> kz = stackalloc float[waves];
        Span<float> amplitude = stackalloc float[waves];
        Span<float> phase = stackalloc float[waves];
        var random = new Lcg(0x5EED_3A7E);
        var power = 0f; // Σ (amplitude · |k|)²: twice the mean square slope
        for (var w = 0; w < waves; w++)
        {
            int x, z;
            do
            {
                x = random.NextInt(-14, 15);
                z = random.NextInt(-14, 15);
            } while (x * x + z * z < 9);

            kx[w] = x;
            kz[w] = z;
            amplitude[w] = 1f / MathF.Pow(MathF.Sqrt(x * x + z * z), 1.2f);
            phase[w] = random.NextFloat() * MathF.Tau;
            power += amplitude[w] * amplitude[w] * (x * x + z * z);
        }

        var pixels = new byte[Size * Size * 4];
        var strength = RmsSlope / MathF.Sqrt(power * 0.5f); // gentle ripples: real water slopes are rarely above 0.3
        for (var v = 0; v < Size; v++)
            for (var u = 0; u < Size; u++)
            {
                float du = 0f, dv = 0f;
                var fu = (float)u / Size;
                var fv = (float)v / Size;
                for (var w = 0; w < waves; w++)
                {
                    var angle = MathF.Tau * (kx[w] * fu + kz[w] * fv) + phase[w];
                    var slope = amplitude[w] * MathF.Cos(angle);
                    du += slope * kx[w];
                    dv += slope * kz[w];
                }

                // Height slope per texel → tangent-space normal; +Y in the map points up the image (towards −v).
                var nx = -du * strength;
                var ny = dv * strength;
                var inverse = 1f / MathF.Sqrt(nx * nx + ny * ny + 1f);
                var o = (v * Size + u) * 4;
                pixels[o] = Encode(nx * inverse);
                pixels[o + 1] = Encode(ny * inverse);
                pixels[o + 2] = Encode(inverse);
                pixels[o + 3] = 255;
            }

        return pixels;
    }

    /// <summary>The foam mask's RGBA8 pixels.</summary>
    public static byte[] GenerateFoamPixels()
    {
        var coarse = new CellNoise(8, 0xF0A3);
        var fine = new CellNoise(19, 0xB17E);
        var pixels = new byte[Size * Size * 4];
        for (var v = 0; v < Size; v++)
            for (var u = 0; u < Size; u++)
            {
                var fu = (u + 0.5f) / Size;
                var fv = (v + 0.5f) / Size;
                var a = coarse.Edge(fu, fv);  // 0 on a cell edge, growing inwards
                var b = fine.Edge(fu, fv);
                var foam = Smooth(1f - a / 0.16f) * 0.85f + Smooth(1f - b / 0.12f) * 0.55f;
                foam *= 0.55f + 0.45f * Smooth(fine.Distance(fu, fv) * 1.6f); // break the lines up
                var value = (byte)Math.Clamp((int)MathF.Round(Math.Clamp(foam, 0f, 1f) * 255f), 0, 255);
                var o = (v * Size + u) * 4;
                pixels[o] = pixels[o + 1] = pixels[o + 2] = pixels[o + 3] = value;
            }

        return pixels;
    }

    private static Texture2D Create(byte[] pixels, string name)
    {
        var texture = Texture2D.FromPixels(Size, Size, pixels, Settings);
        texture.ResourceName = name;
        return texture;
    }

    private static byte Encode(float n) => (byte)Math.Clamp((int)MathF.Round((n * 0.5f + 0.5f) * 255f), 0, 255);

    private static float Smooth(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>A fixed 32-bit LCG (Numerical Recipes): the same textures on every machine.</summary>
    private struct Lcg(uint seed)
    {
        private uint _state = seed;

        public uint Next() => _state = _state * 1664525u + 1013904223u;

        public float NextFloat() => (Next() >> 8) / 16777216f;

        public int NextInt(int min, int max) => min + (int)((ulong)(Next() >> 8) * (uint)(max - min) >> 24);
    }

    /// <summary>Tileable Worley noise: one jittered point per cell of an n × n grid over the unit square, wrapping.</summary>
    private sealed class CellNoise
    {
        private readonly int _cells;
        private readonly float[] _points;

        public CellNoise(int cells, uint seed)
        {
            _cells = cells;
            _points = new float[cells * cells * 2];
            var random = new Lcg(seed);
            for (var i = 0; i < _points.Length; i++)
                _points[i] = 0.1f + 0.8f * random.NextFloat();
        }

        // F2 − F1 in cell units (0 on the boundary between two cells).
        public float Edge(float u, float v)
        {
            Nearest(u, v, out var f1, out var f2);
            return f2 - f1;
        }

        // F1 in cell units.
        public float Distance(float u, float v)
        {
            Nearest(u, v, out var f1, out _);
            return f1;
        }

        private void Nearest(float u, float v, out float f1, out float f2)
        {
            var x = u * _cells;
            var y = v * _cells;
            var cx = (int)MathF.Floor(x);
            var cy = (int)MathF.Floor(y);
            f1 = f2 = float.MaxValue;
            for (var oy = -1; oy <= 1; oy++)
                for (var ox = -1; ox <= 1; ox++)
                {
                    var gx = cx + ox;
                    var gy = cy + oy;
                    var wx = ((gx % _cells) + _cells) % _cells;
                    var wy = ((gy % _cells) + _cells) % _cells;
                    var p = (wy * _cells + wx) * 2;
                    var dx = gx + _points[p] - x;
                    var dy = gy + _points[p + 1] - y;
                    var d = MathF.Sqrt(dx * dx + dy * dy);
                    if (d < f1)
                    {
                        f2 = f1;
                        f1 = d;
                    }
                    else if (d < f2)
                    {
                        f2 = d;
                    }
                }
        }
    }
}

using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// A terrain's macro texture (ADR 0175, G8e.7's "RVT-lite"): the terrain's surface seen from above, baked once into a
/// two-layer <see cref="Texture2DArray"/> covering the whole terrain, so other surfaces can take on the ground below them
/// (<see cref="StandardMaterial3D.TerrainBlend"/>: boulders and logs sink into moss and dirt). Bound at set 0 binding 7
/// for the world it is in (<c>include/terrain_macro.slang</c>).
/// </summary>
/// <remarks>
/// <para>Layer 0: the albedo (linear, stored as its square root for precision in the shade), alpha = roughness. Layer 1:
/// the world normal's x and z (× 0.5 + 0.5), and the ground height as a 16-bit code in blue (high byte) and alpha (low
/// byte): <see cref="HeightMin"/> + code × <see cref="HeightStep"/>. Shaders filter layer 0 and the normal and rebuild the
/// height from four texel loads.</para>
/// <para>The bake is the splat material's own blend at macro scale, on the CPU: per texel the splat weights (bilinear
/// between cell centres), the four strongest layers' albedo and height at a 32² level of each layer image, the material's
/// height blending, tints and macro variation, each layer's mean roughness, and the terrain's smooth normal. A
/// non-splat material gives its albedo colour everywhere.</para>
/// </remarks>
public sealed class TerrainMacroTexture
{
    /// <summary>Layers in the array.</summary>
    public const int Layers = 2;

    /// <summary>Texels per side by default: 12.5 cm over a 256 m terrain.</summary>
    public const int DefaultResolution = 2048;

    /// <summary>Pixels per side of the layer images the bake samples (about the mip a 12.5 cm texel reads).</summary>
    internal const int LayerSampleSize = 32;

    private static readonly TextureImportSettings Settings = new()
    {
        ColorSpace = TextureImportColorSpace.Linear,
        Mipmaps = true,
        Wrap = TextureWrap.Clamp,
    };

    private TerrainMacroTexture(Texture2DArray texture, float sizeMeters, float heightMin, float heightStep)
    {
        Texture = texture;
        SizeMeters = sizeMeters;
        HeightMin = heightMin;
        HeightStep = heightStep;
    }

    /// <summary>The two layers (<see cref="Resolution"/>², RGBA8, linear, mipmapped).</summary>
    public Texture2DArray Texture { get; }

    /// <summary>Texels per side.</summary>
    public int Resolution => Texture.Width;

    /// <summary>The terrain's side (m); the texture spans terrain-local XZ [0, <see cref="SizeMeters"/>]².</summary>
    public float SizeMeters { get; }

    /// <summary>The height of code 0 (terrain-local m).</summary>
    public float HeightMin { get; }

    /// <summary>Metres per height code.</summary>
    public float HeightStep { get; }

    /// <summary>The baked albedo (linear) of the texel holding terrain-local (<paramref name="x"/>, <paramref name="z"/>).</summary>
    public Vector3 AlbedoAt(float x, float z)
    {
        var o = TexelOffset(x, z);
        var p = Texture.GetLayerPixels(0);
        return new Vector3(Decode(p[o]), Decode(p[o + 1]), Decode(p[o + 2]));

        static float Decode(byte b)
        {
            var s = b / 255f;
            return s * s;
        }
    }

    /// <summary>The baked roughness of the texel holding (<paramref name="x"/>, <paramref name="z"/>).</summary>
    public float RoughnessAt(float x, float z) => Texture.GetLayerPixels(0)[TexelOffset(x, z) + 3] / 255f;

    /// <summary>The baked normal of the texel holding (<paramref name="x"/>, <paramref name="z"/>).</summary>
    public Vector3 NormalAt(float x, float z)
    {
        var o = TexelOffset(x, z);
        var p = Texture.GetLayerPixels(1);
        var nx = p[o] / 255f * 2f - 1f;
        var nz = p[o + 1] / 255f * 2f - 1f;
        return new Vector3(nx, MathF.Sqrt(MathF.Max(1f - nx * nx - nz * nz, 0f)), nz);
    }

    /// <summary>The ground height bilinear between texel centres, as the shaders rebuild it.</summary>
    public float HeightAt(float x, float z)
    {
        var n = Resolution;
        var tx = Math.Clamp(x / SizeMeters * n - 0.5f, 0f, n - 1f);
        var tz = Math.Clamp(z / SizeMeters * n - 0.5f, 0f, n - 1f);
        var ix = Math.Min((int)tx, n - 2);
        var iz = Math.Min((int)tz, n - 2);
        float fx = tx - ix, fz = tz - iz;
        var p = Texture.GetLayerPixels(1);
        var top = Code(p, n, ix, iz) * (1f - fx) + Code(p, n, ix + 1, iz) * fx;
        var bottom = Code(p, n, ix, iz + 1) * (1f - fx) + Code(p, n, ix + 1, iz + 1) * fx;
        return HeightMin + (top * (1f - fz) + bottom * fz) * HeightStep;

        static float Code(ReadOnlySpan<byte> pixels, int n, int i, int j)
        {
            var o = (j * n + i) * 4;
            return pixels[o + 2] * 256f + pixels[o + 3];
        }
    }

    private int TexelOffset(float x, float z)
    {
        var n = Resolution;
        var i = Math.Clamp((int)MathF.Floor(x / SizeMeters * n), 0, n - 1);
        var j = Math.Clamp((int)MathF.Floor(z / SizeMeters * n), 0, n - 1);
        return (j * n + i) * 4;
    }

    /// <summary>
    /// Bakes <paramref name="data"/> (loaded) shaded with <paramref name="material"/> into a
    /// <paramref name="resolution"/>² macro texture. Runs on every core; about 0.3 s at 2048² over eight layers.
    /// </summary>
    public static TerrainMacroTexture Bake(TerrainData data, Material? material, int resolution = DefaultResolution)
    {
        ArgumentNullException.ThrowIfNull(data);
        resolution = Math.Clamp(resolution, 16, 4096);
        var size = (float)data.SizeMeters;
        var bed = data.BedArray;
        var grid = data.Grid;
        var (min, max) = data.BedRange;
        var heightMin = min - 0.5f;
        var heightStep = MathF.Max(max + 0.5f - heightMin, 1f) / 65535f;
        var surface = SurfaceModel.From(data, material);

        var albedo = new byte[resolution * resolution * 4];
        var normal = new byte[resolution * resolution * 4];
        var texel = size / resolution;
        Parallel.For(0, resolution, j =>
        {
            Span<float> weights = stackalloc float[TerrainSplatMaterial3D.MaxLayers];
            for (var i = 0; i < resolution; i++)
            {
                var x = (i + 0.5f) * texel;
                var z = (j + 0.5f) * texel;
                var (colour, roughness) = surface.Shade(x, z, weights);
                var n = grid.SmoothNormalAt(bed, x, z);
                var height = grid.HeightAt(bed, x, z);
                var code = (int)Math.Clamp(MathF.Round((height - heightMin) / heightStep), 0f, 65535f);
                var o = (j * resolution + i) * 4;
                albedo[o] = Encode(MathF.Sqrt(Math.Clamp(colour.X, 0f, 1f)));
                albedo[o + 1] = Encode(MathF.Sqrt(Math.Clamp(colour.Y, 0f, 1f)));
                albedo[o + 2] = Encode(MathF.Sqrt(Math.Clamp(colour.Z, 0f, 1f)));
                albedo[o + 3] = Encode(roughness);
                normal[o] = Encode(n.X * 0.5f + 0.5f);
                normal[o + 1] = Encode(n.Z * 0.5f + 0.5f);
                normal[o + 2] = (byte)(code >> 8);
                normal[o + 3] = (byte)(code & 0xFF);
            }
        });

        var pixels = new byte[albedo.Length * 2];
        albedo.CopyTo(pixels, 0);
        normal.CopyTo(pixels, albedo.Length);
        var texture = new Texture2DArray(resolution, resolution, Layers, pixels, Settings) { ResourceName = "Terrain macro" };
        return new TerrainMacroTexture(texture, size, heightMin, heightStep);

        static byte Encode(float v) => (byte)Math.Clamp(MathF.Round(v * 255f), 0f, 255f);
    }

    /// <summary>The terrain's look at macro scale: the splat material's blend (or a plain colour).</summary>
    private sealed class SurfaceModel
    {
        private readonly int _count;
        private readonly byte[][] _albedo = [];  // per layer: sRGB rgb, a = height (LayerSampleSize²)
        private readonly float[] _invTiling = [];
        private readonly float[] _contrast = [];
        private readonly Vector3[] _tint = [];
        private readonly float[] _roughness = [];
        private readonly uint[] _splat0 = [], _splat1 = [];
        private readonly int _cells;
        private readonly float _cellSize;
        private readonly float _macroStrength, _invMacroScale;
        private readonly Vector3 _plain = new(0.5f);
        private readonly float _plainRoughness = 0.9f;

        private static readonly float[] SrgbTable = BuildTable();

        private SurfaceModel(Vector3 plain, float roughness)
        {
            _plain = plain;
            _plainRoughness = roughness;
        }

        private SurfaceModel(TerrainData data, TerrainSplatMaterial3D material)
        {
            _count = material.LayerCount;
            var layers = new TerrainLayer?[_count];
            for (var l = 0; l < _count; l++)
                layers[l] = material.Layers[l];
            var packed = TerrainLayerPacker.Pack(layers, LayerSampleSize);
            _albedo = new byte[Math.Max(_count, 1)][];
            _invTiling = new float[_count];
            _contrast = new float[_count];
            _tint = new Vector3[_count];
            _roughness = new float[_count];
            for (var l = 0; l < _count; l++)
            {
                _albedo[l] = packed.Albedo.GetLayerPixels(l).ToArray();
                var layer = layers[l];
                _invTiling[l] = 1f / MathF.Max(layer?.TilingMeters ?? 4f, 1e-3f);
                _contrast[l] = MathF.Max(Math.Clamp(layer?.HeightBlendContrast ?? 0.2f, 0f, 1f), 1e-3f);
                var tint = layer?.Tint ?? System.Drawing.Color.White;
                _tint[l] = ColorSpace.SrgbToLinear(new Vector3(tint.R, tint.G, tint.B) / 255f);
                var orm = packed.Orm.GetLayerPixels(l);
                var sum = 0f;
                for (var p = 1; p < orm.Length; p += 4)
                    sum += orm[p];
                _roughness[l] = sum / (orm.Length / 4) / 255f;
            }

            _cells = data.CellsPerSide;
            _cellSize = data.CellSize;
            var all = new Rect2I(0, 0, _cells, _cells);
            _splat0 = new uint[_cells * _cells];
            _splat1 = new uint[_cells * _cells];
            data.GetCells(TerrainLayers.Surface, 0, all, _splat0);
            if (_count > 4)
                data.GetCells(TerrainLayers.Surface, 1, all, _splat1);
            _macroStrength = Math.Clamp(material.MacroStrength, 0f, 1f);
            _invMacroScale = 1f / MathF.Max(material.MacroScaleMeters, 1e-3f);
        }

        public static SurfaceModel From(TerrainData data, Material? material) => material switch
        {
            TerrainSplatMaterial3D splat when splat.LayerCount > 0 && data.Profile == TerrainProfile.Realistic => new SurfaceModel(data, splat),
            StandardMaterial3D standard => new SurfaceModel(
                ColorSpace.SrgbToLinear(new Vector3(standard.AlbedoColor.R, standard.AlbedoColor.G, standard.AlbedoColor.B) / 255f), standard.Roughness),
            _ => new SurfaceModel(new Vector3(0.5f), 0.9f),
        };

        public (Vector3 Albedo, float Roughness) Shade(float x, float z, Span<float> w)
        {
            if (_count == 0)
                return (_plain, _plainRoughness);
            Weights(x, z, w);

            // The four strongest weights (ties keep the lower layer), as the shader's selectStrongest.
            Span<int> index = stackalloc int[4];
            Span<float> weight = stackalloc float[4];
            var taken = 0u;
            var sum = 0f;
            for (var k = 0; k < 4; k++)
            {
                index[k] = 0;
                weight[k] = 0f;
                var best = -1;
                var bestWeight = 0f;
                for (var l = 0; l < _count; l++)
                    if ((taken & (1u << l)) == 0 && w[l] > bestWeight)
                    {
                        best = l;
                        bestWeight = w[l];
                    }

                if (best < 0)
                    continue;
                taken |= 1u << best;
                index[k] = best;
                weight[k] = bestWeight;
                sum += bestWeight;
            }

            if (sum <= 1e-4f)
            {
                index[0] = 0;
                weight[0] = 1f;
                sum = 1f;
            }

            Span<Vector4> samples = stackalloc Vector4[4];
            Span<float> height = stackalloc float[4];
            var highest = -1e9f;
            for (var k = 0; k < 4; k++)
            {
                weight[k] /= sum;
                height[k] = 0f;
                if (weight[k] <= 1e-3f)
                {
                    weight[k] = 0f;
                    continue;
                }

                var l = index[k];
                samples[k] = SampleAlbedo(l, x * _invTiling[l], z * _invTiling[l]);
                height[k] = samples[k].W + weight[k];
                highest = MathF.Max(highest, height[k]);
            }

            var total = 0f;
            for (var k = 0; k < 4; k++)
            {
                if (weight[k] <= 0f)
                    continue;
                weight[k] = MathF.Max(height[k] - (highest - _contrast[index[k]]), 0f);
                total += weight[k];
            }

            var albedo = Vector3.Zero;
            var roughness = 0f;
            for (var k = 0; k < 4; k++)
            {
                if (weight[k] <= 0f)
                    continue;
                var b = weight[k] / MathF.Max(total, 1e-6f);
                albedo += new Vector3(samples[k].X, samples[k].Y, samples[k].Z) * _tint[index[k]] * b;
                roughness += _roughness[index[k]] * b;
            }

            if (_macroStrength > 0f)
            {
                var q = new Vector2(x, z) * _invMacroScale;
                var noise = ValueNoise(q) * 0.65f + ValueNoise(q * 3.7f + new Vector2(17.3f)) * 0.35f;
                var m = (noise - 0.5f) * 2f * _macroStrength;
                albedo *= Vector3.Max(Vector3.One + m * new Vector3(1f, 0.9f, 0.7f), Vector3.Zero);
            }

            return (albedo, roughness);
        }

        // Splat weights bilinear between cell centres (the shader's unmipmapped linear sample, clamped at the edges).
        private void Weights(float x, float z, Span<float> w)
        {
            var fx = Math.Clamp(x / _cellSize - 0.5f, 0f, _cells - 1f);
            var fz = Math.Clamp(z / _cellSize - 0.5f, 0f, _cells - 1f);
            var i0 = Math.Min((int)fx, _cells - 2);
            var j0 = Math.Min((int)fz, _cells - 2);
            float tx = fx - i0, tz = fz - j0;
            w.Clear();
            Accumulate(w, i0, j0, (1f - tx) * (1f - tz));
            Accumulate(w, i0 + 1, j0, tx * (1f - tz));
            Accumulate(w, i0, j0 + 1, (1f - tx) * tz);
            Accumulate(w, i0 + 1, j0 + 1, tx * tz);
        }

        private void Accumulate(Span<float> w, int i, int j, float f)
        {
            if (f <= 0f)
                return;
            var c = j * _cells + i;
            uint a = _splat0[c], b = _splat1[c];
            for (var l = 0; l < Math.Min(_count, 4); l++)
                w[l] += ((a >> (8 * l)) & 0xFF) / 255f * f;
            for (var l = 4; l < _count; l++)
                w[l] += ((b >> (8 * (l - 4))) & 0xFF) / 255f * f;
        }

        // Wrapping bilinear lookup of a layer's albedo (linear rgb) and height (w).
        private Vector4 SampleAlbedo(int layer, float u, float v)
        {
            const int n = LayerSampleSize;
            var fx = u * n - 0.5f;
            var fy = v * n - 0.5f;
            var ix = (int)MathF.Floor(fx);
            var iy = (int)MathF.Floor(fy);
            float tx = fx - ix, ty = fy - iy;
            var pixels = _albedo[layer];
            var a = Texel(pixels, ix, iy);
            var b = Texel(pixels, ix + 1, iy);
            var c = Texel(pixels, ix, iy + 1);
            var d = Texel(pixels, ix + 1, iy + 1);
            return Vector4.Lerp(Vector4.Lerp(a, b, tx), Vector4.Lerp(c, d, tx), ty);

            static Vector4 Texel(byte[] p, int x, int y)
            {
                x = ((x % n) + n) % n;
                y = ((y % n) + n) % n;
                var o = (y * n + x) * 4;
                return new Vector4(SrgbTable[p[o]], SrgbTable[p[o + 1]], SrgbTable[p[o + 2]], p[o + 3] / 255f);
            }
        }

        private static float[] BuildTable()
        {
            var table = new float[256];
            for (var i = 0; i < 256; i++)
                table[i] = ColorSpace.SrgbToLinear(i / 255f);
            return table;
        }

        // The splat shader's value noise (TerrainSplat.vk.frag: pcgHash, hash3, valueNoise).
        private static float ValueNoise(Vector2 p)
        {
            var i = new Vector2(MathF.Floor(p.X), MathF.Floor(p.Y));
            var f = p - i;
            var u = f * f * (new Vector2(3f) - 2f * f);
            int cx = (int)i.X, cy = (int)i.Y;
            var a = Hash(cx, cy);
            var b = Hash(cx + 1, cy);
            var d = Hash(cx, cy + 1);
            var e = Hash(cx + 1, cy + 1);
            return float.Lerp(float.Lerp(a, b, u.X), float.Lerp(d, e, u.X), u.Y);
        }

        private static float Hash(int x, int y) => Pcg((uint)x + Pcg((uint)y)) * (1f / 4294967296f);

        private static uint Pcg(uint v)
        {
            var state = v * 747796405u + 2891336453u;
            var word = ((state >> (int)((state >> 28) + 4u)) ^ state) * 277803737u;
            return (word >> 22) ^ word;
        }
    }
}

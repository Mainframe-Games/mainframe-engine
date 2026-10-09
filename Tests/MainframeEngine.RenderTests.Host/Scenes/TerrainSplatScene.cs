using System.Numerics;

namespace MainframeEngine.RenderTests.Host.Scenes;

using Color = System.Drawing.Color;

/// <summary>
/// ADR 0156: a Realistic <see cref="Terrain3D"/> (64 m at 0.5 m) drawn with a <see cref="TerrainSplatMaterial3D"/> of four
/// layers whose textures are generated here (grass, dirt, rock, moss; height in the albedo's alpha). The ground rolls
/// up to a plateau whose steep west face is painted rock and projected triplanar; a winding dirt path and moss patches
/// meet the grass with height-blended edges; the far side shows the far band. Self-checks at frame 5:
/// <see cref="Terrain3D.SurfaceTagAt"/> on the cliff, the path and the meadow, every chunk drawn, and the splat material
/// drawing through its own pipeline. With <c>--alloc</c> the camera orbits (near and far bands, LOD switches).
/// </summary>
public sealed class TerrainSplatScene(HostOptions host) : RenderTestGame(host)
{
    private const uint CheckFrame = 5;
    private const int TextureSize = 256;
    private Camera3D _camera = null!;
    private Terrain3D _terrain = null!;
    private bool Orbit => Host.AllocationMeasuredFrames > 0;

    // Terrain-local height: rolling ground, a mound, and a plateau east of a steep, slightly diagonal cliff.
    internal static float Height(float x, float z) =>
        1.2f * MathF.Sin(x * 0.21f) * MathF.Cos(z * 0.17f)
        + 0.4f * MathF.Sin(0.8f * x + 0.6f * z)
        + 4f * MathF.Exp(-((x - 18f) * (x - 18f) + (z - 20f) * (z - 20f)) / 120f)
        + 7f / (1f + MathF.Exp(-(x - 0.3f * z - 36f) / 1.1f));

    // The dirt path's centre line (terrain-local z for an x).
    private static float PathZ(float x) => 42f + 5f * MathF.Sin(x * 0.12f);

    internal static void Weights(float x, float z, Span<float> w)
    {
        const float e = 0.25f;
        var dx = (Height(x + e, z) - Height(x - e, z)) / (2 * e);
        var dz = (Height(x, z + e) - Height(x, z - e)) / (2 * e);
        var ny = 1f / MathF.Sqrt(1f + dx * dx + dz * dz);
        var rock = SmoothStep(0.86f, 0.72f, ny);                                   // steeper than ~35°
        var path = MathF.Exp(-MathF.Pow(z - PathZ(x), 2) / 5f) * (1f - rock);
        var moss = SmoothStep(0.45f, 0.62f, Noise2(x * 0.11f, z * 0.11f)) * (1f - rock) * (1f - path);
        w[0] = MathF.Max(1f - rock - path - moss, 0f) + 0.05f; // grass
        w[1] = path;                                            // dirt
        w[2] = rock;                                            // rock
        w[3] = moss;                                            // moss
    }

    protected override void LoadScene()
    {
        var scene = new Node3D { Name = nameof(TerrainSplatScene) };
        _camera = new Camera3D { Name = "Camera", Position = new Vector3(-4, 6.5f, 26) };
        _camera.LookAt(new Vector3(12, 3f, -2));
        scene.AddChild(_camera);
        scene.AddChild(new WorldEnvironment { Name = "Environment", Sky = new Sky { Mode = SkyEnvironmentType.Procedural } });

        var sun = new DirectionalLight3D { Name = "Sun", Color = new Vector3(1f, 0.95f, 0.86f), Energy = 1.1f };
        sun.LookAt(Vector3.Normalize(new Vector3(0.75f, -0.5f, -0.35f))); // from the west: lights the cliff face
        scene.AddChild(sun);

        var data = TerrainData.Create(TerrainProfile.Realistic, 64, 0.5f, 16);
        data.SetHeightsFrom(Height);
        data.SetWeightsFrom(Weights);
        _terrain = new Terrain3D
        {
            Name = "Terrain",
            Position = new Vector3(-32, 0, -32),
            Data = data,
            Material = new TerrainSplatMaterial3D
            {
                LayerTextureSize = TextureSize,
                DetailDistance = 30f, // the far half of the view shows the far band and the transition
                FarDistance = 60f,
                Layers =
                [
                    Layer("Grass", "grass", LayerTextures.Kind.Grass, 3f, 0.25f),
                    Layer("Dirt", "dirt", LayerTextures.Kind.Dirt, 2.5f, 0.15f),
                    Layer("Rock", "rock", LayerTextures.Kind.Rock, 6f, 0.2f),
                    Layer("Moss", "moss", LayerTextures.Kind.Moss, 3.5f, 0.3f),
                ],
            },
            LodBias = 0.5f,
        };
        scene.AddChild(_terrain);
        Tree.ChangeScene(scene);
    }

    private static TerrainLayer Layer(string name, string tag, LayerTextures.Kind kind, float tiling, float contrast)
    {
        var (albedo, normal, orm) = LayerTextures.Generate(kind, TextureSize);
        return new TerrainLayer
        {
            Name = name,
            Tag = tag,
            Albedo = albedo,
            Normal = normal,
            Orm = orm,
            TilingMeters = tiling,
            HeightBlendContrast = contrast,
        };
    }

    protected override void UpdateScene(in GameTime gameTime)
    {
        if (Orbit)
        {
            var angle = gameTime.FrameCount % 120 / 120f * MathF.Tau;
            _camera.Position = new Vector3(40 * MathF.Sin(angle), 10, 40 * MathF.Cos(angle));
            _camera.LookAt(new Vector3(0, 3, 0));
            return;
        }

        if (gameTime.FrameCount != CheckFrame)
            return;
        // Terrain-local points → world (the terrain sits at −32, 0, −32).
        Expect(SteepPoint(), "rock", "the cliff face");
        Expect(new Vector2(20f, PathZ(20f)), "dirt", "the path");
        Expect(new Vector2(8f, 8f), null, "the meadow (grass or moss)");

        var chunks = _terrain.ChunksPerSide;
        if (Servers.Render is { } render)
        {
            if (render.MeshStats.Instances != chunks * chunks)
                Fail($"expected {chunks * chunks} terrain chunk instances, got {render.MeshStats.Instances}");
            if (render.MeshStats.DrawCalls == 0)
                Fail("the terrain drew nothing");
        }
    }

    private static Vector2 SteepPoint()
    {
        // The cliff's middle at z = 20: x where x − 0.3 z = 36.
        return new Vector2(36f + 0.3f * 20f, 20f);
    }

    private void Expect(Vector2 local, string? tag, string what)
    {
        var actual = _terrain.SurfaceTagAt(local.X - 32f, local.Y - 32f);
        if (tag is null ? actual is not ("grass" or "moss") : actual != tag)
            Fail($"{what}: SurfaceTagAt is '{actual}', expected '{tag ?? "grass or moss"}'");
    }

    private static float SmoothStep(float edge0, float edge1, float x)
    {
        var t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static float Noise2(float x, float y) => LayerTextures.ValueNoise(x, y, 0, 7);

    protected override void DisposeScene()
    {
    }
}

/// <summary>
/// Tileable procedural PBR texture sets for the terrain splat test: periodic value-noise fBm shaped into grass, dirt
/// with pebbles, layered rock with cracks and blotchy moss. The albedo's alpha is the height; normals come from the
/// height by wrapped central differences; ORM's occlusion follows the height.
/// </summary>
internal static class LayerTextures
{
    public enum Kind { Grass, Dirt, Rock, Moss }

    public static (Texture2D Albedo, Texture2D Normal, Texture2D Orm) Generate(Kind kind, int size)
    {
        var height = new float[size * size];
        var color = new Vector3[size * size];
        var roughness = new float[size * size];
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var u = (float)x / size;
                var v = (float)y / size;
                var i = y * size + x;
                switch (kind)
                {
                    case Kind.Grass:
                        {
                            var blades = Fbm(u * 2f + 0.3f * Fbm(u, v, 4, 2, 11), v * 16f, 16, 3, 1); // vertical streaks
                            var clumps = Fbm(u, v, 4, 4, 2);
                            height[i] = 0.25f + 0.55f * blades * clumps + 0.2f * clumps;
                            color[i] = Vector3.Lerp(new Vector3(0.20f, 0.33f, 0.09f), new Vector3(0.42f, 0.55f, 0.18f), blades * 0.7f + clumps * 0.3f);
                            roughness[i] = 0.85f;
                            break;
                        }
                    case Kind.Dirt:
                        {
                            var soil = Fbm(u, v, 8, 4, 3);
                            var pebbles = SmoothStep(0.62f, 0.72f, Fbm(u, v, 32, 2, 4));
                            height[i] = 0.3f * soil + 0.7f * pebbles;
                            color[i] = Vector3.Lerp(new Vector3(0.30f, 0.21f, 0.13f), new Vector3(0.45f, 0.36f, 0.26f), soil);
                            color[i] = Vector3.Lerp(color[i], new Vector3(0.55f, 0.52f, 0.48f), pebbles);
                            roughness[i] = 0.9f - 0.2f * pebbles;
                            break;
                        }
                    case Kind.Rock:
                        {
                            var warp = Fbm(u, v, 4, 3, 5);
                            var strata = 0.5f + 0.5f * MathF.Sin((v + 0.15f * warp) * MathF.Tau * 6f);
                            var crack = SmoothStep(0.08f, 0.0f, MathF.Abs(Fbm(u, v, 8, 3, 6) - 0.5f));
                            var grain = Fbm(u, v, 32, 2, 7);
                            height[i] = (0.55f + 0.3f * strata + 0.15f * grain) * (1f - 0.8f * crack);
                            color[i] = Vector3.Lerp(new Vector3(0.10f, 0.095f, 0.085f), new Vector3(0.30f, 0.28f, 0.25f), 0.6f * strata + 0.4f * grain) * (1f - 0.6f * crack);
                            roughness[i] = 0.65f + 0.2f * grain;
                            break;
                        }
                    default:
                        {
                            var blobs = Fbm(u, v, 8, 4, 8);
                            var fuzz = Fbm(u, v, 32, 2, 9);
                            height[i] = 0.4f + 0.45f * blobs + 0.15f * fuzz;
                            color[i] = Vector3.Lerp(new Vector3(0.05f, 0.09f, 0.02f), new Vector3(0.36f, 0.34f, 0.05f), blobs * 0.8f + fuzz * 0.2f);
                            roughness[i] = 0.95f;
                            break;
                        }
                }
            }

        var albedo = new byte[size * size * 4];
        var normal = new byte[size * size * 4];
        var orm = new byte[size * size * 4];
        var strength = kind == Kind.Rock ? 6f : 3f;
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var i = y * size + x;
                var h = Math.Clamp(height[i], 0f, 1f);
                var c = ColorSpace.LinearToSrgb(Vector3.Clamp(color[i], Vector3.Zero, Vector3.One));
                albedo[i * 4] = Byte(c.X);
                albedo[i * 4 + 1] = Byte(c.Y);
                albedo[i * 4 + 2] = Byte(c.Z);
                albedo[i * 4 + 3] = Byte(h);

                // OpenGL convention: +Y (green) up the image, i.e. towards smaller rows.
                var dx = (height[y * size + (x + 1) % size] - height[y * size + (x + size - 1) % size]) * 0.5f * strength;
                var dy = (height[(y + size - 1) % size * size + x] - height[(y + 1) % size * size + x]) * 0.5f * strength;
                var n = Vector3.Normalize(new Vector3(-dx, -dy, 1f));
                normal[i * 4] = Byte(n.X * 0.5f + 0.5f);
                normal[i * 4 + 1] = Byte(n.Y * 0.5f + 0.5f);
                normal[i * 4 + 2] = Byte(n.Z * 0.5f + 0.5f);
                normal[i * 4 + 3] = 255;

                orm[i * 4] = Byte(0.55f + 0.45f * h);
                orm[i * 4 + 1] = Byte(roughness[i]);
                orm[i * 4 + 2] = 0;
                orm[i * 4 + 3] = 255;
            }

        return (Texture2D.FromPixels(size, size, albedo), Texture2D.FromPixels(size, size, normal), Texture2D.FromPixels(size, size, orm));
    }

    private static byte Byte(float v) => (byte)Math.Clamp(MathF.Round(v * 255f), 0f, 255f);

    private static float SmoothStep(float edge0, float edge1, float x)
    {
        var t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    // fBm of periodic value noise over the unit square (seamless): `octaves` octaves from `period` cells.
    private static float Fbm(float u, float v, int period, int octaves, int seed)
    {
        float sum = 0f, amplitude = 0.5f, total = 0f;
        for (var o = 0; o < octaves; o++)
        {
            sum += amplitude * ValueNoise(u * period, v * period, period, seed + o * 31);
            total += amplitude;
            amplitude *= 0.5f;
            period *= 2;
        }

        return sum / total;
    }

    /// <summary>Value noise on an integer lattice, periodic over <paramref name="period"/> cells (0 = not periodic).</summary>
    public static float ValueNoise(float x, float y, int period, int seed)
    {
        var ix = (int)MathF.Floor(x);
        var iy = (int)MathF.Floor(y);
        var fx = x - ix;
        var fy = y - iy;
        fx = fx * fx * (3f - 2f * fx);
        fy = fy * fy * (3f - 2f * fy);
        var a = Lattice(ix, iy, period, seed);
        var b = Lattice(ix + 1, iy, period, seed);
        var c = Lattice(ix, iy + 1, period, seed);
        var d = Lattice(ix + 1, iy + 1, period, seed);
        return float.Lerp(float.Lerp(a, b, fx), float.Lerp(c, d, fx), fy);
    }

    private static float Lattice(int x, int y, int period, int seed)
    {
        if (period > 0)
        {
            x = ((x % period) + period) % period;
            y = ((y % period) + period) % period;
        }

        var h = (uint)(x * 374761393 + y * 668265263 + seed * 144269504);
        h = (h ^ (h >> 13)) * 1274126177u;
        h ^= h >> 16;
        return (h & 0xFFFFFF) / (float)0xFFFFFF;
    }
}

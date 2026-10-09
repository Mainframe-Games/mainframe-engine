using System.Numerics;

namespace MainframeEngine;

/// <summary>The leaf images <see cref="TreeTexturePainter"/> draws.</summary>
public enum TreeLeafKind : byte
{
    /// <summary>A birch sprig: small triangular-ovate, serrated leaves, light green.</summary>
    Birch,

    /// <summary>A beech sprig: oval, wavy-edged leaves with straight parallel veins, deep green.</summary>
    Beech,

    /// <summary>A spruce spray: stiff, dark blue-green needles all around the shoots.</summary>
    Spruce,

    /// <summary>A fir spray: soft, flat needles in two rows, green with pale undersides.</summary>
    Fir,
}

/// <summary>The bark images <see cref="TreeTexturePainter"/> draws.</summary>
public enum TreeBarkKind : byte
{
    /// <summary>White birch bark with dark horizontal lenticels and dark fissured patches.</summary>
    Birch,

    /// <summary>Smooth grey beech bark with faint bands and lichen spots.</summary>
    Beech,
}

/// <summary>
/// Draws the new species' leaf and bark images procedurally (ADR 0172: no third-party sources), deterministic per seed:
/// leaf sprigs as RGBA8 with transparent backgrounds (the colour bled into them), and tileable bark as colour, OpenGL
/// normal and roughness maps. The engine's <c>Content/Trees/Leaves/{birch,beech,spruce,fir}.png</c> and
/// <c>Content/Trees/Bark/{Birch,Beech}/</c> were made with it (<see cref="WriteContent"/>).
/// </summary>
public static class TreeTexturePainter
{
    // ── Leaves ─────────────────────────────────────────────────────────────────

    /// <summary>A <paramref name="size"/>² sprig of <paramref name="kind"/> (sRGB RGBA8, straight alpha).</summary>
    public static byte[] PaintLeaf(TreeLeafKind kind, int size = 1024, int seed = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 32);
        const int ss = 2; // supersampling
        var canvas = new Canvas(size * ss);
        var rng = new Random(seed * 7919 + (int)kind);
        switch (kind)
        {
            case TreeLeafKind.Birch or TreeLeafKind.Beech:
                PaintBroadleafSprig(canvas, kind, rng);
                break;
            default:
                PaintNeedleSpray(canvas, kind, rng);
                break;
        }

        var rgba = canvas.Downsample(ss);
        ImageOps.FixAlphaEdges(rgba, size, size);
        return rgba;
    }

    private static void PaintBroadleafSprig(Canvas c, TreeLeafKind kind, Random rng)
    {
        var s = c.Size;
        var birch = kind == TreeLeafKind.Birch;
        var stemColor = birch ? L(0.36f, 0.24f, 0.16f) : L(0.42f, 0.33f, 0.22f);
        var baseColor = birch ? L(0.40f, 0.58f, 0.20f) : L(0.27f, 0.45f, 0.14f);
        // The stem: a gentle curve from the bottom centre to near the top.
        Vector2 Stem(float t) => new(s * (0.5f + 0.05f * MathF.Sin(t * 3.1f + 0.4f)), s * (0.97f - 0.86f * t));
        var leaves = birch ? 9 : 8;
        for (var i = 0; i < leaves; i++)
        {
            var t = 0.12f + 0.84f * i / (leaves - 1);
            var at = Stem(t);
            var side = i % 2 == 0 ? -1f : 1f;
            if (i == leaves - 1)
                side = 0f; // the terminal leaf points up the stem
            var angle = side * (birch ? 0.95f : 1.05f) * (0.85f + 0.3f * (float)rng.NextDouble()) - (side == 0f ? 0f : 0.25f * side * t);
            var direction = new Vector2(MathF.Sin(angle), -MathF.Cos(angle));
            var length = s * (birch ? 0.24f : 0.29f) * (0.75f + 0.35f * (float)rng.NextDouble()) * (1f - 0.25f * t);
            var width = length * (birch ? 0.66f : 0.56f);
            var petiole = length * (birch ? 0.22f : 0.08f);
            var start = at + direction * petiole;
            c.Line(at, start, s * 0.004f, new Vector4(stemColor, 1f));
            var tone = baseColor * (0.85f + 0.3f * (float)rng.NextDouble()) + new Vector3(0.03f * (float)rng.NextDouble());
            PaintLeafBlade(c, start, direction, length, width, tone, birch, rng);
        }

        for (var k = 0; k < 48; k++)
        {
            float t0 = k / 48f, t1 = (k + 1) / 48f;
            c.Line(Stem(t0), Stem(t1), s * (0.008f - 0.005f * t0), new Vector4(stemColor, 1f));
        }
    }

    // One blade from `start` along `direction`: birch triangular-ovate and serrated, beech oval and wavy; a darker midrib
    // and veins, lighter towards the margin.
    private static void PaintLeafBlade(Canvas c, Vector2 start, Vector2 direction, float length, float width, Vector3 tone, bool birch, Random rng)
    {
        var normal = new Vector2(-direction.Y, direction.X);
        var serration = birch ? 18f : 7f;
        var phase = (float)rng.NextDouble() * 6.28f;
        float HalfWidth(float u)
        {
            // u: 0 at the base, 1 at the tip.
            var shape = birch
                ? MathF.Pow(MathF.Sin(MathF.PI * MathF.Pow(u, 0.75f)), 0.9f) * (1.05f - 0.35f * u) // widest low: deltoid
                : MathF.Sin(MathF.PI * MathF.Pow(u, 0.9f)); // oval
            var edge = birch ? 0.06f * MathF.Abs(MathF.Sin(u * serration * MathF.PI + phase)) : 0.035f * MathF.Sin(u * serration * MathF.PI + phase);
            return MathF.Max(shape - edge, 0f) * width * 0.5f;
        }

        var min = Vector2.Min(start - normal * width, Vector2.Min(start + direction * length - normal * width, start + normal * width));
        var max = Vector2.Max(start - normal * width, Vector2.Max(start + direction * length + normal * width, start + normal * width));
        min = Vector2.Min(min, start + direction * length + normal * width);
        max = Vector2.Max(max, start + direction * length - normal * width);
        var veins = birch ? 8f : 7f;
        c.Fill(min, max, p =>
        {
            var d = p - start;
            var u = Vector2.Dot(d, direction) / length;
            if (u is < 0f or > 1f)
                return null;
            var v = Vector2.Dot(d, normal);
            var half = HalfWidth(u);
            if (MathF.Abs(v) > half)
                return null;
            var across = half > 0f ? MathF.Abs(v) / half : 0f;
            var shade = 0.9f + 0.18f * across - 0.08f * u;
            // Midrib, and parallel side veins running out towards the tip.
            var midrib = MathF.Abs(v) < length * 0.008f;
            var vein = MathF.Abs(MathF.Sin((u - across * 0.35f) * veins * MathF.PI)) < 0.07f && across > 0.08f && across < 0.85f;
            var color = tone * shade * (midrib ? 0.7f : vein ? 0.82f : 1f);
            return new Vector4(color, 1f);
        });
    }

    private static void PaintNeedleSpray(Canvas c, TreeLeafKind kind, Random rng)
    {
        var s = c.Size;
        var spruce = kind == TreeLeafKind.Spruce;
        var stem = spruce ? L(0.40f, 0.26f, 0.15f) : L(0.38f, 0.30f, 0.18f);
        var needle = spruce ? L(0.11f, 0.24f, 0.18f) : L(0.17f, 0.33f, 0.12f);
        var pale = spruce ? L(0.20f, 0.34f, 0.30f) : L(0.40f, 0.52f, 0.36f);

        // A main shoot with side shoots (the spray), each carrying needles.
        var shoots = new List<(Vector2 A, Vector2 B, float Width)>();
        Vector2 main0 = new(s * 0.5f, s * 0.98f), main1 = new(s * 0.52f, s * 0.08f);
        shoots.Add((main0, main1, s * 0.007f));
        var sides = spruce ? 10 : 8;
        for (var i = 0; i < sides; i++)
        {
            var t = 0.22f + 0.66f * i / (sides - 1);
            var at = Vector2.Lerp(main0, main1, t);
            var side = i % 2 == 0 ? -1f : 1f;
            var angle = side * (spruce ? 0.85f : 1.0f) * (0.9f + 0.2f * (float)rng.NextDouble());
            var length = s * (0.36f - 0.24f * t) * (0.85f + 0.3f * (float)rng.NextDouble());
            shoots.Add((at, at + new Vector2(MathF.Sin(angle), -MathF.Cos(angle)) * length, s * 0.004f));
        }

        // Needles first (behind the shoots).
        foreach (var (a, b, _) in shoots)
        {
            var axis = Vector2.Normalize(b - a);
            var normal = new Vector2(-axis.Y, axis.X);
            var length = Vector2.Distance(a, b);
            var count = 2 * (int)(length / (s * (spruce ? 0.0045f : 0.0055f)));
            for (var n = 0; n < count; n++)
            {
                var t = (n + (float)rng.NextDouble()) / count;
                var root = Vector2.Lerp(a, b, t);
                var side = spruce ? (rng.Next(2) == 0 ? -1f : 1f) : (n % 2 == 0 ? -1f : 1f);
                var spread = spruce ? 0.55f + 0.5f * (float)rng.NextDouble() : 1.05f + 0.15f * (float)rng.NextDouble(); // radians from the axis
                var dir = Vector2.Normalize(axis * MathF.Cos(spread) + normal * side * MathF.Sin(spread));
                var needleLength = s * (spruce ? 0.05f : 0.06f) * (0.8f + 0.4f * (float)rng.NextDouble()) * (0.75f + 0.25f * (1f - t));
                var tone = Vector3.Lerp(needle, pale, spruce ? 0.15f * (float)rng.NextDouble() : (n % 3 == 0 ? 0.45f : 0.1f));
                tone *= 0.85f + 0.3f * (float)rng.NextDouble();
                c.Line(root, root + dir * needleLength, s * (spruce ? 0.0055f : 0.0075f), new Vector4(tone, 1f), taper: true);
            }
        }

        foreach (var (a, b, width) in shoots)
            c.Line(a, b, width, new Vector4(stem, 1f), taper: true);
    }

    // ── Bark ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// A tileable <paramref name="size"/>² bark of <paramref name="kind"/>: colour (sRGB), normal (OpenGL, +Y up) and
    /// roughness (grey) as RGBA8.
    /// </summary>
    public static (byte[] Color, byte[] Normal, byte[] Roughness) PaintBark(TreeBarkKind kind, int size = 1024, int seed = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 32);
        var height = new float[size * size];
        var tone = new Vector3[size * size];
        var rough = new float[size * size];
        var birch = kind == TreeBarkKind.Birch;
        var marks = new List<(float X, float Y, float W, float H, float Depth)>();
        var rng = new Random(seed * 104729 + (int)kind);
        if (birch)
        {
            for (var i = 0; i < 160; i++) // lenticels: short dark horizontal dashes
                marks.Add(((float)rng.NextDouble(), (float)rng.NextDouble(), 0.02f + 0.08f * (float)rng.NextDouble(), 0.004f + 0.004f * (float)rng.NextDouble(), 1f));
            for (var i = 0; i < 9; i++) // fissured dark patches, wide and low
                marks.Add(((float)rng.NextDouble(), (float)rng.NextDouble(), 0.05f + 0.09f * (float)rng.NextDouble(), 0.012f + 0.02f * (float)rng.NextDouble(), 2f));
        }
        else
        {
            for (var i = 0; i < 45; i++) // lichen spots
                marks.Add(((float)rng.NextDouble(), (float)rng.NextDouble(), 0.006f + 0.016f * (float)rng.NextDouble(), 0.006f + 0.016f * (float)rng.NextDouble(), 3f));
        }

        Parallel.For(0, size, y =>
        {
            for (var x = 0; x < size; x++)
            {
                float u = (float)x / size, v = (float)y / size;
                var n1 = PeriodicNoise(u * 6f, v * 3f, 6, 3, seed);
                var n2 = PeriodicNoise(u * 24f, v * 16f, 24, 16, seed + 3);
                float h, r;
                Vector3 col;
                if (birch)
                {
                    col = L(0.86f, 0.85f, 0.80f) * (0.92f + 0.08f * n1) + new Vector3(0.03f, 0.02f, -0.01f) * n2;
                    h = 0.5f + 0.1f * n2;
                    r = 0.55f + 0.1f * n2;
                    // Faint horizontal peeling bands.
                    var band = 0.5f + 0.5f * MathF.Sin(v * MathF.Tau * 9f + 2f * n1);
                    col *= 0.96f + 0.04f * band;
                }
                else
                {
                    col = L(0.46f, 0.46f, 0.43f) * (0.9f + 0.12f * n1) + L(0.02f, 0.025f, 0.01f) * n2;
                    h = 0.5f + 0.06f * n2 + 0.05f * n1;
                    r = 0.62f + 0.08f * n2;
                    var band = 0.5f + 0.5f * MathF.Sin(v * MathF.Tau * 5f + 3f * n1);
                    col *= 0.97f + 0.05f * band;
                }

                foreach (var (mx, my, mw, mh, depth) in marks)
                {
                    // Wrapped distance (tileable).
                    var dx = MathF.Abs(u - mx);
                    dx = MathF.Min(dx, 1f - dx);
                    var dy = MathF.Abs(v - my);
                    dy = MathF.Min(dy, 1f - dy);
                    // Ragged edges: the ellipse's radius wobbles with the noise.
                    var wobble = 1f + 0.35f * PeriodicNoise(u * 50f, v * 50f, 50, 50, seed + 21);
                    var e = ((dx / mw) * (dx / mw) + (dy / mh) * (dy / mh)) * wobble;
                    if (e >= 1f)
                        continue;
                    var k = 1f - e;
                    if (depth < 1.5f) // lenticel
                    {
                        col = Vector3.Lerp(col, L(0.12f, 0.10f, 0.09f), MathF.Min(1f, k * 2.2f));
                        h -= 0.25f * k;
                        r = MathF.Max(r, 0.85f);
                    }
                    else if (depth < 2.5f) // fissured patch
                    {
                        var crack = 0.5f + 0.5f * PeriodicNoise(u * 40f, v * 40f, 40, 40, seed + 9);
                        col = Vector3.Lerp(col, L(0.24f, 0.21f, 0.19f) * (0.7f + 0.6f * crack), MathF.Min(1f, k * 1.6f));
                        h -= 0.35f * k * crack;
                        r = MathF.Max(r, 0.9f);
                    }
                    else // lichen
                    {
                        col = Vector3.Lerp(col, L(0.58f, 0.62f, 0.50f), MathF.Min(1f, k * 1.5f) * 0.45f);
                        h += 0.08f * k;
                    }
                }

                var i = y * size + x;
                height[i] = h;
                tone[i] = col;
                rough[i] = r;
            }
        });

        var color = new byte[size * size * 4];
        var normal = new byte[size * size * 4];
        var roughness = new byte[size * size * 4];
        var strength = birch ? 6f : 4f;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var i = y * size + x;
                var o = i * 4;
                var srgb = ColorSpace.LinearToSrgb(Vector3.Clamp(tone[i], Vector3.Zero, Vector3.One));
                (color[o], color[o + 1], color[o + 2], color[o + 3]) = (Byte(srgb.X), Byte(srgb.Y), Byte(srgb.Z), 255);
                // Central differences, wrapped; +Y up = towards decreasing v (the image's top).
                var hx = height[y * size + (x + 1) % size] - height[y * size + (x + size - 1) % size];
                var hy = height[(y + size - 1) % size * size + x] - height[(y + 1) % size * size + x];
                var n = Vector3.Normalize(new Vector3(-hx * strength, -hy * strength, 1f));
                (normal[o], normal[o + 1], normal[o + 2], normal[o + 3]) = (Byte(n.X * 0.5f + 0.5f), Byte(n.Y * 0.5f + 0.5f), Byte(n.Z * 0.5f + 0.5f), 255);
                var r = Byte(rough[i]);
                (roughness[o], roughness[o + 1], roughness[o + 2], roughness[o + 3]) = (r, r, r, 255);
            }
        }

        return (color, normal, roughness);
    }

    /// <summary>
    /// Writes every image into <paramref name="contentFolder"/> (the engine's <c>Content</c>): <c>Trees/Leaves/&lt;kind&gt;.png</c>
    /// at 1024² and <c>Trees/Bark/&lt;Kind&gt;/&lt;Kind&gt;_{Color,NormalGL,Roughness}.png</c> at 1024².
    /// </summary>
    public static void WriteContent(string contentFolder)
    {
        foreach (var kind in Enum.GetValues<TreeLeafKind>())
        {
            var path = Path.Combine(contentFolder, "Trees", "Leaves", $"{kind.ToString().ToLowerInvariant()}.png");
            File.WriteAllBytes(path, Png.EncodeRgba8(1024, 1024, PaintLeaf(kind)));
        }

        foreach (var kind in Enum.GetValues<TreeBarkKind>())
        {
            var folder = Path.Combine(contentFolder, "Trees", "Bark", kind.ToString());
            Directory.CreateDirectory(folder);
            var (color, normal, roughness) = PaintBark(kind);
            File.WriteAllBytes(Path.Combine(folder, $"{kind}_Color.png"), Png.EncodeRgba8(1024, 1024, color));
            File.WriteAllBytes(Path.Combine(folder, $"{kind}_NormalGL.png"), Png.EncodeRgba8(1024, 1024, normal));
            File.WriteAllBytes(Path.Combine(folder, $"{kind}_Roughness.png"), Png.EncodeRgba8(1024, 1024, roughness));
        }
    }

    // Value noise over a torus (periodX × periodY cells), -1..1, smooth.
    private static float PeriodicNoise(float x, float y, int periodX, int periodY, int seed)
    {
        var x0 = (int)MathF.Floor(x);
        var y0 = (int)MathF.Floor(y);
        float fx = x - x0, fy = y - y0;
        fx = fx * fx * (3f - 2f * fx);
        fy = fy * fy * (3f - 2f * fy);
        float Corner(int cx, int cy)
        {
            cx = ((cx % periodX) + periodX) % periodX;
            cy = ((cy % periodY) + periodY) % periodY;
            var h = (uint)(cx * 374761393 + cy * 668265263 + seed * 2246822519);
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= h >> 16;
            return (h & 0xFFFF) / 32767.5f - 1f;
        }

        var a = Corner(x0, y0) + (Corner(x0 + 1, y0) - Corner(x0, y0)) * fx;
        var b = Corner(x0, y0 + 1) + (Corner(x0 + 1, y0 + 1) - Corner(x0, y0 + 1)) * fx;
        return a + (b - a) * fy;
    }

    // An sRGB-authored colour in linear light.
    private static Vector3 L(float r, float g, float b) => ColorSpace.SrgbToLinear(new Vector3(r, g, b));

    private static byte Byte(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);

    // A linear-colour RGBA canvas with straight alpha; shapes overwrite (painter's order).
    private sealed class Canvas(int size)
    {
        private readonly Vector4[] _pixels = new Vector4[size * size];

        public int Size { get; } = size;

        // Fills the pixels in [min, max] whose centre the shader colours (null: untouched).
        public void Fill(Vector2 min, Vector2 max, Func<Vector2, Vector4?> shader)
        {
            int x0 = Math.Max(0, (int)min.X), x1 = Math.Min(Size - 1, (int)MathF.Ceiling(max.X));
            int y0 = Math.Max(0, (int)min.Y), y1 = Math.Min(Size - 1, (int)MathF.Ceiling(max.Y));
            for (var y = y0; y <= y1; y++)
            {
                for (var x = x0; x <= x1; x++)
                {
                    if (shader(new Vector2(x + 0.5f, y + 0.5f)) is { } color)
                        _pixels[y * Size + x] = color;
                }
            }
        }

        // A round-capped line of `width` (tapering to a third at `b`).
        public void Line(Vector2 a, Vector2 b, float width, Vector4 color, bool taper = false)
        {
            var min = Vector2.Min(a, b) - new Vector2(width);
            var max = Vector2.Max(a, b) + new Vector2(width);
            var ab = b - a;
            var lengthSquared = MathF.Max(ab.LengthSquared(), 1e-6f);
            Fill(min, max, p =>
            {
                var t = Math.Clamp(Vector2.Dot(p - a, ab) / lengthSquared, 0f, 1f);
                var radius = width * 0.5f * (taper ? 1f - 0.65f * t : 1f);
                return Vector2.DistanceSquared(p, a + ab * t) <= radius * radius ? color : null;
            });
        }

        // Box-filters ss × ss pixels into sRGB RGBA8 (colour averaged over the covered ones).
        public byte[] Downsample(int ss)
        {
            var size = Size / ss;
            var rgba = new byte[size * size * 4];
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var sum = Vector3.Zero;
                    var alpha = 0f;
                    for (var sy = 0; sy < ss; sy++)
                    {
                        for (var sx = 0; sx < ss; sx++)
                        {
                            var p = _pixels[(y * ss + sy) * Size + x * ss + sx];
                            sum += new Vector3(p.X, p.Y, p.Z) * p.W;
                            alpha += p.W;
                        }
                    }

                    var o = (y * size + x) * 4;
                    var color = alpha > 0f ? ColorSpace.LinearToSrgb(Vector3.Clamp(sum / alpha, Vector3.Zero, Vector3.One)) : Vector3.Zero;
                    (rgba[o], rgba[o + 1], rgba[o + 2], rgba[o + 3]) = (Byte(color.X), Byte(color.Y), Byte(color.Z), Byte(alpha / (ss * ss)));
                }
            }

            return rgba;
        }
    }
}

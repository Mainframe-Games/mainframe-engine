using System.Numerics;
using MainframeEngine;

namespace Forest;

/// <summary>
/// Wild flowers for the meadow and the forest edges (ADR 0178, look-dev against the Unreal references): procedural
/// patches drawn with the grass material (vertex colours, translucent, wind from <see cref="MeshSurface.Custom0"/>).
/// Each mesh is a whole patch (heather: a mound of pink-tipped spikes; daisies: white heads with yellow eyes; buttercups:
/// small yellow cups on tall stems), so a low foliage density reads as scattered drifts of flowers rather than an even
/// sprinkle. Deterministic per seed; own work (CC0).
/// </summary>
public static class ForestFlowers
{
    private const int StemSegments = 3;

    /// <summary>Heather (Calluna): a low mound about <paramref name="size"/> m across of stiff spikes, green below, pink-purple above.</summary>
    public static ArrayMesh Heather(float size = 0.9f, int seed = 1)
    {
        var b = new Builder();
        var random = new Random(seed);
        var clumps = 9;
        for (var c = 0; c < clumps; c++)
        {
            var centre = Disc(random, size * 0.4f);
            var spikes = 9 + random.Next(6);
            var height = 0.2f + 0.16f * (float)random.NextDouble();
            for (var i = 0; i < spikes; i++)
            {
                var angle = (float)random.NextDouble() * MathF.Tau;
                var outward = new Vector3(MathF.Cos(angle), 0f, MathF.Sin(angle));
                var root = centre + outward * 0.06f * (float)random.NextDouble();
                var h = height * (0.7f + 0.5f * (float)random.NextDouble());
                var lean = outward * (0.15f + 0.25f * (float)random.NextDouble()) + Vector3.UnitY;
                var shade = 0.8f + 0.35f * (float)random.NextDouble();
                var pink = new Vector4(0.6f, 0.42f, 0.54f, 1f) * shade;
                var stem = new Vector4(0.2f, 0.21f, 0.1f, 1f) * shade;
                // The flowering top 55 %: a wider strip in pink, the woody base a thin green one.
                b.Strip(root, Vector3.Normalize(lean), outward, h * 0.5f, 0.012f, 0.008f, stem, stem * 1.2f, 0f, 0.5f, (float)random.NextDouble());
                var top = root + Vector3.Normalize(lean) * h * 0.5f;
                b.Strip(top, Vector3.Normalize(lean + outward * 0.1f), outward, h * 0.55f, 0.035f, 0.012f, pink * 0.85f, pink, 0.5f, 1f,
                    (float)random.NextDouble());
            }
        }

        return b.Build("Heather patch");
    }

    /// <summary>Ox-eye daisies: <paramref name="count"/> white heads with yellow eyes on 20–35 cm stems over a patch of <paramref name="size"/> m.</summary>
    public static ArrayMesh Daisies(float size = 0.8f, int count = 11, int seed = 2) =>
        Heads("Daisy patch", size, count, seed, stemHeight: (0.2f, 0.35f), headRadius: 0.026f, petals: 12,
            petal: new Vector4(0.93f, 0.93f, 0.88f, 1f), eye: new Vector4(0.9f, 0.7f, 0.12f, 1f), cup: 0.1f);

    /// <summary>Buttercups: <paramref name="count"/> small glossy yellow cups on 30–50 cm stems over a patch of <paramref name="size"/> m.</summary>
    public static ArrayMesh Buttercups(float size = 0.9f, int count = 14, int seed = 3) =>
        Heads("Buttercup patch", size, count, seed, stemHeight: (0.3f, 0.5f), headRadius: 0.014f, petals: 5,
            petal: new Vector4(0.96f, 0.76f, 0.1f, 1f), eye: new Vector4(0.85f, 0.62f, 0.08f, 1f), cup: 0.45f);

    /// <summary>
    /// A wide meadow tuft: <paramref name="blades"/> curved blades spread over a disc of <paramref name="radius"/> m, heights
    /// from <paramref name="height"/>.Min to .Max, coloured from <paramref name="root"/> to <paramref name="tip"/> with a few
    /// dry, straw-coloured blades among them. Wider than <see cref="GrassMesh.Clump"/>'s, so the same blades cover more
    /// ground: the meadow reads as a continuous sward, not tufts on bare soil.
    /// </summary>
    public static ArrayMesh MeadowTuft(int blades, float radius, (float Min, float Max) height, Vector4 root, Vector4 tip, int seed)
    {
        var b = new Builder();
        var random = new Random(seed);
        var straw = new Vector4(0.62f, 0.56f, 0.34f, 1f);
        for (var i = 0; i < blades; i++)
        {
            var basePoint = Disc(random, radius);
            var angle = (float)random.NextDouble() * MathF.Tau;
            var forward = new Vector3(MathF.Cos(angle), 0f, MathF.Sin(angle));
            var h = height.Min + (height.Max - height.Min) * MathF.Pow((float)random.NextDouble(), 1.5f);
            var shade = 0.8f + 0.35f * (float)random.NextDouble();
            var bladeTip = (random.NextDouble() < 0.12 ? straw : tip) * shade;
            b.Blade(basePoint, forward, h, 0.028f + 0.02f * (float)random.NextDouble(), 0.25f + 0.5f * (float)random.NextDouble(),
                root * shade, bladeTip, (float)random.NextDouble());
        }

        return b.Build("Meadow tuft");
    }

    private static ArrayMesh Heads(string name, float size, int count, int seed, (float Min, float Max) stemHeight, float headRadius,
        int petals, Vector4 petal, Vector4 eye, float cup)
    {
        var b = new Builder();
        var random = new Random(seed);
        var stemColor = new Vector4(0.24f, 0.29f, 0.12f, 1f);
        for (var i = 0; i < count; i++)
        {
            var root = Disc(random, size * 0.5f);
            var h = stemHeight.Min + (stemHeight.Max - stemHeight.Min) * (float)random.NextDouble();
            var angle = (float)random.NextDouble() * MathF.Tau;
            var outward = new Vector3(MathF.Cos(angle), 0f, MathF.Sin(angle));
            var up = Vector3.Normalize(Vector3.UnitY + outward * 0.15f * (float)random.NextDouble());
            var phase = (float)random.NextDouble();
            b.Strip(root, up, outward, h, 0.006f, 0.004f, stemColor * 0.8f, stemColor, 0f, 1f, phase);
            // A few leaves at the foot.
            for (var l = 0; l < 2; l++)
            {
                var a = angle + 2.1f * (l + 1);
                var dir = new Vector3(MathF.Cos(a), 0f, MathF.Sin(a));
                b.Strip(root, Vector3.Normalize(dir * 0.8f + Vector3.UnitY * 0.6f), dir, 0.09f, 0.025f, 0.01f, stemColor * 0.8f,
                    stemColor * 1.1f, 0f, 0.3f, phase);
            }

            // The head: a fan facing up and a little outwards (towards the light), petals round a raised eye.
            var centre = root + up * h;
            var facing = Vector3.Normalize(Vector3.UnitY * 0.8f + outward * 0.45f);
            var shade = 0.88f + 0.22f * (float)random.NextDouble();
            b.Head(centre, facing, headRadius * (0.85f + 0.3f * (float)random.NextDouble()), petals, petal * shade, eye, cup, phase);
        }

        return b.Build(name);
    }

    private static Vector3 Disc(Random random, float radius)
    {
        var a = (float)random.NextDouble() * MathF.Tau;
        var r = radius * MathF.Sqrt((float)random.NextDouble());
        return new Vector3(MathF.Cos(a) * r, 0f, MathF.Sin(a) * r);
    }

    private sealed class Builder
    {
        private readonly List<Vector3> _positions = [];
        private readonly List<Vector3> _normals = [];
        private readonly List<Vector2> _uvs = [];
        private readonly List<Vector4> _colors = [];
        private readonly List<Vector4> _custom = [];
        private readonly List<int> _indices = [];

        private int Vertex(Vector3 p, Vector3 n, Vector2 uv, Vector4 color, Vector4 custom)
        {
            _positions.Add(p);
            _normals.Add(n);
            _uvs.Add(uv);
            _colors.Add(Vector4.Clamp(color with { W = 1f }, Vector4.Zero, Vector4.One));
            _custom.Add(custom);
            return _positions.Count - 1;
        }

        // A straight tapered strip from `root` along `dir`, facing across `side`; wind weight from w0 at the root to w1 at the top.
        public void Strip(Vector3 root, Vector3 dir, Vector3 side, float length, float widthRoot, float widthTop, Vector4 rootColor,
            Vector4 topColor, float w0, float w1, float phase)
        {
            // The strip's width runs across `dir`, roughly perpendicular to `side` (so it faces along `side`).
            var across = Vector3.Cross(Vector3.UnitY, side);
            across -= dir * Vector3.Dot(across, dir);
            side = across.LengthSquared() > 1e-8f ? Vector3.Normalize(across) : Vector3.UnitX;
            var face = Vector3.Cross(side, dir);
            if (face.Y < 0f)
                face = -face;
            var normal = Vector3.Normalize(face + Vector3.UnitY);
            var first = _positions.Count;
            for (var k = 0; k <= StemSegments; k++)
            {
                var t = (float)k / StemSegments;
                var p = root + dir * (length * t);
                var half = 0.5f * float.Lerp(widthRoot, widthTop, t);
                var color = Vector4.Lerp(rootColor, topColor, t);
                var wind = float.Lerp(w0, w1, t);
                var custom = new Vector4(wind * wind, 1f, phase, 0.45f + 0.55f * float.Lerp(w0, w1, t));
                Vertex(p - side * half, normal, new Vector2(0f, 1f - t), color, custom);
                Vertex(p + side * half, normal, new Vector2(1f, 1f - t), color, custom);
                if (k > 0)
                    Quad(first + 2 * k - 2, first + 2 * k - 1, first + 2 * k + 1, first + 2 * k, normal);
            }
        }

        // A curved, tapered grass blade rising `height` and arching `bend · height / 2` along `forward`, ending in a point.
        public void Blade(Vector3 basePoint, Vector3 forward, float height, float width, float bend, Vector4 rootColor, Vector4 tipColor, float phase)
        {
            const int segments = 4;
            var side = new Vector3(-forward.Z, 0f, forward.X);
            var first = _positions.Count;
            for (var k = 0; k <= segments; k++)
            {
                var t = (float)k / segments;
                var p = Point(t);
                var tangent = Point(MathF.Min(t + 0.05f, 1f)) - Point(MathF.Max(t - 0.05f, 0f));
                var face = Vector3.Cross(side, tangent);
                if (face.Y < 0f)
                    face = -face;
                var n = Vector3.Normalize(Vector3.Normalize(face) + Vector3.UnitY);
                var color = Vector4.Lerp(rootColor, tipColor, MathF.Sqrt(t));
                var custom = new Vector4(t, 1f, phase, 0.35f + 0.65f * MathF.Sqrt(t));
                if (k == segments)
                {
                    var tip = Vertex(p, n, new Vector2(0.5f, 0f), color, custom);
                    Triangle(first + 2 * k - 2, first + 2 * k - 1, tip, n);
                    break;
                }

                var half = 0.5f * width * MathF.Pow(1f - t, 0.7f);
                Vertex(p - side * half, n, new Vector2(0f, 1f - t), color, custom);
                Vertex(p + side * half, n, new Vector2(1f, 1f - t), color, custom);
                if (k > 0)
                    Quad(first + 2 * k - 2, first + 2 * k - 1, first + 2 * k + 1, first + 2 * k, n);
            }

            Vector3 Point(float t) => basePoint + forward * (0.5f * bend * height * t * t) + Vector3.UnitY * (height * t * (1f - 0.25f * bend * t * t));
        }

        // A flower head: a raised eye and `petals` petals round it, cupped by `cup` (0 flat … 1 a deep cup).
        public void Head(Vector3 centre, Vector3 facing, float radius, int petals, Vector4 petal, Vector4 eye, float cup, float phase)
        {
            var tangent = Vector3.Normalize(Vector3.Cross(facing, MathF.Abs(facing.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX));
            var bitangent = Vector3.Cross(facing, tangent);
            var custom = new Vector4(1f, 1f, phase, 1f);
            var middle = Vertex(centre + facing * radius * 0.25f, facing, new Vector2(0.5f, 0.5f), eye, custom);
            var ring = new int[petals * 2];
            for (var i = 0; i < petals * 2; i++)
            {
                // Alternate petal tips (out) and gaps (in), lifted by the cup.
                var a = i / (petals * 2f) * MathF.Tau;
                var r = (i & 1) == 0 ? radius : radius * 0.45f;
                var lift = cup * radius * (r / radius);
                var p = centre + (tangent * MathF.Cos(a) + bitangent * MathF.Sin(a)) * r + facing * lift;
                var c = (i & 1) == 0 ? petal : Vector4.Lerp(petal, eye, 0.35f);
                ring[i] = Vertex(p, facing, new Vector2(0.5f + 0.5f * MathF.Cos(a), 0.5f + 0.5f * MathF.Sin(a)), c, custom);
            }

            for (var i = 0; i < ring.Length; i++)
                Triangle(middle, ring[i], ring[(i + 1) % ring.Length], facing);
        }

        private void Quad(int a, int b, int c, int d, Vector3 normal)
        {
            Triangle(a, b, c, normal);
            Triangle(a, c, d, normal);
        }

        // Counter-clockwise around `normal`.
        private void Triangle(int a, int b, int c, Vector3 normal)
        {
            var face = Vector3.Cross(_positions[b] - _positions[a], _positions[c] - _positions[a]);
            if (Vector3.Dot(face, normal) < 0f)
                (b, c) = (c, b);
            _indices.AddRange([a, b, c]);
        }

        public ArrayMesh Build(string name)
        {
            var surface = new MeshSurface([.. _positions], [.. _normals], [.. _uvs], [.. _indices])
            {
                Colors = [.. _colors],
                Custom0 = [.. _custom],
            };
            var mesh = new ArrayMesh { ResourceName = name };
            mesh.AddSurface(surface);
            return mesh;
        }
    }
}

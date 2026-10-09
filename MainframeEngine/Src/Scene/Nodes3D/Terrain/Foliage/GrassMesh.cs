using System.Numerics;
using DrawingColor = System.Drawing.Color;

namespace MainframeEngine;

/// <summary>
/// Procedural ground-cover meshes for terrain foliage (ADR 0157): grass clumps, fern fans and pebbles. Everything is
/// geometry with vertex colours (no textures, no downloads): blades and leaflets are tapered strips, so they need no
/// alpha cut-out and do not shimmer.
/// </summary>
/// <remarks>
/// The plants carry the foliage streams that <see cref="FoliageMaterial3D"/> sways with: <see cref="MeshSurface.Custom0"/>
/// x = wind weight (0 at the root, 1 at the tip), y = 1 (the leaf flutter, growing towards UV v = 0 at the tip), z = a
/// per-blade phase, w = ambient occlusion (darker at the root); <see cref="MeshSurface.Colors"/> is a root-to-tip
/// gradient (sRGB) with per-blade variation. UVs run u across a blade (0 to 1) and v from the tip (0) to the root (1),
/// so a blade texture can be applied too. Normals lean towards up, so a clump shades evenly from every side: draw them
/// with <see cref="CreateMaterial"/> (<see cref="FoliageBackFace.Keep"/>, no cut-out). Deterministic for a seed.
/// </remarks>
public static class GrassMesh
{
    /// <summary>Default grass root colour (sRGB).</summary>
    public static readonly Vector4 GrassRoot = new(0.13f, 0.20f, 0.06f, 1f);

    /// <summary>Default grass tip colour (sRGB).</summary>
    public static readonly Vector4 GrassTip = new(0.52f, 0.60f, 0.27f, 1f);

    private const int BladeSegments = 4;

    /// <summary>
    /// A clump of <paramref name="blades"/> curved grass blades about <paramref name="height"/> metres tall and
    /// <paramref name="width"/> metres wide at the root, crossing at random yaws around a small base disc, each
    /// curving outward by <paramref name="bend"/> (0 straight up, 1 a strong arc of about half its height).
    /// </summary>
    public static ArrayMesh Clump(int blades = 14, float height = 0.45f, float width = 0.035f, float bend = 0.35f, int seed = 0,
        Vector4? rootColor = null, Vector4? tipColor = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(blades, 1);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        var root = rootColor ?? GrassRoot;
        var tip = tipColor ?? GrassTip;
        var b = new PlantBuilder(blades * (2 * BladeSegments + 1), blades * (2 * BladeSegments - 1) * 3);
        var state = FoliagePlacement.Mix(0xC1A3B5UL ^ (ulong)(uint)seed);
        var spread = height * 0.25f;
        for (var i = 0; i < blades; i++)
        {
            // Base on a small disc; the blade faces (and bends) roughly away from the centre, with some randomness.
            var angle = (i + Next(ref state) * 0.8f) / blades * MathF.Tau;
            var r = spread * MathF.Sqrt(Next(ref state));
            var outward = new Vector3(MathF.Cos(angle), 0f, MathF.Sin(angle));
            var basePoint = outward * r;
            var facing = angle + (Next(ref state) - 0.5f) * 1.6f;
            var forward = new Vector3(MathF.Cos(facing), 0f, MathF.Sin(facing));
            var h = height * (0.6f + 0.4f * Next(ref state));
            var w = width * (0.75f + 0.5f * Next(ref state));
            var curve = bend * (0.5f + Next(ref state));
            var phase = Next(ref state);
            var shade = 0.85f + 0.3f * Next(ref state);
            var yellow = Next(ref state) * 0.12f;
            var bladeRoot = root * shade;
            var bladeTip = (tip + new Vector4(yellow, yellow * 0.6f, 0f, 0f)) * shade;
            bladeRoot.W = bladeTip.W = 1f;
            Strip(ref b, basePoint, forward, h, w, curve, 1f, phase, bladeRoot, bladeTip, aoRoot: 0.35f);
        }

        return b.Build("Grass clump");
    }

    /// <summary>
    /// A fern: <paramref name="fronds"/> fronds about <paramref name="length"/> metres long fanning out from the centre,
    /// each arching up and drooping at the tip, with <paramref name="leaflets"/> pairs of leaflets up to
    /// <paramref name="width"/> metres long.
    /// </summary>
    public static ArrayMesh Fern(int fronds = 7, float length = 0.6f, float width = 0.14f, int leaflets = 9, int seed = 0,
        Vector4? rootColor = null, Vector4? tipColor = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(fronds, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(leaflets, 1);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        var root = rootColor ?? new Vector4(0.09f, 0.18f, 0.05f, 1f);
        var tip = tipColor ?? new Vector4(0.30f, 0.48f, 0.14f, 1f);
        var b = new PlantBuilder(fronds * (leaflets * 6 + 2 * (leaflets + 1)), fronds * (leaflets * 6 + leaflets * 6));
        var state = FoliagePlacement.Mix(0xFE41UL ^ (ulong)(uint)seed);
        for (var f = 0; f < fronds; f++)
        {
            var angle = (f + 0.3f * Next(ref state)) / fronds * MathF.Tau;
            var dir = new Vector3(MathF.Cos(angle), 0f, MathF.Sin(angle));
            var side = new Vector3(-dir.Z, 0f, dir.X);
            var l = length * (0.75f + 0.35f * Next(ref state));
            var rise = 0.9f + 0.35f * Next(ref state); // initial elevation, radians
            var droop = 0.55f + 0.3f * Next(ref state);
            var phase = Next(ref state);
            var shade = 0.85f + 0.3f * Next(ref state);
            var frondRoot = root * shade;
            var frondTip = tip * shade;
            frondRoot.W = frondTip.W = 1f;

            Vector3 Spine(float t) =>
                dir * (l * t * MathF.Cos(rise)) + Vector3.UnitY * (l * t * MathF.Sin(rise) - droop * l * t * t);

            // The stalk: a thin strip along the spine.
            var stalk = width * 0.08f;
            var steps = leaflets + 1;
            var first = b.Count;
            for (var k = 0; k <= steps; k++)
            {
                var t = (float)k / steps;
                var p = Spine(t);
                var n = PlantNormal(side, Spine(MathF.Min(t + 0.01f, 1f)) - Spine(MathF.Max(t - 0.01f, 0f)));
                var color = Vector4.Lerp(frondRoot, frondTip, t) * 0.8f;
                color.W = 1f;
                b.Vertex(p - side * stalk, n, new Vector2(0f, 1f - t), color, new Vector4(t, 1f, phase, 0.45f + 0.55f * t));
                b.Vertex(p + side * stalk, n, new Vector2(1f, 1f - t), color, new Vector4(t, 1f, phase, 0.45f + 0.55f * t));
                if (k > 0)
                    b.Quad(first + 2 * k - 2, first + 2 * k - 1, first + 2 * k + 1, first + 2 * k);
            }

            // Leaflets: a triangle each side per spine step, longest a third of the way out, angled forwards.
            for (var k = 0; k < leaflets; k++)
            {
                var t0 = (k + 0.15f) / steps;
                var t1 = (k + 1.05f) / steps;
                var tm = 0.5f * (t0 + t1);
                var a = Spine(t0);
                var c = Spine(t1);
                var tangent = Vector3.Normalize(c - a);
                var span = width * MathF.Sin(MathF.PI * MathF.Min(1f, 0.2f + 0.9f * tm)) * (1f - 0.35f * tm);
                var n = PlantNormal(side, tangent);
                var color = Vector4.Lerp(frondRoot, frondTip, tm);
                color.W = 1f;
                for (var s = -1; s <= 1; s += 2)
                {
                    var outer = Vector3.Lerp(a, c, 0.75f) + side * (s * span) + tangent * (span * 0.25f) - Vector3.UnitY * (span * 0.2f);
                    var i0 = b.Vertex(a, n, new Vector2(0.5f, 1f - t0), (color * 0.9f) with { W = 1f },
                        new Vector4(t0, 1f, phase, 0.5f + 0.5f * t0));
                    var i1 = b.Vertex(c, n, new Vector2(0.5f, 1f - t1), color, new Vector4(t1, 1f, phase, 0.5f + 0.5f * t1));
                    var i2 = b.Vertex(outer, n, new Vector2(s < 0 ? 0f : 1f, 1f - tm), (color * 1.08f) with { W = 1f },
                        new Vector4(MathF.Min(1f, tm + 0.1f), 1f, phase, 0.6f + 0.4f * tm));
                    b.Triangle(i0, i1, i2);
                }
            }
        }

        return b.Build("Fern");
    }

    /// <summary>
    /// A pebble or small rock: a flattened, noise-displaced icosphere of about <paramref name="radius"/> metres, its
    /// underside below the origin so it sits in the ground. Grey vertex colours; no wind data (draw it with a
    /// <see cref="StandardMaterial3D"/>).
    /// </summary>
    public static ArrayMesh Rock(float radius = 0.25f, float roughness = 0.25f, int seed = 0, Vector4? color = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(radius);
        var tint = color ?? new Vector4(0.42f, 0.40f, 0.37f, 1f);
        var (positions, indices) = Icosphere(2);
        var state = FoliagePlacement.Mix(0x20C4UL ^ (ulong)(uint)seed);
        // Three random lobes (low-frequency bumps) plus per-vertex roughness, then flatten and sink.
        Span<Vector3> lobes = stackalloc Vector3[3];
        Span<float> lobeSize = stackalloc float[3];
        for (var i = 0; i < 3; i++)
        {
            lobes[i] = Vector3.Normalize(new Vector3(Next(ref state) - 0.5f, Next(ref state) * 0.6f - 0.2f, Next(ref state) - 0.5f) + new Vector3(1e-4f));
            lobeSize[i] = 0.15f + 0.25f * Next(ref state);
        }

        var squash = 0.5f + 0.2f * Next(ref state);
        var colors = new Vector4[positions.Length];
        for (var v = 0; v < positions.Length; v++)
        {
            var p = positions[v];
            var r = 1f;
            for (var i = 0; i < 3; i++)
                r += lobeSize[i] * MathF.Max(0f, Vector3.Dot(p, lobes[i]));
            var h = FoliagePlacement.Mix((ulong)v * 0x9E37UL ^ (ulong)(uint)seed);
            r *= 1f + roughness * (((h >> 40) * (1f / 16777216f)) - 0.5f);
            positions[v] = new Vector3(p.X * r, p.Y * r * squash - 0.25f * squash, p.Z * r) * radius;
            var shade = 0.85f + 0.3f * ((h >> 16 & 0xFFFF) / 65535f);
            colors[v] = new Vector4(tint.X * shade, tint.Y * shade, tint.Z * shade, 1f);
        }

        var normals = MeshGeometry.ComputeSmoothNormals(positions, indices);
        var uvs = new Vector2[positions.Length];
        for (var v = 0; v < uvs.Length; v++)
            uvs[v] = new Vector2(0.5f + MathF.Atan2(normals[v].Z, normals[v].X) / MathF.Tau, 0.5f - 0.5f * normals[v].Y);
        var surface = new MeshSurface(positions, normals, uvs, indices) { Colors = colors };
        var mesh = new ArrayMesh { ResourceName = "Rock" };
        mesh.AddSurface(surface);
        return mesh;
    }

    /// <summary>
    /// The material for <see cref="Clump"/> and <see cref="Fern"/>: a <see cref="FoliageMaterial3D"/> with vertex-colour
    /// albedo (white), no cut-out, normals kept on both faces, some translucency, PBR shading and a gentle bend.
    /// </summary>
    public static FoliageMaterial3D CreateMaterial() => new()
    {
        ResourceName = "Grass",
        AlbedoColor = DrawingColor.White,
        AlphaCutout = false,
        BackFace = FoliageBackFace.Keep,
        Translucency = 0.45f,
        Roughness = 0.85f,
        ShadingMode = ShadingMode.Pbr,
        WindStrength = 0.6f,
        WindBranchBend = 0.5f,
    };

    // A tapered, curved strip from `basePoint` along `forward` (the bend direction): BladeSegments quads, the last one a
    // triangle to a single tip vertex.
    private static void Strip(ref PlantBuilder b, Vector3 basePoint, Vector3 forward, float height, float width, float bend,
        float flutter, float phase, Vector4 rootColor, Vector4 tipColor, float aoRoot)
    {
        var side = new Vector3(-forward.Z, 0f, forward.X);
        var first = b.Count;
        for (var k = 0; k <= BladeSegments; k++)
        {
            var t = (float)k / BladeSegments;
            var p = BladePoint(basePoint, forward, height, bend, t);
            var tangent = BladePoint(basePoint, forward, height, bend, MathF.Min(t + 0.05f, 1f)) -
                          BladePoint(basePoint, forward, height, bend, MathF.Max(t - 0.05f, 0f));
            var n = PlantNormal(side, tangent);
            var color = Vector4.Lerp(rootColor, tipColor, t);
            var custom = new Vector4(t, flutter, phase, aoRoot + (1f - aoRoot) * MathF.Sqrt(t));
            if (k == BladeSegments)
            {
                var tip = b.Vertex(p, n, new Vector2(0.5f, 0f), color, custom);
                b.Triangle(first + 2 * k - 2, first + 2 * k - 1, tip);
                break;
            }

            var half = 0.5f * width * MathF.Pow(1f - t, 0.7f);
            b.Vertex(p - side * half, n, new Vector2(0f, 1f - t), color, custom);
            b.Vertex(p + side * half, n, new Vector2(1f, 1f - t), color, custom);
            if (k > 0)
                b.Quad(first + 2 * k - 2, first + 2 * k - 1, first + 2 * k + 1, first + 2 * k);
        }
    }

    // A point of a blade rising `height` and curving `bend · height / 2` along `forward` (a quarter-circle-like arc).
    private static Vector3 BladePoint(Vector3 basePoint, Vector3 forward, float height, float bend, float t)
    {
        var reach = 0.5f * bend * height * t * t;
        var rise = height * t * (1f - 0.25f * bend * t * t);
        return basePoint + forward * reach + Vector3.UnitY * rise;
    }

    // The face normal of a strip (across `side`, along `tangent`) tilted halfway to up, so plants shade evenly.
    private static Vector3 PlantNormal(Vector3 side, Vector3 tangent)
    {
        var face = Vector3.Cross(side, tangent);
        if (face.LengthSquared() < 1e-12f)
            return Vector3.UnitY;
        face = Vector3.Normalize(face);
        if (face.Y < 0f)
            face = -face;
        return Vector3.Normalize(face + Vector3.UnitY);
    }

    private static float Next(ref ulong state) => FoliagePlacement.Next01(ref state);

    // A unit icosphere (12 vertices subdivided `levels` times, shared midpoints), counter-clockwise seen from outside.
    private static (Vector3[] Positions, int[] Indices) Icosphere(int levels)
    {
        var t = (1f + MathF.Sqrt(5f)) / 2f;
        var positions = new List<Vector3>
        {
            new(-1, t, 0), new(1, t, 0), new(-1, -t, 0), new(1, -t, 0),
            new(0, -1, t), new(0, 1, t), new(0, -1, -t), new(0, 1, -t),
            new(t, 0, -1), new(t, 0, 1), new(-t, 0, -1), new(-t, 0, 1),
        };
        for (var i = 0; i < positions.Count; i++)
            positions[i] = Vector3.Normalize(positions[i]);
        var indices = new List<int>
        {
            0, 11, 5, 0, 5, 1, 0, 1, 7, 0, 7, 10, 0, 10, 11, 1, 5, 9, 5, 11, 4, 11, 10, 2, 10, 7, 6, 7, 1, 8,
            3, 9, 4, 3, 4, 2, 3, 2, 6, 3, 6, 8, 3, 8, 9, 4, 9, 5, 2, 4, 11, 6, 2, 10, 8, 6, 7, 9, 8, 1,
        };
        for (var level = 0; level < levels; level++)
        {
            var midpoints = new Dictionary<long, int>();
            var next = new List<int>(indices.Count * 4);
            int Mid(int a, int b)
            {
                var key = ((long)Math.Min(a, b) << 32) | (uint)Math.Max(a, b);
                if (midpoints.TryGetValue(key, out var m))
                    return m;
                positions.Add(Vector3.Normalize(positions[a] + positions[b]));
                midpoints[key] = positions.Count - 1;
                return positions.Count - 1;
            }

            for (var k = 0; k < indices.Count; k += 3)
            {
                int a = indices[k], b = indices[k + 1], c = indices[k + 2];
                int ab = Mid(a, b), bc = Mid(b, c), ca = Mid(c, a);
                next.AddRange([a, ab, ca, b, bc, ab, c, ca, bc, ab, bc, ca]);
            }

            indices = next;
        }

        return (positions.ToArray(), indices.ToArray());
    }

    // Collects one surface's arrays; triangles are wound counter-clockwise around their vertex normals.
    private struct PlantBuilder(int vertices, int indices)
    {
        private readonly List<Vector3> _positions = new(vertices);
        private readonly List<Vector3> _normals = new(vertices);
        private readonly List<Vector2> _uvs = new(vertices);
        private readonly List<Vector4> _colors = new(vertices);
        private readonly List<Vector4> _custom = new(vertices);
        private readonly List<int> _indices = new(indices);

        public readonly int Count => _positions.Count;

        public readonly int Vertex(Vector3 position, Vector3 normal, Vector2 uv, Vector4 color, Vector4 custom)
        {
            _positions.Add(position);
            _normals.Add(normal);
            _uvs.Add(uv);
            _colors.Add(Vector4.Clamp(color, Vector4.Zero, Vector4.One));
            _custom.Add(custom);
            return _positions.Count - 1;
        }

        public readonly void Triangle(int a, int b, int c)
        {
            var face = Vector3.Cross(_positions[b] - _positions[a], _positions[c] - _positions[a]);
            if (Vector3.Dot(face, _normals[a] + _normals[b] + _normals[c]) < 0f)
                (b, c) = (c, b);
            _indices.Add(a);
            _indices.Add(b);
            _indices.Add(c);
        }

        // a-b is the lower edge, d-c the upper one (a below d, b below c).
        public readonly void Quad(int a, int b, int c, int d)
        {
            Triangle(a, b, c);
            Triangle(a, c, d);
        }

        public readonly ArrayMesh Build(string name)
        {
            var surface = new MeshSurface(_positions.ToArray(), _normals.ToArray(), _uvs.ToArray(), _indices.ToArray())
            {
                Colors = _colors.ToArray(),
                Custom0 = _custom.ToArray(),
            };
            var mesh = new ArrayMesh { ResourceName = name };
            mesh.AddSurface(surface);
            return mesh;
        }
    }
}

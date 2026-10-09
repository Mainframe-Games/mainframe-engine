using System.Numerics;

namespace MainframeEngine.Trees;

/// <summary>A surface's look in a CPU bake: its texture × a linear tint, the alpha test, and whether back faces draw.</summary>
/// <param name="Albedo">Null: the tint alone.</param>
/// <param name="Tint">Linear RGB multiplier.</param>
/// <param name="Cutoff">Alpha-test cutoff (0 = opaque).</param>
/// <param name="TwoSided">Back faces draw with the flipped normal (leaf cards); otherwise they are culled (bark).</param>
/// <param name="TrianglesPerGroup">Triangles of one card (2 for a quad): a sample counts a card once in its layer count.</param>
/// <param name="Mask">A per-material value kept with the nearest surface (impostors: 1 on leaves, 0 on bark).</param>
/// <param name="CountLayers">Count the cards every sample passes through (thickness); off, hidden fragments are skipped early.</param>
/// <param name="Detail">A thickness (R) and occlusion (G) map sampled with the albedo and kept with the nearest surface.</param>
internal readonly record struct BakeMaterial(BakeTexture? Albedo, Vector3 Tint, float Cutoff, bool TwoSided, int TrianglesPerGroup = 2,
    float Mask = 0f, bool CountLayers = true, BakeTexture? Detail = null);

/// <summary>One pixel of a resolved bake: what the camera saw through it.</summary>
/// <param name="Coverage">Share of the pixel's samples covered (0..1).</param>
/// <param name="Albedo">Linear RGB averaged over the covered samples.</param>
/// <param name="Normal">Unit normal in view space (x right, y up, z towards the camera), averaged over the covered samples.</param>
/// <param name="Depth">Depth of the nearest surface (view units, larger is farther), averaged over the covered samples.</param>
/// <param name="Layers">Cards the pixel's covered samples pass through, averaged (1 = one leaf: thin).</param>
/// <param name="Occlusion">The nearest surface's interpolated vertex occlusion (1 without one), averaged.</param>
/// <param name="Mask">The nearest surface's <see cref="BakeMaterial.Mask"/>, averaged.</param>
/// <param name="Thickness">The nearest surface's <see cref="BakeMaterial.Detail"/> thickness (0 without one), averaged.</param>
internal readonly record struct BakePixel(float Coverage, Vector3 Albedo, Vector3 Normal, float Depth, float Layers, float Occlusion = 1f,
    float Mask = 0f, float Thickness = 0f);

/// <summary>
/// The CPU rasterizer of the tree bakes (ADR 0172: cluster atlases and impostors): an orthographic, supersampled depth
/// buffer that keeps the nearest surface's linear albedo and view-space normal per sample, alpha-tests textured cards and
/// counts every card a sample passes through (the layer count behind the thickness map). Deterministic: one target is
/// drawn by one thread in submission order, with plain float math. Not thread-safe; use one per thread.
/// </summary>
internal sealed class BakeRasterizer
{
    private readonly float[] _depth;
    private readonly float[] _albedo;
    private readonly float[] _normal;
    private readonly ushort[] _layers;
    private readonly int[] _lastGroup;
    private readonly float[] _occlusion;
    private readonly float[] _mask;
    private readonly float[] _thickness;
    private int _groupBase;

    /// <param name="width">Resolved pixels across.</param>
    /// <param name="height">Resolved pixels down.</param>
    /// <param name="supersample">Samples per pixel along each axis.</param>
    public BakeRasterizer(int width, int height, int supersample)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(supersample);
        Width = width;
        Height = height;
        Supersample = supersample;
        SampleWidth = width * supersample;
        SampleHeight = height * supersample;
        var samples = SampleWidth * SampleHeight;
        _depth = new float[samples];
        _albedo = new float[samples * 3];
        _normal = new float[samples * 3];
        _layers = new ushort[samples];
        _lastGroup = new int[samples];
        _occlusion = new float[samples];
        _mask = new float[samples];
        _thickness = new float[samples];
        Clear();
    }

    public int Width { get; }

    public int Height { get; }

    public int Supersample { get; }

    /// <summary>Samples across: draw positions are in sample units (x right, y down from the top-left corner).</summary>
    public int SampleWidth { get; }

    public int SampleHeight { get; }

    public void Clear()
    {
        Array.Fill(_depth, float.PositiveInfinity);
        Array.Clear(_albedo);
        Array.Clear(_normal);
        Array.Clear(_layers);
        Array.Fill(_lastGroup, -1);
        Array.Clear(_occlusion);
        Array.Clear(_mask);
        Array.Clear(_thickness);
        _groupBase = 0;
    }

    /// <summary>
    /// Draws an indexed triangle list. <paramref name="toSamples"/> maps object space to sample space (x right and y down
    /// in samples, z = depth, larger farther; System.Numerics row vectors); normals follow its rotation (it may scale
    /// uniformly and mirror y). Counter-clockwise triangles (in object space) face their normals. <paramref name="occlusion"/>
    /// (optional, per vertex) is interpolated and kept with the nearest surface.
    /// </summary>
    public void Draw(ReadOnlySpan<Vector3> positions, ReadOnlySpan<Vector3> normals, ReadOnlySpan<Vector2> uvs, ReadOnlySpan<int> indices,
        in Matrix4x4 toSamples, in BakeMaterial material, ReadOnlySpan<float> occlusion = default)
    {
        var groups = Math.Max(1, material.TrianglesPerGroup);
        var triangles = indices.Length / 3;
        for (var t = 0; t < triangles; t++)
        {
            int i0 = indices[t * 3], i1 = indices[t * 3 + 1], i2 = indices[t * 3 + 2];
            var faceObject = Vector3.Cross(positions[i1] - positions[i0], positions[i2] - positions[i0]);
            var face = Vector3.TransformNormal(faceObject, toSamples);
            var front = face.Z < 0f; // facing the camera (which looks along +z)
            if (!front && !material.TwoSided)
                continue;
            var flip = front ? 1f : -1f;
            DrawTriangle(
                Vector3.Transform(positions[i0], toSamples), Vector3.Transform(positions[i1], toSamples), Vector3.Transform(positions[i2], toSamples),
                Normal(normals, i0, faceObject, toSamples) * flip, Normal(normals, i1, faceObject, toSamples) * flip, Normal(normals, i2, faceObject, toSamples) * flip,
                uvs.Length > 0 ? uvs[i0] : default, uvs.Length > 0 ? uvs[i1] : default, uvs.Length > 0 ? uvs[i2] : default,
                occlusion.Length > 0 ? new Vector3(occlusion[i0], occlusion[i1], occlusion[i2]) : Vector3.One,
                material, _groupBase + t / groups);
        }

        _groupBase += (triangles + groups - 1) / groups;
    }

    private static Vector3 Normal(ReadOnlySpan<Vector3> normals, int i, Vector3 face, in Matrix4x4 toSamples)
    {
        var n = Vector3.TransformNormal(normals.Length > i ? normals[i] : face, toSamples);
        var length = n.Length();
        return length > 1e-20f ? n / length : Vector3.UnitZ;
    }

    private void DrawTriangle(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 n0, Vector3 n1, Vector3 n2, Vector2 uv0, Vector2 uv1, Vector2 uv2,
        Vector3 occlusion, in BakeMaterial material, int group)
    {
        var area = Edge(p0, p1, p2.X, p2.Y);
        if (MathF.Abs(area) < 1e-12f)
            return;
        var minX = Math.Max(0, (int)MathF.Floor(MathF.Min(p0.X, MathF.Min(p1.X, p2.X))));
        var maxX = Math.Min(SampleWidth - 1, (int)MathF.Ceiling(MathF.Max(p0.X, MathF.Max(p1.X, p2.X))));
        var minY = Math.Max(0, (int)MathF.Floor(MathF.Min(p0.Y, MathF.Min(p1.Y, p2.Y))));
        var maxY = Math.Min(SampleHeight - 1, (int)MathF.Ceiling(MathF.Max(p0.Y, MathF.Max(p1.Y, p2.Y))));
        if (minX > maxX || minY > maxY)
            return;

        // Texture level of detail: texels per sample over the triangle.
        var texture = material.Albedo;
        var lod = 0f;
        if (texture is not null)
        {
            var uvArea = MathF.Abs((uv1.X - uv0.X) * (uv2.Y - uv0.Y) - (uv2.X - uv0.X) * (uv1.Y - uv0.Y)) * texture.Width * texture.Height;
            lod = 0.5f * MathF.Log2(MathF.Max(uvArea / MathF.Abs(area), 1e-8f));
        }

        var inverse = 1f / area;
        var detail = material.Detail;
        var level = texture?.Level(lod) ?? 0;
        var detailLevel = detail?.Level(lod + 0.5f * MathF.Log2(MathF.Max((float)detail.Width * detail.Height / (texture?.Width * texture?.Height ?? 1), 1e-8f))) ?? 0;
        var countLayers = material.CountLayers;
        // Barycentric weights are affine in x and y: step them instead of recomputing the edge functions.
        var px0 = minX + 0.5f;
        var dw0dx = -(p2.Y - p1.Y) * inverse;
        var dw1dx = -(p0.Y - p2.Y) * inverse;
        for (var y = minY; y <= maxY; y++)
        {
            var py = y + 0.5f;
            var w0 = Edge(p1, p2, px0, py) * inverse;
            var w1 = Edge(p2, p0, px0, py) * inverse;
            var row = y * SampleWidth;
            for (var x = minX; x <= maxX; x++, w0 += dw0dx, w1 += dw1dx)
            {
                var w2 = 1f - w0 - w1;
                if (w0 < 0f || w1 < 0f || w2 < 0f)
                    continue;

                var s = row + x;
                var z = p0.Z * w0 + p1.Z * w1 + p2.Z * w2;
                if (!countLayers && z >= _depth[s])
                    continue; // hidden, and nothing to count

                float r = material.Tint.X, g = material.Tint.Y, b = material.Tint.Z;
                float u = 0f, v = 0f;
                if (texture is not null)
                {
                    u = uv0.X * w0 + uv1.X * w1 + uv2.X * w2;
                    v = uv0.Y * w0 + uv1.Y * w1 + uv2.Y * w2;
                    var tap = texture.Tap(u, v, level);
                    var alpha = BakeTexture.Color(tap, 3);
                    if (alpha < material.Cutoff || (material.Cutoff > 0f && alpha <= 0f))
                        continue;
                    if (countLayers && _lastGroup[s] != group)
                    {
                        _lastGroup[s] = group;
                        if (_layers[s] < ushort.MaxValue)
                            _layers[s]++;
                    }

                    if (z >= _depth[s])
                        continue;
                    r *= BakeTexture.Color(tap, 0);
                    g *= BakeTexture.Color(tap, 1);
                    b *= BakeTexture.Color(tap, 2);
                }
                else
                {
                    if (countLayers && _lastGroup[s] != group)
                    {
                        _lastGroup[s] = group;
                        if (_layers[s] < ushort.MaxValue)
                            _layers[s]++;
                    }

                    if (z >= _depth[s])
                        continue;
                }

                _depth[s] = z;
                _albedo[s * 3] = r;
                _albedo[s * 3 + 1] = g;
                _albedo[s * 3 + 2] = b;
                var n = n0 * w0 + n1 * w1 + n2 * w2;
                _normal[s * 3] = n.X;
                _normal[s * 3 + 1] = n.Y;
                _normal[s * 3 + 2] = n.Z;
                var occluded = occlusion.X * w0 + occlusion.Y * w1 + occlusion.Z * w2;
                var thickness = 0f;
                if (detail is not null)
                {
                    var tap = detail.Tap(u, v, detailLevel);
                    thickness = BakeTexture.Color(tap, 0);
                    occluded *= BakeTexture.Color(tap, 1);
                }

                _occlusion[s] = occluded;
                _thickness[s] = thickness;
                _mask[s] = material.Mask;
            }
        }
    }

    private static float Edge(Vector3 a, Vector3 b, float x, float y) => (b.X - a.X) * (y - a.Y) - (b.Y - a.Y) * (x - a.X);

    /// <summary>
    /// The resolved image (row-major, <see cref="Width"/> × <see cref="Height"/>): each pixel averages its samples. Normals
    /// are in view space: x right, y up, z towards the camera.
    /// </summary>
    public BakePixel[] Resolve()
    {
        var pixels = new BakePixel[Width * Height];
        var ss = Supersample;
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var covered = 0;
                var albedo = Vector3.Zero;
                var normal = Vector3.Zero;
                float depth = 0f, layers = 0f, occlusion = 0f, mask = 0f, thickness = 0f;
                for (var sy = 0; sy < ss; sy++)
                {
                    for (var sx = 0; sx < ss; sx++)
                    {
                        var s = (y * ss + sy) * SampleWidth + x * ss + sx;
                        if (float.IsPositiveInfinity(_depth[s]))
                            continue;
                        covered++;
                        albedo += new Vector3(_albedo[s * 3], _albedo[s * 3 + 1], _albedo[s * 3 + 2]);
                        // Sample space has y down and z away from the camera: view space flips both.
                        normal += new Vector3(_normal[s * 3], -_normal[s * 3 + 1], -_normal[s * 3 + 2]);
                        depth += _depth[s];
                        layers += _layers[s];
                        occlusion += _occlusion[s];
                        mask += _mask[s];
                        thickness += _thickness[s];
                    }
                }

                if (covered == 0)
                    continue;
                var length = normal.Length();
                pixels[y * Width + x] = new BakePixel((float)covered / (ss * ss), albedo / covered,
                    length > 1e-12f ? normal / length : Vector3.UnitZ, depth / covered, layers / covered, occlusion / covered, mask / covered,
                    thickness / covered);
            }
        }

        return pixels;
    }
}

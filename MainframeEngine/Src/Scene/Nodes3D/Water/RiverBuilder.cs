using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// A river ribbon's vertex streams, as <see cref="River3D"/> generates them. Every array has one entry per vertex
/// (<see cref="Indices"/>: three per triangle, counter-clockwise seen from above).
/// </summary>
/// <param name="Positions">River-local positions at the (downhill-clamped) surface height.</param>
/// <param name="Normals">Up.</param>
/// <param name="UVs">u = 0 … 1 across (left bank to right bank, facing downstream); v = arc length / <c>UvLength</c>.</param>
/// <param name="Custom0">
/// x = water column depth (depth × (1 − x²), x = −1 … 1 across), yz = surface flow in m/s along local X and Z (slower at
/// the banks), w = foam (rapids above 2 m/s). Meant for <c>MeshSurface.Custom0</c> once that stream exists.
/// </param>
/// <param name="Indices">Triangle list.</param>
public readonly record struct RiverMeshData(Vector3[] Positions, Vector3[] Normals, Vector2[] UVs, Vector4[] Custom0, int[] Indices)
{
    public int VertexCount => Positions?.Length ?? 0;

    public int IndexCount => Indices?.Length ?? 0;
}

/// <summary>Shape and flow settings for <see cref="RiverBuilder"/> (River3D's exports).</summary>
internal struct RiverSettings
{
    public float SectionLength;
    public int CrossSegments;
    public float UvLength;
    public bool EnforceDownhill;
    public float MinSpeed;
    public float MaxSpeed;
    public float SpeedPerSqrtSlope;
    public float SlopeWindow;

    public static RiverSettings Default => new()
    {
        SectionLength = 1f,
        CrossSegments = 4,
        UvLength = 4f,
        EnforceDownhill = true,
        MinSpeed = 0.3f,
        MaxSpeed = 4f,
        SpeedPerSqrtSlope = 6f,
        SlopeWindow = 6f,
    };
}

/// <summary>
/// Generates a <see cref="River3D"/>'s ribbon and its query data from a <see cref="Curve3D"/> (plain C#, no GPU):
/// cross-sections every <c>SectionLength</c> of arc length at the downhill-clamped surface height, flow speed from the
/// slope (<c>MinSpeed + SpeedPerSqrtSlope × √slope</c>, Manning-like), and a uniform grid of section segments for
/// <see cref="TrySample"/>. Arrays are reused while the section count stays the same.
/// </summary>
internal sealed class RiverBuilder
{
    /// <summary>Grid cell size in metres (river-local XZ).</summary>
    public const float CellSize = 4f;

    /// <summary>Speed above which foam ramps in (rapids), m/s.</summary>
    public const float RapidsSpeed = 2f;

    private float[] _clampedHeights = [];
    private int _bakedCount;
    private float _bakedLength;

    // Per section (k = 0 .. SectionCount - 1).
    private Vector3[] _centres = [];
    private Vector2[] _tangents = [];   // XZ, unit, downstream
    private float[] _offsets = [];
    private float[] _halfWidths = [];
    private float[] _depths = [];
    private float[] _speeds = [];

    // Grid over section segments (k, k + 1): CSR layout.
    private int[] _cellStarts = [];
    private int[] _cellItems = [];
    private int _cellsX, _cellsZ;
    private float _gridMinX, _gridMinZ;

    private Vector3[] _positions = [];
    private Vector3[] _normals = [];
    private Vector2[] _uvs = [];
    private Vector4[] _custom0 = [];
    private int[] _indices = [];

    public int SectionCount { get; private set; }

    /// <summary>Arc length of the river in metres.</summary>
    public float Length => _bakedLength;

    /// <summary>River-local bounds of the water (surface down to the deepest column).</summary>
    public Aabb Bounds { get; private set; } = Aabb.Empty;

    public RiverMeshData Mesh { get; private set; } = new([], [], [], [], []);

    public ReadOnlySpan<Vector3> SectionCentres => _centres.AsSpan(0, SectionCount);

    public ReadOnlySpan<float> SectionOffsets => _offsets.AsSpan(0, SectionCount);

    public ReadOnlySpan<float> SectionSpeeds => _speeds.AsSpan(0, SectionCount);

    public ReadOnlySpan<Vector2> SectionTangents => _tangents.AsSpan(0, SectionCount);

    public ReadOnlySpan<float> SectionHalfWidths => _halfWidths.AsSpan(0, SectionCount);

    public ReadOnlySpan<float> SectionDepths => _depths.AsSpan(0, SectionCount);

    /// <summary>Builds everything from <paramref name="curve"/> (null or fewer than two points: an empty river).</summary>
    public void Build(Curve3D? curve, in RiverSettings settings)
    {
        SectionCount = 0;
        Bounds = Aabb.Empty;
        _bakedCount = 0;
        _bakedLength = 0;
        if (curve is null || curve.PointCount < 2 || !(curve.GetBakedLength() > 0))
        {
            Mesh = new RiverMeshData([], [], [], [], []);
            BuildGrid();
            return;
        }

        ClampHeights(curve, settings.EnforceDownhill);
        BuildSections(curve, settings);
        BuildRibbon(settings);
        BuildGrid();
    }

    // 1. The baked surface heights, clamped so they never rise downstream (the curve itself is unchanged).
    private void ClampHeights(Curve3D curve, bool enforceDownhill)
    {
        var baked = curve.GetBakedPoints();
        _bakedCount = baked.Length;
        _bakedLength = curve.GetBakedLength();
        if (_clampedHeights.Length < _bakedCount)
            _clampedHeights = new float[_bakedCount];
        var previous = float.PositiveInfinity;
        for (var i = 0; i < _bakedCount; i++)
        {
            var y = baked[i].Y;
            if (enforceDownhill && y > previous)
                y = previous;
            _clampedHeights[i] = y;
            previous = y;
        }
    }

    /// <summary>The clamped surface height <paramref name="offset"/> metres along the river.</summary>
    public float SurfaceHeightAt(float offset)
    {
        if (_bakedCount == 0)
            return 0f;
        if (_bakedCount == 1)
            return _clampedHeights[0];
        var step = _bakedLength / (_bakedCount - 1);
        var o = Math.Clamp(offset, 0f, _bakedLength);
        var index = Math.Min((int)(o / step), _bakedCount - 2);
        var t = Math.Clamp((o - step * index) / step, 0f, 1f);
        return _clampedHeights[index] + (_clampedHeights[index + 1] - _clampedHeights[index]) * t;
    }

    /// <summary>Flow speed at <paramref name="offset"/> from the clamped slope over <c>SlopeWindow</c>.</summary>
    public float SpeedAt(float offset, in RiverSettings settings)
    {
        var half = MathF.Max(settings.SlopeWindow, 1e-3f) * 0.5f;
        var a = Math.Clamp(offset - half, 0f, _bakedLength);
        var b = Math.Clamp(offset + half, 0f, _bakedLength);
        var slope = b > a ? (SurfaceHeightAt(a) - SurfaceHeightAt(b)) / (b - a) : 0f;
        var speed = settings.MinSpeed + settings.SpeedPerSqrtSlope * MathF.Sqrt(MathF.Max(slope, 0f));
        return Math.Clamp(speed, settings.MinSpeed, MathF.Max(settings.MinSpeed, settings.MaxSpeed));
    }

    // 2. Cross-sections every SectionLength of arc length.
    private void BuildSections(Curve3D curve, in RiverSettings settings)
    {
        var length = _bakedLength;
        var steps = Math.Max(1, (int)MathF.Ceiling(length / MathF.Max(settings.SectionLength, 0.01f)));
        var count = steps + 1;
        if (_centres.Length != count)
        {
            _centres = new Vector3[count];
            _tangents = new Vector2[count];
            _offsets = new float[count];
            _halfWidths = new float[count];
            _depths = new float[count];
            _speeds = new float[count];
        }

        var spacing = length / steps;
        var h = MathF.Min(spacing * 0.5f, 0.5f);
        var tangent = new Vector2(0, -1);
        for (var k = 0; k < count; k++)
        {
            var s = k == steps ? length : spacing * k;
            var p = curve.SampleBaked(s);
            var ahead = curve.SampleBaked(Math.Min(s + h, length));
            var behind = curve.SampleBaked(Math.Max(s - h, 0f));
            var d = new Vector2(ahead.X - behind.X, ahead.Z - behind.Z);
            var dl = d.Length();
            if (dl > 1e-5f)
                tangent = d / dl; // else (a vertical run): keep the previous one
            _centres[k] = new Vector3(p.X, SurfaceHeightAt(s), p.Z);
            _tangents[k] = tangent;
            _offsets[k] = s;
            _halfWidths[k] = MathF.Max(curve.SampleBakedWidth(s), 0f) * 0.5f;
            _depths[k] = MathF.Max(curve.SampleBakedDepth(s), 0f);
            _speeds[k] = SpeedAt(s, settings);
        }

        SectionCount = count;
    }

    // 3. The ribbon: CrossSegments + 1 vertices per section at x = −1 … 1 across, two triangles per quad.
    private void BuildRibbon(in RiverSettings settings)
    {
        var columns = Math.Max(1, settings.CrossSegments) + 1;
        var vertexCount = SectionCount * columns;
        var indexCount = (SectionCount - 1) * (columns - 1) * 6;
        if (_positions.Length != vertexCount)
        {
            _positions = new Vector3[vertexCount];
            _normals = new Vector3[vertexCount];
            _uvs = new Vector2[vertexCount];
            _custom0 = new Vector4[vertexCount];
        }

        if (_indices.Length != indexCount)
            _indices = new int[indexCount];

        var uvLength = MathF.Max(settings.UvLength, 1e-3f);
        var bounds = Aabb.Empty;
        for (var k = 0; k < SectionCount; k++)
        {
            var centre = _centres[k];
            var right = Right(_tangents[k]);
            var hw = _halfWidths[k];
            var speed = _speeds[k];
            var foam = 0.6f * Math.Clamp((speed - RapidsSpeed) / 2f, 0f, 1f);
            for (var j = 0; j < columns; j++)
            {
                var u = (float)j / (columns - 1);
                var x = u * 2f - 1f;
                var v = k * columns + j;
                var position = centre + right * (x * hw);
                _positions[v] = position;
                _normals[v] = Vector3.UnitY;
                _uvs[v] = new Vector2(u, _offsets[k] / uvLength);
                var across = 1f - x * x;
                var flow = _tangents[k] * (speed * (1f - 0.6f * x * x));
                _custom0[v] = new Vector4(_depths[k] * across, flow.X, flow.Y, foam);
                bounds = bounds.Encapsulate(position);
                bounds = bounds.Encapsulate(position - new Vector3(0, _depths[k], 0));
            }
        }

        var i = 0;
        for (var k = 0; k < SectionCount - 1; k++)
        {
            for (var j = 0; j < columns - 1; j++)
            {
                var a = k * columns + j;
                var b = a + 1;
                var c = a + columns;
                var d = c + 1;
                // Counter-clockwise seen from above: (a, b, c) and (b, d, c), with b to the right of a and c downstream.
                _indices[i++] = a;
                _indices[i++] = b;
                _indices[i++] = c;
                _indices[i++] = b;
                _indices[i++] = d;
                _indices[i++] = c;
            }
        }

        Bounds = bounds;
        Mesh = new RiverMeshData(_positions, _normals, _uvs, _custom0, _indices);
    }

    /// <summary>The horizontal right vector (facing downstream) of an XZ tangent.</summary>
    private static Vector3 Right(Vector2 tangent) => new(-tangent.Y, 0, tangent.X);

    // 4. A uniform grid mapping cells to the section segments whose quads overlap them.
    private void BuildGrid()
    {
        var segments = Math.Max(0, SectionCount - 1);
        if (segments == 0)
        {
            _cellsX = _cellsZ = 0;
            return;
        }

        var bounds = Bounds;
        _gridMinX = bounds.Min.X;
        _gridMinZ = bounds.Min.Z;
        _cellsX = Math.Max(1, (int)MathF.Ceiling((bounds.Max.X - bounds.Min.X) / CellSize) + 1);
        _cellsZ = Math.Max(1, (int)MathF.Ceiling((bounds.Max.Z - bounds.Min.Z) / CellSize) + 1);
        var cells = _cellsX * _cellsZ;
        if (_cellStarts.Length != cells + 1)
            _cellStarts = new int[cells + 1];
        else
            Array.Clear(_cellStarts);

        // Pass 1: count per cell (into _cellStarts[cell + 1]); pass 2: prefix sums; pass 3: fill.
        for (var k = 0; k < segments; k++)
        {
            SegmentCells(k, out var x0, out var z0, out var x1, out var z1);
            for (var z = z0; z <= z1; z++)
                for (var x = x0; x <= x1; x++)
                    _cellStarts[z * _cellsX + x + 1]++;
        }

        for (var c = 0; c < cells; c++)
            _cellStarts[c + 1] += _cellStarts[c];
        var total = _cellStarts[cells];
        if (_cellItems.Length < total)
            _cellItems = new int[total];
        Span<int> fill = cells <= 4096 ? stackalloc int[cells] : new int[cells];
        _cellStarts.AsSpan(0, cells).CopyTo(fill);
        for (var k = 0; k < segments; k++)
        {
            SegmentCells(k, out var x0, out var z0, out var x1, out var z1);
            for (var z = z0; z <= z1; z++)
                for (var x = x0; x <= x1; x++)
                    _cellItems[fill[z * _cellsX + x]++] = k;
        }
    }

    private void SegmentCells(int k, out int x0, out int z0, out int x1, out int z1)
    {
        var minX = float.PositiveInfinity;
        var minZ = float.PositiveInfinity;
        var maxX = float.NegativeInfinity;
        var maxZ = float.NegativeInfinity;
        for (var e = k; e <= k + 1; e++)
        {
            var right = Right(_tangents[e]) * _halfWidths[e];
            var c = _centres[e];
            minX = MathF.Min(minX, MathF.Min(c.X - right.X, c.X + right.X));
            maxX = MathF.Max(maxX, MathF.Max(c.X - right.X, c.X + right.X));
            minZ = MathF.Min(minZ, MathF.Min(c.Z - right.Z, c.Z + right.Z));
            maxZ = MathF.Max(maxZ, MathF.Max(c.Z - right.Z, c.Z + right.Z));
        }

        x0 = CellX(minX);
        x1 = CellX(maxX);
        z0 = CellZ(minZ);
        z1 = CellZ(maxZ);
    }

    private int CellX(float x) => Math.Clamp((int)MathF.Floor((x - _gridMinX) / CellSize), 0, _cellsX - 1);

    private int CellZ(float z) => Math.Clamp((int)MathF.Floor((z - _gridMinZ) / CellSize), 0, _cellsZ - 1);

    /// <summary>
    /// Samples the river at a river-local point: inside when it lies between two cross-section lines and within the
    /// interpolated half width of the centreline. Flow and column follow the ribbon's lateral profile.
    /// </summary>
    public bool TrySample(Vector3 local, out WaterSample sample, out float offset)
    {
        sample = default;
        offset = 0;
        if (SectionCount < 2 || local.X < Bounds.Min.X - CellSize || local.X > Bounds.Max.X + CellSize ||
            local.Z < Bounds.Min.Z - CellSize || local.Z > Bounds.Max.Z + CellSize)
            return false;
        var cx = (int)MathF.Floor((local.X - _gridMinX) / CellSize);
        var cz = (int)MathF.Floor((local.Z - _gridMinZ) / CellSize);
        if ((uint)cx >= (uint)_cellsX || (uint)cz >= (uint)_cellsZ)
            return false;

        var cell = cz * _cellsX + cx;
        var best = float.PositiveInfinity;
        var p = new Vector2(local.X, local.Z);
        for (var i = _cellStarts[cell]; i < _cellStarts[cell + 1]; i++)
        {
            var k = _cellItems[i];
            var c0 = new Vector2(_centres[k].X, _centres[k].Z);
            var c1 = new Vector2(_centres[k + 1].X, _centres[k + 1].Z);
            // Between the section lines: in front of section k, behind section k + 1.
            var a = Vector2.Dot(p - c0, _tangents[k]);
            var b = -Vector2.Dot(p - c1, _tangents[k + 1]);
            if (a < -1e-4f || b < -1e-4f)
                continue;
            var t = a + b > 0 ? Math.Clamp(a / (a + b), 0f, 1f) : 0f;
            var centre = c0 + (c1 - c0) * t;
            var tangent = _tangents[k] + (_tangents[k + 1] - _tangents[k]) * t;
            var tl = tangent.Length();
            tangent = tl > 1e-6f ? tangent / tl : _tangents[k];
            var hw = _halfWidths[k] + (_halfWidths[k + 1] - _halfWidths[k]) * t;
            var lateral = Vector2.Dot(p - centre, new Vector2(-tangent.Y, tangent.X));
            if (!(hw > 0) || MathF.Abs(lateral) > hw)
                continue;
            var x = lateral / hw;
            var score = MathF.Abs(x);
            if (score >= best)
                continue;
            best = score;
            var across = 1f - x * x;
            var depth = _depths[k] + (_depths[k + 1] - _depths[k]) * t;
            var speed = _speeds[k] + (_speeds[k + 1] - _speeds[k]) * t;
            var flow = tangent * (speed * (1f - 0.6f * x * x));
            var surface = _centres[k].Y + (_centres[k + 1].Y - _centres[k].Y) * t;
            sample = new WaterSample(surface, depth * across, new Vector3(flow.X, 0, flow.Y));
            offset = _offsets[k] + (_offsets[k + 1] - _offsets[k]) * t;
        }

        return best <= 1f;
    }
}

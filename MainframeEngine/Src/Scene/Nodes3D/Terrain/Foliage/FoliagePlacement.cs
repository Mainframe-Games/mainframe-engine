using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// The instances of one foliage tile (ADR 0157): transforms in the tile node's space (its origin is the tile's corner),
/// sorted by a per-instance hash key uniform in [0, 1), so any prefix is an even random sample of the whole tile and
/// drawing the first <c>f · Count</c> draws the instances whose keys are below <c>f</c> (in expectation); and their bounds.
/// </summary>
internal sealed class FoliageTileData
{
    public static readonly FoliageTileData Empty = new([], Aabb.Empty);

    public FoliageTileData(Transform3D[] transforms, Aabb bounds)
    {
        Transforms = transforms;
        Bounds = bounds;
    }

    /// <summary>Instance transforms (tile-local), in ascending key order.</summary>
    public Transform3D[] Transforms { get; }

    /// <summary>The mesh's bounds under every instance (tile-local); empty without instances or a mesh.</summary>
    public Aabb Bounds { get; }

    public int Count => Transforms.Length;
}

/// <summary>
/// Deterministic foliage placement (ADR 0157). Each type scatters over one global jittered grid of
/// <see cref="FoliageType.Density"/> points per m²; every random value of a point is hashed (SplitMix64) from the
/// type's <see cref="FoliageType.Seed"/>, its index and the point's grid coordinates, so the same terrain data always
/// gives the same instances and rebuilding one tile never moves another's.
/// </summary>
/// <remarks>
/// <para><b>Fuzzy tiles.</b> A point belongs to the tile under its position moved by a hashed offset of up to half a
/// tile on each axis. Tiles thin by drawing a prefix of their instances (sorted by a per-instance hash key), each tile at
/// its own distance factor; because membership near a tile edge is shared at random between the two tiles, the drawn
/// density between tile centres is a linear blend of their factors — continuous, so thinning shows no line at tile
/// edges.</para>
/// <para>A point grows when its accept hash is below the growth probability: the summed weight of the type's
/// <see cref="FoliageType.LayerMask"/> layers (1 without a mask) times a slope fade, and 0 in water or outside the
/// height limits.</para>
/// </remarks>
internal static class FoliagePlacement
{
    private const float SlopeFadeDegrees = 5f;

    /// <summary>The tile side in metres for <paramref name="type"/> on <paramref name="data"/> (a chunk ÷ subdivisions).</summary>
    public static float TileSize(TerrainData data, FoliageType type) =>
        data.ChunkQuads * data.VertexSpacing / Math.Max(1, type.Subdivisions);

    /// <summary>Tiles per map side for <paramref name="type"/>.</summary>
    public static int TilesPerSide(TerrainData data, FoliageType type) => data.ChunksPerSide * Math.Max(1, type.Subdivisions);

    /// <summary>Grid spacing in metres for a density per m² (0 or less: nothing grows).</summary>
    public static float Spacing(float density) => density > 0f ? 1f / MathF.Sqrt(density) : float.PositiveInfinity;

    /// <summary>
    /// Builds tile (<paramref name="tx"/>, <paramref name="tz"/>) of type <paramref name="type"/> (index
    /// <paramref name="typeIndex"/> in the terrain's list): every grid point whose fuzzy position falls in the tile.
    /// </summary>
    public static FoliageTileData BuildTile(TerrainData data, FoliageType type, int typeIndex, int tx, int tz)
    {
        var spacing = Spacing(type.Density);
        var tile = TileSize(data, type);
        var size = data.Grid.Size;
        if (!float.IsFinite(spacing) || type.ScaleMax <= 0f)
            return FoliageTileData.Empty;

        var x0 = tx * tile;
        var z0 = tz * tile;
        var tiles = TilesPerSide(data, type);
        var half = tile * 0.5f;
        var cells = (int)MathF.Ceiling(size / spacing); // grid cells per side over the whole map
        var gx0 = Math.Max(0, (int)MathF.Floor((x0 - half) / spacing));
        var gz0 = Math.Max(0, (int)MathF.Floor((z0 - half) / spacing));
        var gx1 = Math.Min(cells - 1, (int)MathF.Floor((x0 + tile + half) / spacing));
        var gz1 = Math.Min(cells - 1, (int)MathF.Floor((z0 + tile + half) / spacing));
        if (gx1 < gx0 || gz1 < gz0)
            return FoliageTileData.Empty;

        var bed = data.BedHeights;
        var grid = data.Grid;
        var water = data.WaterPixels;
        var realistic = data.Profile == TerrainProfile.Realistic;
        var cellSize = data.CellSize;
        var mask = type.LayerMask;
        var slopeMax = type.SlopeMaxDegrees;
        var origin = new Vector3(x0, 0f, z0);
        var typeSeed = Mix(((ulong)(uint)type.Seed << 32) ^ (uint)typeIndex ^ 0xF011A6EUL);

        var transforms = new List<Transform3D>();
        var keys = new List<float>();
        for (var gz = gz0; gz <= gz1; gz++)
            for (var gx = gx0; gx <= gx1; gx++)
            {
                var state = Mix(typeSeed ^ ((ulong)(uint)gx | ((ulong)(uint)gz << 32)));
                var accept = Next01(ref state);
                var jx = Next01(ref state);
                var jz = Next01(ref state);
                var fuzzX = Next01(ref state);
                var fuzzZ = Next01(ref state);
                var yaw = Next01(ref state);
                var scale = Next01(ref state);
                var key = Next01(ref state);

                var x = (gx + 0.5f + (jx - 0.5f) * type.Jitter) * spacing;
                var z = (gz + 0.5f + (jz - 0.5f) * type.Jitter) * spacing;
                if (x < 0f || z < 0f || x > size || z > size)
                    continue;

                // Fuzzy ownership: the tile under the position moved by up to half a tile (clamped into the map).
                var ox = Math.Clamp((int)MathF.Floor((x + (fuzzX - 0.5f) * tile) / tile), 0, tiles - 1);
                var oz = Math.Clamp((int)MathF.Floor((z + (fuzzZ - 0.5f) * tile) / tile), 0, tiles - 1);
                if (ox != tx || oz != tz)
                    continue;

                var probability = Growth(data, realistic, mask, cellSize, x, z);
                if (probability <= 0f || WaterDepthFraction(grid, water, x, z) > 0f)
                    continue;
                var height = grid.HeightAt(bed, x, z);
                if (height < type.HeightMin || height > type.HeightMax)
                    continue;
                var normal = grid.SmoothNormalAt(bed, x, z);
                var slope = MathF.Acos(Math.Clamp(normal.Y, -1f, 1f)) * (180f / MathF.PI);
                probability *= Math.Clamp((slopeMax - slope) / SlopeFadeDegrees, 0f, 1f);
                if (accept >= probability)
                    continue;

                var s = type.ScaleMin + (type.ScaleMax - type.ScaleMin) * scale;
                var rotation = type.RandomYaw ? Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw * MathF.Tau) : Quaternion.Identity;
                if (type.AlignToNormal > 0f)
                    rotation = Quaternion.Concatenate(rotation, Tilt(Vector3.Normalize(Vector3.Lerp(Vector3.UnitY, normal, type.AlignToNormal))));
                var position = new Vector3(x, height - type.SinkMeters * s, z) - origin;
                transforms.Add(Transform3D.FromTrs(position, rotation, new Vector3(s)));
                keys.Add(key);
            }

        if (transforms.Count == 0)
            return FoliageTileData.Empty;
        var transformArray = transforms.ToArray();
        var keyArray = keys.ToArray();
        Array.Sort(keyArray, transformArray);

        var bounds = Aabb.Empty;
        if (type.Mesh is { } mesh)
        {
            var local = mesh.Bounds;
            foreach (var t in transformArray)
                bounds = bounds.Merge(local.Transform(t.ToMatrix4x4()));
        }

        return new FoliageTileData(transformArray, bounds);
    }

    /// <summary>The growth probability from the splat weights (Realistic) or surface id (Faceted) of the cell under (x, z).</summary>
    private static float Growth(TerrainData data, bool realistic, uint mask, float cellSize, float x, float z)
    {
        if (mask == 0)
            return 1f;
        var u = (int)(x / cellSize);
        var v = (int)(z / cellSize);
        if (!realistic)
        {
            var id = data.GetSurface(u, v);
            return id < 32 && (mask >> id & 1) != 0 ? 1f : 0f;
        }

        var sum = 0f;
        for (var layer = 0; layer < TerrainData.MaxLayers; layer++)
            if ((mask >> layer & 1) != 0)
                sum += data.GetLayerWeight(layer, u, v);
        return MathF.Min(sum, 1f);
    }

    /// <summary>The water depth (fraction of the full depth, interpolated on the height triangle) at local (x, z).</summary>
    private static float WaterDepthFraction(TerrainGrid grid, byte[] water, float x, float z)
    {
        grid.Locate(x, z, out var i, out var j, out var fx, out var fz);
        var stride = grid.Stride;
        var k = j * stride + i;
        return TerrainGrid.Interpolate(grid.IsDiagonalB(i, j), water[k * 4], water[(k + 1) * 4], water[(k + stride) * 4],
            water[(k + stride + 1) * 4], fx, fz);
    }

    /// <summary>The rotation taking +Y to <paramref name="up"/> (unit).</summary>
    private static Quaternion Tilt(Vector3 up)
    {
        var axis = Vector3.Cross(Vector3.UnitY, up);
        var sin = axis.Length();
        if (sin < 1e-6f)
            return Quaternion.Identity;
        return Quaternion.CreateFromAxisAngle(axis / sin, MathF.Atan2(sin, up.Y));
    }

    /// <summary>SplitMix64's finaliser.</summary>
    internal static ulong Mix(ulong z)
    {
        z += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>The next value of a SplitMix64 stream, uniform in [0, 1) with 24 bits.</summary>
    internal static float Next01(ref ulong state)
    {
        state += 0x9E3779B97F4A7C15UL;
        return (Mix(state) >> 40) * (1f / 16777216f);
    }
}

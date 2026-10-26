using System.Numerics;
using MainframeEngine;
using MainframeEngine.Trees;
using static Forest.ValleyNoise;
using DrawingColor = System.Drawing.Color;

namespace Forest;

/// <summary>
/// The distant mountains round the valley: a second, coarse <see cref="Terrain3D"/> (2048 m at 8 m, centred on the map,
/// no collision, no shadows) and the far lake. Outside the map its ground starts at the valley terrain's edge (the cliff
/// tops, the lake bed) and climbs into forested foothills and bare, ridged peaks; inside it sinks out of sight under the
/// valley. The peaks stay low enough that, seen from the valley, none rises much above 15° (the morning sun is at 25°
/// in the north-north-east, so they never shade it, and the north stays lowest). South of the map the lake widens
/// between the foothills into a broad basin, flooded by a flat water plane with the pond's material, its far shore
/// under the southern range.
/// </summary>
public static class ForestMountains
{
    public const int SizeMeters = 2048;
    public const float VertexSpacing = 8f;

    /// <summary>The far terrain's corner in world space: centred on the valley, its grid on the valley's edges.</summary>
    public static Vector3 Origin => new(ValleyLayout.SizeMeters * 0.5f - SizeMeters * 0.5f, 0f, ValleyLayout.SizeMeters * 0.5f - SizeMeters * 0.5f);

    private const float Sunk = -70f;

    /// <summary>
    /// Builds the far terrain and the far lake as children of <paramref name="parent"/>. <paramref name="valleyData"/> is
    /// the valley terrain after carving and flooding (its edge heights start the mountains).
    /// </summary>
    public static Terrain3D Build(Node3D parent, ValleyGenerator valley, TerrainData valleyData, TerrainSplatMaterial3D? material, WaterMaterial3D lakeMaterial)
    {
        var data = new TerrainData
        {
            ResourceName = "Mountains",
            Profile = TerrainProfile.Realistic,
            SizeMeters = SizeMeters,
            VertexSpacing = VertexSpacing,
            ChunkMeters = 128,
            HeightMin = Sunk - 2f,
            HeightMax = 420f,
            CollisionMode = TerrainCollisionMode.None,
            CastShadows = false,
        };
        var n = (int)(SizeMeters / VertexSpacing) + 1;
        var heights = new float[n * n];
        var bed = valleyData.BedHeights.ToArray();
        var grid = valleyData.Grid;
        var origin = Origin;
        for (var j = 0; j < n; j++)
            for (var i = 0; i < n; i++)
                heights[j * n + i] = Height(valley, grid, bed, origin.X + i * VertexSpacing, origin.Z + j * VertexSpacing);
        data.SetHeights(new Rect2I(0, 0, n, n), heights);
        if (material is not null)
            data.SetWeightsFrom((x, z, w) => Weights(data, x, z, w));

        var terrain = new Terrain3D
        {
            Name = "Mountains",
            Data = data,
            Position = origin,
            Material = (Material?)material ?? new StandardMaterial3D { ShadingMode = ShadingMode.Pbr, AlbedoColor = DrawingColor.FromArgb(255, 84, 90, 70), Roughness = 0.95f },
            LodBias = 0.6f,
        };
        parent.AddChild(terrain);
        // Water is not an occluder for the light probes (the terrain's own water is not traced either).
        parent.AddChild(new MeshInstance3D { Name = "FarLake", Mesh = FarLake(), MaterialOverride = lakeMaterial, CastShadows = false, GIMode = GIMode.Disabled });
        return terrain;
    }

    /// <summary>
    /// Conifers (pines, spruces, firs: <paramref name="species"/> is the valley's set) over the foothills' forest, 15–420 m
    /// outside the map below the tree line: one jittered candidate per 9 m cell, kept off steep ground and the far lake.
    /// They are only ever seen from inside the cliffs, so every one is an impostor (no meshes, collision or shadows): a
    /// quad each, which gives the slopes a forest's broken, dark texture.
    /// </summary>
    public static TreeScatter CreateTrees(Terrain3D mountains, TreeSpecies[] species, int seed)
    {
        var data = mountains.Data!;
        var origin = Origin;
        var size = ValleyLayout.SizeMeters;
        const float cell = 9f, near = 15f, far = 420f;
        var placements = new List<TreePlacement>();
        var cells = (int)((size + 2f * far) / cell);
        int[] kinds = [ForestVegetation.PineLarge, ForestVegetation.PineMedium, ForestVegetation.SpruceLarge, ForestVegetation.FirLarge, ForestVegetation.SpruceMedium];
        for (var cj = 0; cj < cells; cj++)
            for (var ci = 0; ci < cells; ci++)
            {
                var x = -far + (ci + 0.1f + 0.8f * Hash01(ci, cj, seed + 501)) * cell;
                var z = -far + (cj + 0.1f + 0.8f * Hash01(ci, cj, seed + 502)) * cell;
                var dx = MathF.Max(MathF.Max(-x, x - size), 0f);
                var dz = MathF.Max(MathF.Max(-z, z - size), 0f);
                var d = MathF.Sqrt(dx * dx + dz * dz);
                if (d < near || d > far || LakeBasin(x, z) > 0.05f)
                    continue;
                var lx = x - origin.X;
                var lz = z - origin.Z;
                var h = data.Grid.HeightAt(data.Heights, lx, lz);
                var normal = data.Grid.SmoothNormalAt(data.Heights, lx, lz);
                var treeline = 150f + 60f * (Fbm(x, z, 90f, 3, 407) - 0.5f);
                // Thinner towards the tree line and on steeper ground; clumped by noise.
                var p = 0.75f * SmoothStep(treeline, treeline - 40f, h) * SmoothStep(0.8f, 0.92f, normal.Y) * (0.4f + 0.9f * Fbm(x, z, 60f, 2, seed + 503));
                if (h < ValleyLayout.LakeLevel + 1f || Hash01(ci, cj, seed + 504) >= p)
                    continue;
                var kind = kinds[(int)(Hash01(ci, cj, seed + 505) * kinds.Length) % kinds.Length];
                placements.Add(new TreePlacement(new Vector3(x, h - 0.3f, z), Hash01(ci, cj, seed + 506) * MathF.Tau, 0.85f + 0.4f * Hash01(ci, cj, seed + 507), kind));
            }

        var trees = new TreeScatter
        {
            Name = "FarTrees",
            Species = species,
            ChunkSize = 128f,
            Lod1Distance = 0.25f,
            Lod2Distance = 0.5f,
            ImpostorDistance = 1f,
            MaxDistance = 1400f,
            CastShadows = false,
            Collision = false,
        };
        trees.SetPlacements([.. placements]);
        return trees;
    }

    /// <summary>
    /// The far terrain's height at world (x, z): sunk inside the map (but on its edge, where it meets the valley terrain),
    /// else the valley's edge height plus the mountains' rise with the distance outside, and the far lake's basin.
    /// </summary>
    public static float Height(ValleyGenerator valley, in TerrainGrid grid, float[] bed, float x, float z)
    {
        var size = ValleyLayout.SizeMeters;
        const float eps = 0.01f;
        var inside = x > eps && x < size - eps && z > eps && z < size - eps;
        if (inside)
            return Sunk;
        var cx = Math.Clamp(x, 0f, size);
        var cz = Math.Clamp(z, 0f, size);
        var edge = grid.HeightAt(bed, cx, cz) - 0.3f;
        var dx = x - cx;
        var dz = z - cz;
        var d = MathF.Sqrt(dx * dx + dz * dz);
        if (d < eps)
            return edge;

        // The range: higher away from the north-north-east (the sun), every peak below ≈ 15° from the map's middle.
        var fromCentre = new Vector2(x - size * 0.5f, z - size * 0.5f);
        var bearing = MathF.Atan2(fromCentre.X, -fromCentre.Y); // 0 = north, +90° = east
        var sunward = 0.5f + 0.5f * MathF.Cos(bearing - float.DegreesToRadians(ValleyLayout.SunAzimuthDegrees));
        var reach = 1f - MathF.Exp(-d / 260f);
        var ridges = Ridged(x, z, 340f, 4, valley.Seed + 401);
        var massif = Fbm(x, z, 900f, 2, valley.Seed + 402);
        var peak = (110f + 260f * massif) * (1f - 0.55f * sunward);
        var rise = reach * peak * (0.45f + 0.75f * ridges);
        // Foothills: rolling, forested ground just outside the cliffs.
        rise += 18f * SmoothStep(0f, 60f, d) * Fbm(x, z, 70f, 3, valley.Seed + 403);
        var cap = MathF.Tan(float.DegreesToRadians(15f)) * (fromCentre.Length() + 40f);
        var h = edge + MathF.Min(rise, MathF.Max(cap - edge, 0f) + 0.15f * rise);
        // Under the low sun's line from the valley's showpieces (the north-north-east stays open), never below the edge.
        h = MathF.Max(MathF.Min(h, valley.SunCap(x, z)), MathF.Min(edge, h));

        // The far lake: south of the map it widens between the foothills, its far shore under the southern range.
        var basin = LakeBasin(x, z);
        if (basin > 0f)
        {
            var lakeBed = ValleyLayout.LakeLevel - ValleyLayout.LakeDepth - 4f * SmoothStep(0f, 200f, z - size);
            h = float.Lerp(h, MathF.Min(h, lakeBed), basin);
        }

        return h;
    }

    /// <summary>The far lake's basin at (x, z), 0..1: from the map's lake southwards, widening, for about 600 m.</summary>
    public static float LakeBasin(float x, float z)
    {
        var size = ValleyLayout.SizeMeters;
        var s = z - size;
        if (s <= 0f)
            return 0f;
        // As wide as the map's lake at its edge (between the west and east cliffs), then wider, then closing.
        var centre = 150f + 60f * MathF.Sin(s / 260f);
        var halfWidth = 140f + 0.7f * s - 0.0009f * s * s;
        var across = MathF.Abs(x - centre) + 25f * (Fbm(x, z, 120f, 2, 405) - 0.5f);
        return SmoothStep(halfWidth + 30f, halfWidth - 20f, across) * SmoothStep(-1f, 12f, s) * SmoothStep(760f, 640f, s);
    }

    private static void Weights(TerrainData data, float x, float z, Span<float> w)
    {
        // Terrain-local to world.
        var origin = Origin;
        var wx = x + origin.X;
        var wz = z + origin.Z;
        var h = data.Grid.HeightAt(data.Heights, x, z);
        var n = data.Grid.SmoothNormalAt(data.Heights, x, z);
        var slope = float.RadiansToDegrees(MathF.Acos(Math.Clamp(n.Y, -1f, 1f)));
        var noise = Fbm(wx, wz, 90f, 3, 407);
        // Forest (needles) low, thinning with height into rock; grassy shoulders; sand on the far lake's shore.
        var treeline = 150f + 60f * (noise - 0.5f);
        var forest = SmoothStep(treeline + 25f, treeline - 25f, h) * SmoothStep(38f, 30f, slope);
        w[ValleyGenerator.Needles] = forest;
        w[ValleyGenerator.Grass] = (1f - forest) * SmoothStep(34f, 24f, slope) * 0.6f;
        w[ValleyGenerator.Rock] = MathF.Max(1f - forest - w[ValleyGenerator.Grass], SmoothStep(30f, 42f, slope));
        w[ValleyGenerator.Sand] = SmoothStep(ValleyLayout.LakeLevel + 1.5f, ValleyLayout.LakeLevel + 0.5f, h) * 2f;
    }

    /// <summary>
    /// The far lake: a flat surface at <see cref="ValleyLayout.LakeLevel"/> from the map's south edge over the basin (and
    /// under the hills round it, which hide it), deep everywhere (<see cref="MeshSurface.Custom0"/>.x = column depth).
    /// </summary>
    private static ArrayMesh FarLake()
    {
        var size = ValleyLayout.SizeMeters;
        var origin = Origin;
        float x0 = origin.X, x1 = origin.X + SizeMeters, z0 = size, z1 = origin.Z + SizeMeters;
        const int cells = 16;
        var positions = new Vector3[(cells + 1) * (cells + 1)];
        var normals = new Vector3[positions.Length];
        var uvs = new Vector2[positions.Length];
        var custom = new Vector4[positions.Length];
        for (var j = 0; j <= cells; j++)
            for (var i = 0; i <= cells; i++)
            {
                var k = j * (cells + 1) + i;
                positions[k] = new Vector3(float.Lerp(x0, x1, i / (float)cells), ValleyLayout.LakeLevel - 0.02f, float.Lerp(z0, z1, j / (float)cells));
                normals[k] = Vector3.UnitY;
                uvs[k] = new Vector2(0.5f, 0f); // no ribbon-edge foam
                custom[k] = new Vector4(12f, 0f, 0f, 0f);
            }

        var indices = new int[cells * cells * 6];
        var o = 0;
        for (var j = 0; j < cells; j++)
            for (var i = 0; i < cells; i++)
            {
                var a = j * (cells + 1) + i;
                var b = a + 1;
                var c = a + cells + 1;
                var d = c + 1;
                indices[o++] = a;
                indices[o++] = c;
                indices[o++] = b;
                indices[o++] = b;
                indices[o++] = c;
                indices[o++] = d;
            }

        var mesh = new ArrayMesh { ResourceName = "Far lake" };
        mesh.AddSurface(new MeshSurface(positions, normals, uvs, indices) { Custom0 = custom });
        return mesh;
    }
}

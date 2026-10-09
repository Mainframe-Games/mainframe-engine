using System.Numerics;
using MainframeEngine;
using DrawingColor = System.Drawing.Color;

namespace Forest;

/// <summary>
/// The near forest-floor clutter (G8e.7, ADR 0175): what a walker sees between the grass and the ferns within 15–22 m:
/// ambientCG leaf-litter cards lying on the ground, twigs (Poly Haven's dry branches, small), pine cones (procedural)
/// under the conifers, pebbles (procedural shapes with Poly Haven's stone texture), moss tufts and scanned grass clumps
/// in the glade, nettles along the path. All of it is terrain foliage (<see cref="FoliageType"/>): placed by the splat
/// layers, thinned out with distance, nothing per frame. Built only with the art.
/// </summary>
public static class ForestClutter
{
    /// <summary>How far the floor clutter reaches (it thins out over the last <see cref="ThinBand"/> metres).</summary>
    public const float Distance = 15f;

    public const float ThinBand = 5f;

    /// <summary>The leaf-litter cards' reach (larger, flat, cheap: they hide the terrain texture's repeats further out).</summary>
    public const float LitterDistance = 24f;

    /// <summary>
    /// The clutter types for the splat layers (bit masks of <see cref="ValleyGenerator"/>'s layer indices), densities
    /// scaled by <paramref name="density"/>.
    /// </summary>
    public static List<FoliageType> Create(float density)
    {
        const uint grass = 1u << ValleyGenerator.Grass, leaves = 1u << ValleyGenerator.Leaves, moss = 1u << ValleyGenerator.Moss,
            needles = 1u << ValleyGenerator.Needles, dirt = 1u << ValleyGenerator.Dirt, mud = 1u << ValleyGenerator.Mud;
        var types = new List<FoliageType>
        {
            new()
            {
                ResourceName = "Leaf litter",
                Mesh = LitterCard(ForestAssets.LeafLitter.SizeMeters),
                Material = LitterMaterial(),
                Density = 0.42f * density,
                LayerMask = leaves | needles | moss | grass | dirt,
                ScaleMin = 0.7f,
                ScaleMax = 1.4f,
                AlignToNormal = 1f,
                SinkMeters = 0f,
                SlopeMaxDegrees = 34f,
                CullDistance = LitterDistance,
                ThinBand = 8f,
                Subdivisions = 4,
                Seed = 21,
            },
            new()
            {
                ResourceName = "Twigs",
                Mesh = ForestValley.Recentre(ForestAssets.DryBranches, withWind: false),
                Material = PropMaterial(ForestAssets.DryBranches, terrainBlend: 0.5f),
                Density = 0.05f * density,
                LayerMask = leaves | needles | moss | dirt,
                ScaleMin = 0.22f,
                ScaleMax = 0.45f,
                AlignToNormal = 0.9f,
                SinkMeters = 0.01f,
                SlopeMaxDegrees = 30f,
                CullDistance = Distance,
                ThinBand = ThinBand,
                Subdivisions = 4,
                Seed = 22,
            },
            new()
            {
                ResourceName = "Pine cones",
                Mesh = PineCone(seed: 3),
                Material = new StandardMaterial3D { ResourceName = "Pine cone", ShadingMode = ShadingMode.Pbr, Roughness = 0.85f },
                Density = 0.35f * density,
                LayerMask = needles,
                ScaleMin = 0.8f,
                ScaleMax = 1.3f,
                AlignToNormal = 1f,
                SinkMeters = 0.008f,
                SlopeMaxDegrees = 32f,
                CullDistance = Distance,
                ThinBand = ThinBand,
                Subdivisions = 4,
                Seed = 23,
            },
            new()
            {
                ResourceName = "Pebbles",
                Mesh = GrassMesh.Rock(radius: 0.07f, roughness: 0.35f, seed: 9, color: new Vector4(1f, 1f, 1f, 1f)),
                Material = PropMaterial(ForestAssets.Stone01, terrainBlend: 0.8f),
                Density = 0.12f * density,
                LayerMask = leaves | dirt | mud | moss,
                ScaleMin = 0.5f,
                ScaleMax = 1.6f,
                AlignToNormal = 0.8f,
                SinkMeters = 0.015f,
                SlopeMaxDegrees = 35f,
                CullDistance = Distance,
                ThinBand = ThinBand,
                Subdivisions = 4,
                Seed = 24,
            },
            new()
            {
                ResourceName = "Moss tufts",
                Mesh = Part(ForestAssets.MossClumps, "moss_01_a_LOD0", withWind: false),
                Material = CutoutMaterial(ForestAssets.MossClumps, translucency: 0.2f, wind: 0f),
                Density = 0.9f * density,
                LayerMask = moss,
                ScaleMin = 3f,
                ScaleMax = 6f,
                AlignToNormal = 1f,
                SinkMeters = 0.003f,
                SlopeMaxDegrees = 45f,
                CullDistance = Distance,
                ThinBand = ThinBand,
                Subdivisions = 4,
                Seed = 25,
            },
            new()
            {
                ResourceName = "Nettles",
                Mesh = Part(ForestAssets.Nettles, "nettle_plant_medium_a_LOD0", withWind: true),
                Material = CutoutMaterial(ForestAssets.Nettles, translucency: 0.45f, wind: 0.5f),
                Density = 0.035f * density,
                LayerMask = leaves | grass,
                ScaleMin = 0.8f,
                ScaleMax = 1.3f,
                AlignToNormal = 0.2f,
                SinkMeters = 0.02f,
                SlopeMaxDegrees = 25f,
                CullDistance = 30f,
                ThinBand = 10f,
                Subdivisions = 2,
                Seed = 26,
            },
        };
        return types;
    }

    /// <summary>
    /// The glade's scanned grass clumps (Poly Haven's grass_medium_01 variants), between the procedural blades: they give
    /// the meadow its body and break the blades' regularity.
    /// </summary>
    public static IEnumerable<FoliageType> ScannedGrass(float density, float distance)
    {
        const uint grass = 1u << ValleyGenerator.Grass, leaves = 1u << ValleyGenerator.Leaves;
        var material = CutoutMaterial(ForestAssets.GrassClumps, translucency: 0.5f, wind: 0.45f);
        (string Node, float Density, int Seed)[] variants =
        [
            ("grass_medium_01_mid_b_LOD0", 0.9f, 31),
            ("grass_medium_01_small_b_LOD0", 1.1f, 32),
            ("grass_medium_01_tall_a_LOD0", 0.5f, 33),
        ];
        foreach (var (node, d, seed) in variants)
            yield return new FoliageType
            {
                ResourceName = "Scanned grass " + node,
                Mesh = Part(ForestAssets.GrassClumps, node, withWind: true),
                Material = material,
                Density = d * density,
                LayerMask = grass | (d < 0.6f ? leaves : 0u),
                ScaleMin = 0.8f,
                ScaleMax = 1.4f,
                AlignToNormal = 0.3f,
                SinkMeters = 0.02f,
                SlopeMaxDegrees = 35f,
                CullDistance = distance,
                ThinBand = 10f,
                Subdivisions = 2,
                Seed = seed,
            };
    }

    /// <summary>A prop's PBR material, blending into the ground below it (<see cref="StandardMaterial3D.TerrainBlend"/>).</summary>
    public static StandardMaterial3D PropMaterial(ForestPropAsset prop, float terrainBlend)
    {
        var material = ForestAssets.CreatePropMaterial(prop);
        material.TerrainBlend = terrainBlend;
        material.TerrainBlendHeight = 0.12f;
        return material;
    }

    /// <summary>A cut-out scan as foliage: its textures, dithered alpha, translucent, wind from the stream.</summary>
    public static FoliageMaterial3D CutoutMaterial(ForestPropAsset prop, float translucency, float wind)
    {
        var source = ForestAssets.CreatePropMaterial(prop);
        return new FoliageMaterial3D
        {
            ResourceName = prop.AssetId,
            AlbedoTexture = source.AlbedoTexture,
            NormalTexture = source.NormalTexture,
            OrmTexture = source.OrmTexture,
            AlphaCutout = true,
            AlphaCutoff = 0.45f,
            AlphaDither = true,
            BackFace = FoliageBackFace.Flip,
            Translucency = translucency,
            ShadingMode = ShadingMode.Pbr,
            Roughness = 1f,
            WindStrength = wind,
            WindBranchBend = 0.3f,
            InstanceValueJitter = 0.12f,
            InstanceHueJitter = 0.1f,
        };
    }

    private static FoliageMaterial3D LitterMaterial()
    {
        var litter = ForestAssets.LeafLitter;
        return new FoliageMaterial3D
        {
            ResourceName = "Leaf litter",
            AlbedoTexture = ResourceLoader.Load<Texture2D>(litter.AlbedoPath),
            NormalTexture = ResourceLoader.Load<Texture2D>(litter.NormalPath),
            OrmTexture = ResourceLoader.Load<Texture2D>(litter.OrmPath),
            AlbedoColor = DrawingColor.FromArgb(255, 205, 190, 165), // the scan's dry autumn leaves, a little darker
            AlphaCutout = true,
            AlphaCutoff = 0.5f,
            AlphaDither = true,
            BackFace = FoliageBackFace.Cull,
            Translucency = 0f,
            ShadingMode = ShadingMode.Pbr,
            Roughness = 1f,
            WindStrength = 0f,
            InstanceValueJitter = 0.18f,
            InstanceHueJitter = 0.15f,
        };
    }

    /// <summary>One mesh node of a prop that is a row of variants, standing on the origin (see <see cref="ForestValley.Recentre"/>).</summary>
    public static ArrayMesh Part(ForestPropAsset prop, string node, bool withWind) => ForestValley.Recentre(prop, withWind, node);

    /// <summary>A flat square card of <paramref name="size"/> metres lying 1 cm above the ground, UV 0–1 (the decal-like litter).</summary>
    public static ArrayMesh LitterCard(float size)
    {
        var h = size * 0.5f;
        const float lift = 0.01f;
        Vector3[] positions = [new(-h, lift, -h), new(h, lift, -h), new(h, lift, h), new(-h, lift, h)];
        Vector3[] normals = [Vector3.UnitY, Vector3.UnitY, Vector3.UnitY, Vector3.UnitY];
        Vector2[] uvs = [new(0f, 0f), new(1f, 0f), new(1f, 1f), new(0f, 1f)];
        int[] indices = [0, 2, 1, 0, 3, 2]; // counter-clockwise seen from above
        var mesh = new ArrayMesh { ResourceName = "Leaf litter card" };
        mesh.AddSurface(new MeshSurface(positions, normals, uvs, indices));
        return mesh;
    }

    /// <summary>
    /// A pine cone lying on its side (≈ 7 cm long): rings of overlapping scales around a tapered axis, brown with darker
    /// scale tips and lighter edges (vertex colours), deterministic per <paramref name="seed"/>.
    /// </summary>
    public static ArrayMesh PineCone(int seed)
    {
        const int rings = 9, perRing = 8;
        const float length = 0.07f, radius = 0.022f;
        var random = new Random(seed);
        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var colors = new List<Vector4>();
        var uvs = new List<Vector2>();
        var indices = new List<int>();
        var tilt = (float)(random.NextDouble() - 0.5) * 0.4f;

        // The axis lies along +X; each scale is a small pyramid (base on the core, tip outwards and towards the base).
        for (var r = 0; r < rings; r++)
        {
            var t = (r + 0.5f) / rings;
            var ringRadius = radius * MathF.Sin(MathF.PI * MathF.Pow(t, 0.8f)) * (0.75f + 0.25f * t);
            var x = (t - 0.5f) * length;
            for (var k = 0; k < perRing; k++)
            {
                var angle = (k + 0.5f * (r & 1)) / perRing * MathF.Tau + tilt;
                var outward = new Vector3(0f, MathF.Cos(angle), MathF.Sin(angle));
                var side = new Vector3(0f, -MathF.Sin(angle), MathF.Cos(angle));
                var core = new Vector3(x, 0f, 0f) + outward * ringRadius * 0.45f;
                var tip = new Vector3(x - length * 0.06f, 0f, 0f) + outward * ringRadius * (1.05f + 0.1f * (float)random.NextDouble());
                var halfWidth = ringRadius * MathF.PI / perRing * 1.1f;
                var halfStep = length / rings * 0.55f;
                Vector3[] corners =
                [
                    core + side * halfWidth + new Vector3(halfStep, 0f, 0f),
                    core - side * halfWidth + new Vector3(halfStep, 0f, 0f),
                    core - side * halfWidth - new Vector3(halfStep, 0f, 0f),
                    core + side * halfWidth - new Vector3(halfStep, 0f, 0f),
                ];
                var shade = 0.75f + 0.25f * (float)random.NextDouble();
                var baseColor = new Vector4(0.30f * shade, 0.19f * shade, 0.11f * shade, 1f);
                var tipColor = new Vector4(0.52f * shade, 0.38f * shade, 0.24f * shade, 1f);
                for (var e = 0; e < 4; e++)
                {
                    var a = corners[e];
                    var b = corners[(e + 1) % 4];
                    var n = Vector3.Normalize(Vector3.Cross(b - a, tip - a));
                    if (Vector3.Dot(n, outward) < 0f)
                        n = -n;
                    var start = positions.Count;
                    positions.AddRange([a, b, tip]);
                    normals.AddRange([n, n, n]);
                    colors.AddRange([baseColor, baseColor, tipColor]);
                    uvs.AddRange([Vector2.Zero, Vector2.UnitX, Vector2.One]);
                    var flip = Vector3.Dot(Vector3.Cross(b - a, tip - a), outward) < 0f;
                    indices.AddRange(flip ? [start, start + 2, start + 1] : [start, start + 1, start + 2]);
                }
            }
        }

        // Rotate to lie on the ground (axis along X, resting on its side) and raise it so its underside touches y = 0.
        var minY = float.MaxValue;
        foreach (var p in positions)
            minY = MathF.Min(minY, p.Y);
        for (var i = 0; i < positions.Count; i++)
            positions[i] -= new Vector3(0f, minY * 0.85f, 0f);
        var surface = new MeshSurface([.. positions], [.. normals], [.. uvs], [.. indices]) { Colors = [.. colors] };
        var mesh = new ArrayMesh { ResourceName = "Pine cone" };
        mesh.AddSurface(surface);
        return mesh;
    }
}

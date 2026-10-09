using System.Numerics;
using MainframeEngine;

namespace Forest;

/// <summary>
/// The art check (<c>--scene Content/Scenes/asset_gallery.mscene</c>): a 64 m terrain with one band per
/// <see cref="ForestAssets.TerrainLayers"/> entry, west to east (grass, leaves, moss, rock on a steep ridge, dirt, gravel,
/// mud, needles), a forest-floor mix in front, every <see cref="ForestAssets.Props"/> model on it, a sun and the
/// <see cref="ForestAssets.SkyPanorama"/> sky. Everything is built when the node is ready (unowned, so the scene file
/// holds this node only). Game arguments after <c>++</c>: <c>--view overview|terrain|props</c> (default overview),
/// <c>--sky panorama|physical</c> (default panorama).
/// </summary>
public sealed class ForestAssetGallery : Node3D
{
    public const string SceneId = "asset_gallery";
    public const string ScenePath = "Content/Scenes/asset_gallery.mscene";

    // The terrain: 64 m at 0.5 m, its local origin at world (-32, 0, -40). Bands run north–south in local z < BandsEndZ.
    private const int TerrainSize = 64;
    private const float BandWidth = 7f;
    private const float BandsStartX = 4f;
    private const float BandsEndZ = 34f;
    private static readonly Vector3 TerrainOrigin = new(-32, 0, -40);

    /// <summary>The views <c>--view</c> selects: camera position and target.</summary>
    public static readonly Dictionary<string, (Vector3 Position, Vector3 Target)> Views = new(StringComparer.Ordinal)
    {
        ["overview"] = (new Vector3(0, 15, 34), new Vector3(0, 0, -8)),
        ["terrain"] = (new Vector3(0, 13, 8), new Vector3(0, 0.5f, -22)),
        ["props"] = (new Vector3(0, 3.6f, 19), new Vector3(0, 0.4f, 5)),
    };

    /// <summary>Where each prop stands (world XZ, yaw in degrees, uniform scale).</summary>
    public static readonly (ForestPropAsset Prop, Vector2 Position, float Yaw, float Scale)[] Placements =
    [
        (ForestAssets.MossyRocks01, new Vector2(-13, 1), 0, 1),
        (ForestAssets.MossyRocks02, new Vector2(-3.5f, 0.5f), 0, 1),
        (ForestAssets.Boulder, new Vector2(3, 2), 30, 1),
        (ForestAssets.DeadTrunk02, new Vector2(8.5f, 1), 10, 1),
        (ForestAssets.Stump01, new Vector2(14, 2), 0, 1),
        (ForestAssets.Stump02, new Vector2(-7, 9), 40, 1),
        (ForestAssets.Rock07, new Vector2(-4, 10), 0, 4),
        (ForestAssets.Rock09, new Vector2(-2.5f, 10.5f), 0, 8),
        (ForestAssets.DryBranches, new Vector2(0, 9.5f), 20, 1),
        (ForestAssets.DeadTrunk, new Vector2(4, 9), -15, 1),
        (ForestAssets.Fern, new Vector2(8, 9), 0, 1.5f),
        (ForestAssets.Fern, new Vector2(10.5f, 10), 120, 1.2f),
    ];

    protected override void OnReady()
    {
        var args = GameHost.UserArgs;
        var view = Views.GetValueOrDefault(Argument(args, "--view") ?? "overview", Views["overview"]);
        var physicalSky = Argument(args, "--sky") == "physical";

        var sun = new DirectionalLight3D
        {
            Name = "Sun",
            Color = new Vector3(1f, 0.94f, 0.84f),
            Energy = 1.6f,
            CastsShadows = true,
            ShadowCascades = 3,
            RotationDegrees = new Vector3(-40, -35, 0),
        };
        AddChild(sun);
        AddChild(new WorldEnvironment
        {
            Name = "Environment",
            Sky = physicalSky
                ? new Sky { Mode = SkyEnvironmentType.Physical, SunDirection = -sun.GlobalForward }
                : ForestAssets.CreatePanoramaSky(),
            AmbientSource = AmbientSource.Sky,
        });

        var camera = new Camera3D { Name = "Camera", Fov = 60f, Position = view.Position, Current = true };
        AddChild(camera);
        camera.LookAt(view.Target);

        var data = TerrainData.Create(TerrainProfile.Realistic, TerrainSize, 0.5f, 16);
        data.SetHeightsFrom(Height);
        data.SetWeightsFrom(Weights);
        AddChild(new Terrain3D
        {
            Name = "Terrain",
            Position = TerrainOrigin,
            Data = data,
            Material = ForestAssets.CreateTerrainMaterial(),
        });

        var materials = new Dictionary<ForestPropAsset, StandardMaterial3D>();
        foreach (var (prop, position, yaw, scale) in Placements)
        {
            if (!materials.TryGetValue(prop, out var material))
                materials[prop] = material = ForestAssets.CreatePropMaterial(prop);
            var node = ForestAssets.InstantiateProp(prop, material);
            node.Name = $"{prop.AssetId}_{ChildCount}";
            node.Position = new Vector3(position.X, Height(position.X - TerrainOrigin.X, position.Y - TerrainOrigin.Z), position.Y);
            node.RotationDegrees = new Vector3(0, yaw, 0);
            node.Scale = new Vector3(scale);
            AddChild(node);
        }
    }

    private static string? Argument(IReadOnlyList<string> args, string name)
    {
        for (var i = 0; i + 1 < args.Count; i++)
            if (args[i] == name)
                return args[i + 1];
        return null;
    }

    /// <summary>The band index (0–7) at local x, or -1 outside the bands.</summary>
    private static int Band(float x)
    {
        if (x < BandsStartX)
            return -1;
        var band = (int)((x - BandsStartX) / BandWidth);
        return band < ForestAssets.TerrainLayers.Count ? band : -1;
    }

    /// <summary>Terrain-local height: gentle undulation, a steep ridge in the rock band, flat ground in front.</summary>
    public static float Height(float x, float z)
    {
        var h = 0.25f * MathF.Sin(x * 0.45f) * MathF.Cos(z * 0.37f) + 0.15f * MathF.Sin(0.9f * x + 0.7f * z);
        var rockCenter = BandsStartX + (ForestAssets.LayerIndex("rock") + 0.5f) * BandWidth;
        var across = SmoothStep(3.2f, 1.6f, MathF.Abs(x - rockCenter));
        var along = SmoothStep(BandsEndZ - 2f, BandsEndZ - 8f, z) * SmoothStep(4f, 10f, z);
        h += 3.5f * across * along;
        // Flatten the front (props) gradually.
        return h * float.Lerp(1f, 0.3f, SmoothStep(BandsEndZ - 2f, BandsEndZ + 2f, z));
    }

    /// <summary>Splat weights: one layer per band (soft 1 m edges); in front, leaves with needle and moss patches.</summary>
    public static void Weights(float x, float z, Span<float> w)
    {
        w.Clear();
        var front = SmoothStep(BandsEndZ - 1f, BandsEndZ + 1f, z);
        var band = Band(x);
        if (band >= 0)
        {
            var local = x - BandsStartX - band * BandWidth;
            var edge = SmoothStep(0f, 1f, local) * SmoothStep(BandWidth, BandWidth - 1f, local);
            w[band] += (1f - front) * float.Max(edge, 0.05f);
        }
        else
        {
            w[0] += 1f - front;
        }

        var needles = SmoothStep(0.4f, 0.7f, 0.5f + 0.5f * MathF.Sin(x * 0.31f) * MathF.Cos(z * 0.43f));
        var moss = SmoothStep(0.55f, 0.8f, 0.5f + 0.5f * MathF.Sin(x * 0.53f + 1.3f) * MathF.Sin(z * 0.29f));
        w[1] += front * (1f - 0.6f * needles - 0.6f * moss);
        w[7] += front * 0.6f * needles;
        w[2] += front * 0.6f * moss;
    }

    private static float SmoothStep(float edge0, float edge1, float x)
    {
        var t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}

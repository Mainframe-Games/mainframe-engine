using System.Numerics;
using MainframeEngine;

namespace Forest;

/// <summary>
/// The Forest's main scene (<c>Content/Scenes/forest.mscene</c>, written by <c>--write-scenes</c>): the morning sun,
/// the environment (physical sky, sky lighting, valley fog, light shafts, auto exposure, glow, wind), the
/// <see cref="ForestValley"/> (generated when ready: terrain, stream, pond, trees, ground cover, props), the
/// <see cref="FirstPersonController"/> at the trailhead and <see cref="ForestAudio"/>. The environment's knobs live here
/// and the post-processing look in <see cref="ForestLook"/>'s <c>.mres</c> files, so the editor can tune them.
/// </summary>
public static class ForestScene
{
    public const string Id = "forest";

    /// <summary>The scene file, relative to the project folder.</summary>
    public const string Path = "Content/Scenes/forest.mscene";

    /// <summary>Where the player starts (feet, on the ground; the valley snaps it exactly when ready).</summary>
    public static Vector3 Spawn
    {
        get
        {
            var s = ValleyLayout.Spawn;
            return new Vector3(s.X, SpawnHeight, s.Y);
        }
    }

    // The trailhead's ground before the path pass (the valley snaps the player to the final ground).
    private static float SpawnHeight => MathF.Round(ValleyGenerator.Generate().GroundHeight(ValleyLayout.Spawn.X, ValleyLayout.Spawn.Y) + 0.3f, 2);

    public static float SpawnYawDegrees => ValleyLayout.SpawnYawDegrees;

    public static Node Build()
    {
        var root = new Node3D { Name = Id };

        // Sun first: the physical sky, fog scatter and shafts follow the first directional light. G8e.2 shadows (ADR 0167):
        // 4 × 1024² cascades to 140 m on a staggered schedule (at most two cascade passes a frame), the last two drawing
        // the trees' level 1 from any distance; soft PCSS penumbrae from the sun's 0.5°; contact shadows; and the far
        // shadow, rendered once over the valley, so the ridges shade the slopes past the cascades. 2048² cascades cost
        // ~4 ms more (leaf cards: every cascade texel under the canopy runs the cut-out shader): they wait for G8e.5.
        Add(root, root, new DirectionalLight3D
        {
            Name = "Sun",
            Color = new Vector3(1f, 0.87f, 0.7f),
            Energy = 2.6f,
            CastsShadows = true,
            ShadowCascades = 4,
            ShadowResolution = 1024,
            ShadowMaxDistance = 140f,
            ShadowSplitLambda = 0.8f,
            ShadowCacheMode = ShadowCacheMode.Staggered,
            ShadowCoarseCascades = 2,
            LightAngularDistance = 0.5f,
            ContactShadows = true,
            ContactShadowLength = 0.4f,
            FarShadowEnabled = true,
            Rotation = Transform3D.BasisLookingAlong(-ValleyLayout.TowardsSun, Vector3.UnitY).GetRotation(),
        });
        Add(root, root, CreateEnvironment());
        Add(root, root, new ForestValley { Name = "Valley" });
        BuildPlayer(root);
        Add(root, root, new ForestAudio
        {
            Name = "Audio",
            RiverPath = "Valley/Stream",
            PlayerPath = "Player",
            FallPositions = [ForestValley.FallPosition],
        });
        return root;
    }

    /// <summary>
    /// The morning environment: physical sky, IBL from it, valley fog with sun scatter and a breeze; the post-processing
    /// look (shafts, eye adaptation, a little glow, GTAO and the forest-morning grade) and the lens (a subtle vignette and a
    /// touch of film grain) are <see cref="ForestLook"/>'s resource files.
    /// </summary>
    public static WorldEnvironment CreateEnvironment() => new()
    {
        Name = "Environment",
        Sky = new Sky
        {
            Mode = SkyEnvironmentType.Physical,
            Turbidity = 6f,
            MieCoefficient = 0.005f,
            GroundColor = new Vector3(0.28f, 0.27f, 0.2f),
            EnergyMultiplier = 1f,
        },
        AmbientSource = AmbientSource.Sky,
        ReflectedLightSource = ReflectedLightSource.Sky,
        AmbientEnergy = 1.6f, // the open canopy's skylight: shade stays readable next to the sun (no GI)

        WindDirection = new Vector3(-1f, 0f, 0.25f), // from the east
        WindStrength = 0.35f,
        WindFrequency = 0.45f,
        WindTurbulence = 0.4f,

        FogEnabled = true,
        FogLightColor = new Vector3(0.42f, 0.47f, 0.53f),
        FogDensity = 0.005f, // beyond the volumetric fog's 64 m: the valley and the far slopes haze over
        FogHeight = 6f,
        FogHeightDensity = 0.08f,
        FogSunScatter = 0.1f, // the volumetric fog scatters the sun now; this only tints the far fog towards it

        // ADR 0171: fog lit through the sun's shadow maps within 64 m: soft rays through the canopy gaps from any view, the
        // shaded air clear (no ambient inject: the sky would light the air under the canopy as brightly as the glade's).
        // Its density follows the height fog's shape, so the valley floor is hazier than the glade.
        VolumetricFogEnabled = true,
        VolumetricFogDensity = 0.018f,
        VolumetricFogAnisotropy = 0.75f,
        VolumetricFogLength = 64f,
        VolumetricFogSkyAffect = 0.2f,
        VolumetricFogAmbientInject = 0.02f,
        VolumetricFogNoiseScale = 8f,
        VolumetricFogNoiseStrength = 0.5f,

        // ADR 0169: the post-processing look and the lens are resource files the editor tunes (ForestLook).
        PostProcess = ForestLook.LoadProfile(),
        CameraAttributes = ForestLook.LoadLens(),
    };

    private static void BuildPlayer(Node root)
    {
        var player = Add(root, root, new FirstPersonController { Name = "Player", Position = Spawn, FloorSnapLength = 0.35f });
        Add(root, player, new CollisionShape3D
        {
            Name = "Shape",
            Shape = new CapsuleShape3D { Radius = player.Radius, Height = player.StandingHeight },
            Position = new Vector3(0, player.StandingHeight / 2, 0),
        });
        var head = Add(root, player, new Node3D
        {
            Name = "Head",
            Position = new Vector3(0, player.StandingHeight - FirstPersonController.EyeBelowTop, 0),
            RotationDegrees = new Vector3(0, SpawnYawDegrees, 0),
        });
        Add(root, head, new Camera3D { Name = "Camera", Current = true, Near = 0.05f, Far = 1200f, Fov = FirstPersonController.VerticalFov(90f, 16f / 9f) });
    }

    /// <summary>Adds <paramref name="child"/> under <paramref name="parent"/>, owned by the scene root (so it is saved).</summary>
    public static T Add<T>(Node root, Node parent, T child) where T : Node
    {
        parent.AddChild(child);
        child.Owner = root;
        return child;
    }
}

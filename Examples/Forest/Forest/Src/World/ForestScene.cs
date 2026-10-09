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

    /// <summary>
    /// The valley's baked light probes (ADR 0170), relative to the project folder: a <see cref="LightProbeData"/> and its
    /// <c>.probes</c> file (LFS), written by <c>--bake-lighting</c> (or the editor's Bake Lighting) next to the scene.
    /// </summary>
    public const string ProbesPath = "Content/Scenes/forest-lighting.mres";

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
            Color = new Vector3(1f, 0.93f, 0.82f), // ADR 0178: a warm white morning sun (0.87, 0.7 before: the haze went orange)
            // 2.6 against a 1.6 sky blew out sun-facing slopes and canopy tops in shaded views: the probe-darkened shade pins
            // auto exposure at its maximum, so the sun / sky ratio is what keeps sunlit surfaces in range.
            Energy = 2f,
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
            // Pitch down by the elevation, then turn from north (−Z) to the azimuth: shines along −TowardsSun.
            RotationDegrees = new Vector3(-ValleyLayout.SunElevationDegrees, 180f - ValleyLayout.SunAzimuthDegrees, 0f),
        });
        Add(root, root, CreateEnvironment());
        Add(root, root, new ForestValley { Name = "Valley" });
        Add(root, root, CreateProbes());
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
        // The open canopy's skylight: shade stays readable next to the sun. ADR 0178: 2 → 2.4, the references' lifted shade.
        AmbientEnergy = 2.4f,

        WindDirection = new Vector3(-1f, 0f, 0.25f), // from the east
        WindStrength = 0.35f,
        WindFrequency = 0.45f,
        WindTurbulence = 0.4f,

        // ADR 0178 (look-dev against the Unreal references): a bright, warm white haze, strong aerial perspective. The far
        // fog is a pale warm grey (blue-grey before) and denser, so distance fades to a low-contrast glow that lifts the
        // blacks, and it takes the sun's colour looking into the low sun.
        FogEnabled = true,
        FogLightColor = new Vector3(0.78f, 0.8f, 0.77f),
        FogDensity = 0.0015f, // 0.004 in ADR 0178 read too hazy in play (Brogan, 2026-10-10)
        FogHeight = 8f,
        FogHeightDensity = 0.03f,
        FogSunScatter = 0.06f,

        // ADR 0171: fog lit through the sun's shadow maps within 64 m: rays through the canopy gaps from any view. Its density
        // follows the height fog's shape, so the valley floor is hazier than the slopes. ADR 0178: denser, warm, less
        // forward-peaked, and lit by the (probe-occluded) sky too, so the shaded air glows softly instead of darkening.
        VolumetricFogEnabled = true,
        VolumetricFogDensity = 0.004f,
        VolumetricFogAlbedo = new Vector3(1f, 1f, 0.95f),
        VolumetricFogAnisotropy = 0.65f,
        VolumetricFogLength = 64f,
        VolumetricFogSkyAffect = 0.15f,
        VolumetricFogAmbientInject = 0.08f,
        VolumetricFogNoiseScale = 8f,
        VolumetricFogNoiseStrength = 0.7f,

        // ADR 0169: the post-processing look and the lens are resource files the editor tunes (ForestLook).
        PostProcess = ForestLook.LoadProfile(),
        CameraAttributes = ForestLook.LoadLens(),
    };

    /// <summary>
    /// The valley's probe volume (ADR 0170): terrain-following over the whole 256 m terrain, every 2 m, eight layers up to
    /// the canopy (27 m). The valley is generated at load, so the volume checks its committed bake against what the valley
    /// generated (the hash of everything the bake reads) and bakes in the background when they differ.
    /// </summary>
    public static LightProbeVolume CreateProbes() => new()
    {
        Name = "Lighting",
        Position = new Vector3(ValleyLayout.SizeMeters * 0.5f, 0f, ValleyLayout.SizeMeters * 0.5f),
        Size = new Vector3(ValleyLayout.SizeMeters, 32f, ValleyLayout.SizeMeters),
        Layout = ProbeLayout.TerrainFollowing,
        ProbeSpacing = new Vector3(2f),
        RaysPerProbe = 192,
        Bounces = 2,
        // Tuned against R2/R3/R5 (ADR 0170): the full bake reads too dark under the dense canopy next to the sunlit glade
        // (the leaves' sub-pixel gaps and their spectral transmission are not in the bake), so 15 % of the sky occlusion, the
        // rest of the blocked sky light tinted the canopy's green, and a stronger bounce: the shade stays readable and takes
        // the canopy's colour. A darker shade pinned auto exposure at its maximum and blew out sunlit slopes (Brogan's
        // playtest, 2026-10-09), so the fill is strong and forest.mres caps the exposure (min luminance 0.03).
        Energy = 4f,
        SkyOcclusion = 0.15f,
        OcclusionTint = System.Drawing.Color.FromArgb(255, 200, 225, 170),
        BakeWhenStale = true,
        Data = ResourceLoader.Exists(ProbesPath) ? ResourceLoader.Load<LightProbeData>(ProbesPath) : null,
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

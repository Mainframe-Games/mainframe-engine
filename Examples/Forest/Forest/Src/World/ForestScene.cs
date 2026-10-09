using System.Numerics;
using MainframeEngine;

namespace Forest;

/// <summary>
/// The Forest's main scene (<c>Content/Scenes/forest.mscene</c>, written by <c>--write-scenes</c>): the morning sun,
/// the environment (physical sky, sky lighting, valley fog, light shafts, auto exposure, glow, wind), the
/// <see cref="ForestValley"/> (generated when ready: terrain, stream, pond, trees, ground cover, props), the
/// <see cref="FirstPersonController"/> at the trailhead and <see cref="ForestAudio"/>. The look's knobs live here, so
/// the editor can tune them.
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

        // Sun first: the physical sky, fog scatter and shafts follow the first directional light.
        Add(root, root, new DirectionalLight3D
        {
            Name = "Sun",
            Color = new Vector3(1f, 0.87f, 0.7f),
            Energy = 2.6f,
            CastsShadows = true,
            ShadowCascades = 2,
            ShadowResolution = 1024,
            ShadowMaxDistance = 60f,
            ShadowSplitLambda = 0.8f,
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

    /// <summary>The morning look: physical sky, IBL from it, valley fog with sun scatter, shafts, eye adaptation, a little glow, a breeze.</summary>
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
        FogDensity = 0.003f,
        FogHeight = 6f,
        FogHeightDensity = 0.08f,
        FogSunScatter = 0.3f,

        GlowEnabled = true,
        GlowIntensity = 0.3f,
        GlowStrength = 1f,
        GlowBloom = 0f,
        GlowHdrThreshold = 4f, // only the sun, its glints and the brightest sky gaps bloom; sunlit leaves do not sparkle
        GlowHdrLuminanceCap = 3f,

        AutoExposureEnabled = true,
        AutoExposureScale = 0.16f,
        AutoExposureSpeed = 0.6f,

        LightShaftsEnabled = true,
        LightShaftsIntensity = 2.3f, // the morning haze in the canopy: strong rays through every sky gap near the sun
        LightShaftsDecay = 0.965f,
        LightShaftsDensity = 0.85f,
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

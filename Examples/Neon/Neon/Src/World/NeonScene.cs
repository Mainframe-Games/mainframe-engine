using System.Numerics;
using MainframeEngine;

namespace Neon;

/// <summary>
/// Neon's main scene (<c>Assets/Scenes/neon.mscene</c>, written by <c>--write-scenes</c>): a dim moonlight, a dark
/// procedural night sky, two coloured "city glow" lights outside the window wall (placeholders until the city), the
/// <see cref="NeonApartment"/> (built when ready), the <see cref="FirstPersonController"/> with its
/// <see cref="Interactor"/> at the front door, the <see cref="NeonAudio"/> and the <see cref="FallSafety"/> under it all.
/// </summary>
public static class NeonScene
{
    public const string Id = "neon";

    /// <summary>The scene file, relative to the project folder.</summary>
    public const string Path = "Assets/Scenes/neon.mscene";

    /// <summary>Where the player starts (feet): the apartment model's <c>Spawn</c>, inside the front door.</summary>
    public static readonly Vector3 Spawn = new(5.1f, 0.02f, -4f);

    /// <summary>The player's starting yaw (180°: facing the window wall, +Z).</summary>
    public const float SpawnYawDegrees = 180f;

    public static Node Build()
    {
        var root = new Node3D { Name = Id };

        // The first directional light is the sky's sun: a faint, cold moon high in the south-west, through the glass.
        Add(root, root, new DirectionalLight3D
        {
            Name = "Moon",
            Color = new Vector3(0.55f, 0.65f, 1f),
            Energy = 0.12f,
            CastsShadows = true,
            ShadowCascades = 2,
            ShadowMaxDistance = 40f,
            RotationDegrees = new Vector3(-40f, 200f, 0f),
        });
        Add(root, root, CreateEnvironment());
        // The city's glow below the window wall, until the city: magenta from the left, teal from the right.
        Add(root, root, new OmniLight3D { Name = "CityGlowMagenta", Position = new Vector3(-10f, -4f, 18f), Color = new Vector3(1f, 0.2f, 0.65f), Energy = 3f, Range = 30f });
        Add(root, root, new OmniLight3D { Name = "CityGlowTeal", Position = new Vector3(12f, -4f, 18f), Color = new Vector3(0.1f, 0.85f, 1f), Energy = 2.5f, Range = 30f });
        Add(root, root, new NeonApartment { Name = "Apartment" });
        BuildPlayer(root);
        Add(root, root, new NeonAudio { Name = "Audio" });
        var safety = Add(root, root, FallSafety.Create(Spawn));
        foreach (var child in safety.Children)
            child.Owner = root;
        return root;
    }

    /// <summary>A dark night: a navy sky over a magenta-lit horizon (the city's light in the haze), dim sky ambience, fog.</summary>
    public static WorldEnvironment CreateEnvironment() => new()
    {
        Name = "Environment",
        Sky = new Sky
        {
            Mode = SkyEnvironmentType.Procedural,
            SkyColor = new Vector3(0.012f, 0.014f, 0.035f),
            HorizonColor = new Vector3(0.16f, 0.05f, 0.14f),
            GroundColor = new Vector3(0.05f, 0.025f, 0.06f),
            HorizonSharpness = 3f,
            SunColor = new Vector3(0.7f, 0.75f, 1f),
            SunIntensity = 0.4f,
            SunAngularRadius = 0.3f,
        },
        AmbientSource = AmbientSource.Sky,
        ReflectedLightSource = ReflectedLightSource.Sky,
        AmbientEnergy = 0.6f,
        FogEnabled = true,
        FogLightColor = new Vector3(0.12f, 0.05f, 0.12f),
        FogDensity = 0.004f,
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
        Add(root, head, new Camera3D { Name = "Camera", Current = true, Near = 0.05f, Far = 2000f, Fov = FirstPersonController.VerticalFov(90f, 16f / 9f) });
        Add(root, player, new Interactor { Name = "Interactor" });
    }

    /// <summary>Adds <paramref name="child"/> under <paramref name="parent"/>, owned by the scene root (so it is saved).</summary>
    public static T Add<T>(Node root, Node parent, T child) where T : Node
    {
        parent.AddChild(child);
        child.Owner = root;
        return child;
    }
}

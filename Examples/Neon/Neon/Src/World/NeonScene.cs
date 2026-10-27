using System.Numerics;
using MainframeEngine;

namespace Neon;

/// <summary>
/// Neon's main scene (<c>Assets/Scenes/neon.mscene</c>, written by <c>--write-scenes</c>): a dim moonlight, a dark
/// procedural night sky glowing magenta at the horizon, the <see cref="NeonCity"/> round the tower (built when ready; no
/// lights: its neon is emission and glow), the <see cref="NeonApartment"/> (built when ready), the
/// <see cref="FirstPersonController"/> with its <see cref="Interactor"/> at the front door, the <see cref="NeonAudio"/>
/// and the <see cref="FallSafety"/> under it all.
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
        Add(root, root, new NeonCity { Name = "City" });
        Add(root, root, new NeonApartment { Name = "Apartment" });
        BuildPlayer(root);
        Add(root, root, new NeonAudio { Name = "Audio" });
        var safety = Add(root, root, FallSafety.Create(Spawn));
        foreach (var child in safety.Children)
            child.Owner = root;
        return root;
    }

    /// <summary>
    /// A rainy night over the city: a near-black sky whose horizon glows magenta (the city's light in the cloud), purple
    /// haze that thickens towards the streets 70 m below (height fog), dim sky ambience. The look (glow, tonemap, lens) is
    /// <see cref="NeonLook"/>'s resource files.
    /// </summary>
    public static WorldEnvironment CreateEnvironment() => new()
    {
        Name = "Environment",
        Sky = new Sky
        {
            Mode = SkyEnvironmentType.Procedural,
            SkyColor = new Vector3(0.01f, 0.012f, 0.03f),
            HorizonColor = new Vector3(0.36f, 0.09f, 0.3f),
            GroundColor = new Vector3(0.18f, 0.05f, 0.16f),
            HorizonSharpness = 2.2f,
            SunColor = new Vector3(0.7f, 0.75f, 1f),
            SunIntensity = 0.3f,
            SunAngularRadius = 0.3f,
        },
        AmbientSource = AmbientSource.Sky,
        ReflectedLightSource = ReflectedLightSource.Sky,
        AmbientEnergy = 0.6f,
        FogEnabled = true,
        FogLightColor = new Vector3(0.24f, 0.07f, 0.24f),
        FogDensity = 0.0026f,
        FogHeight = CityLayout.GroundY + 30f,
        FogHeightDensity = 0.012f,
        PostProcess = NeonLook.LoadProfile(),
        CameraAttributes = NeonLook.LoadLens(),
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

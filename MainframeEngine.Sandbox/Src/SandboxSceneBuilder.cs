using System.Numerics;

namespace MainframeEngine.Sandbox;

/// <summary>
/// Builds the Sandbox scene in code. The game loads <see cref="Game.MainScene"/> from disk; this is the
/// source it was generated from (<c>dotnet run --project MainframeEngine.Sandbox -- --write-scene
/// MainframeEngine.Sandbox/Content/Scenes/Sandbox.mscene</c>) and doubles as an example of building a tree
/// by hand.
/// </summary>
public static class SandboxSceneBuilder
{
    public static Node Build()
    {
        var root = new Node3D { Name = "Sandbox" };

        var camera = new FlyCamera { Name = "Camera", Position = new Vector3(0, 5f, 10f) };
        camera.LookAt(Vector3.Zero);
        Add(root, camera);

        Add(root, new WorldEnvironment
        {
            Name = "Environment",
            Sky = new Sky { Mode = SkyEnvironmentType.Panoramic, Panorama = "Content/Sky/sky_10_2k.png" },
        });

        Add(root, new SpineNode
        {
            Name = "SpineBoy",
            Folder = "Content/Models/Spine/SpineBoy",
            Scale = new Vector3(0.1f, 0.1f, 0.1f),
            Animation = "walk",
        });
        Add(root, new Quad { Name = "Floor", RotationDegrees = new Vector3(90, 0, 0), Scale = new Vector3(10, 10, 1) });
        Add(root, new SpinningBox { Name = "Box", Position = new Vector3(3, 1, 0) });

        // Every shadow type at once (2 directional + 1 point + 2 spot): each shadow sub-pass uses its own
        // light matrix (M1 renderer stabilization).
        Add(root, Aim(new DirectionalLight3D
        {
            Name = "Sun",
            Position = new Vector3(0, 5, 0),
            Color = new Vector3(1f, 0.95f, 0.8f),
            Energy = 0.8f,
        }, new Vector3(0, -0.5f, -1)));
        Add(root, Aim(new DirectionalLight3D
        {
            Name = "Fill",
            Position = new Vector3(0, 5, 0),
            Color = new Vector3(0.6f, 0.7f, 1f),
            Energy = 0.2f,
        }, new Vector3(-0.8f, -1f, 0.3f)));
        Add(root, new OmniLight3D
        {
            Name = "BlueLamp",
            Position = new Vector3(4.5f, 1.5f, 2f),
            Color = new Vector3(0.2f, 0.5f, 1f),
            Energy = 0.8f,
            Range = 8,
        });
        Add(root, Aim(new SpotLight3D
        {
            Name = "WarmSpot",
            Position = new Vector3(-3, 4, 3),
            Color = new Vector3(1f, 0.6f, 0.4f),
            Energy = 0.9f,
            Range = 15f,
            InnerConeAngle = 12f,
            OuterConeAngle = 25f,
        }, new Vector3(0.5f, -1, -0.5f)));
        Add(root, Aim(new SpotLight3D
        {
            Name = "GreenSpot",
            Position = new Vector3(3, 4, -2),
            Color = new Vector3(0.5f, 1f, 0.6f),
            Energy = 0.7f,
            Range = 12f,
            InnerConeAngle = 15f,
            OuterConeAngle = 28f,
        }, new Vector3(-0.2f, -1, 0.4f)));

        return root;
    }

    /// <summary>Builds the scene and saves it to <paramref name="path"/>.</summary>
    public static string Write(string path)
    {
        var root = Build();
        try
        {
            return SceneSaver.Save(root, path);
        }
        finally
        {
            root.Free();
        }
    }

    private static void Add(Node root, Node child)
    {
        root.AddChild(child);
        child.Owner = root;
    }

    // Light nodes shine along -Z; point that axis along `direction`. Stored as Euler degrees, so the
    // scene file stays readable.
    private static T Aim<T>(T light, Vector3 direction) where T : Node3D
    {
        light.LookAt(light.Position + Vector3.Normalize(direction));
        light.RotationDegrees = light.RotationDegrees; // pin the Euler form that gets saved
        return light;
    }
}

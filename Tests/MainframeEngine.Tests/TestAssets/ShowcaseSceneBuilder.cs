using System.Drawing;
using System.Numerics;

namespace MainframeEngine.Tests.TestAssets;

using Color = System.Drawing.Color;

/// <summary>
/// Builds the showcase fixture scene in code: every shadow-light type, primitives and materials, the glTF test
/// model, SpineBoy, a sky, positional audio and a physics tumble. The committed copy is
/// <c>Tests/Content/Scenes/Showcase.mscene</c> (parsed, instantiated and re-saved by the serialization tests, opened
/// by the editor smoke/render tests); regenerate it with
/// <c>UPDATE_TEST_ASSETS=1 dotnet test Tests/MainframeEngine.Tests --filter FullyQualifiedName~TheShowcaseFixture</c>.
/// </summary>
public static class ShowcaseSceneBuilder
{
    private const string TestModelFolder = "Content/Models/TestModel";

    public static Node3D Build()
    {
        var root = new Node3D { Name = "Showcase" };

        var camera = new Camera3D { Name = "Camera", Current = true, Position = new Vector3(0, 5, 10) };
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

        // M3 meshes and materials: a textured floor (the checker's .meta asks for nearest filtering), the box, a few primitives (blended glass, emissive capsule, cylinder) and the glTF test model, instanced
        // from its file by UID (imported through Assimp; only its overrides are saved here).
        var checker = ResourceLoader.Load<Texture2D>(TestModelFolder + "/checker.png");
        Add(root, new MeshInstance3D
        {
            Name = "Floor",
            Mesh = new PlaneMesh { Size = new Vector2(10, 10) },
            MaterialOverride = new StandardMaterial3D { AlbedoTexture = checker, UvScale = new Vector2(5, 5), Specular = 0.1f },
        });
        var box = new MeshInstance3D
        {
            Name = "Box",
            Position = new Vector3(3, 1, 0),
            Mesh = new BoxMesh(),
            MaterialOverride = new StandardMaterial3D { AlbedoColor = Color.FromArgb(255, 230, 120, 80) },
        };
        Add(root, box);
        Add(root, new MeshInstance3D
        {
            Name = "Glass",
            Position = new Vector3(1.5f, 0.6f, 2.5f),
            Mesh = new SphereMesh { Radius = 0.6f, Height = 1.2f },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = Color.FromArgb(110, 120, 180, 255),
                Transparency = AlphaMode.Blend,
                Specular = 1f,
                Shininess = 96f,
            },
        });
        Add(root, new MeshInstance3D
        {
            Name = "Lamp",
            Position = new Vector3(-1.5f, 0.75f, 2.5f),
            Mesh = new CapsuleMesh { Radius = 0.25f, Height = 1.5f },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = Color.Black,
                EmissionColor = Color.FromArgb(255, 255, 160, 60),
                EmissionEnergy = 2f,
                ShadingMode = ShadingMode.Unshaded,
            },
        });
        Add(root, new MeshInstance3D
        {
            Name = "Column",
            Position = new Vector3(-3.5f, 1f, -2.5f),
            Mesh = new CylinderMesh { TopRadius = 0.35f, BottomRadius = 0.45f, Height = 2f },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = Color.FromArgb(255, 210, 205, 190), Specular = 0.5f, Shininess = 64f },
        });

        var model = ResourceLoader.Load<PackedScene>(TestModelFolder + "/test_model.gltf");
        var instance = model.Instantiate<Node3D>();
        instance.Name = "Model";
        instance.Position = new Vector3(-2.5f, 0, -0.5f);
        instance.RotationDegrees = new Vector3(0, 20, 0);
        Add(root, instance);
        model.Release();

        // A quiet looping hum attached to the box (M7): streamed OGG, positional, on the SFX bus. Walk a
        // camera around it to hear the panning and the distance attenuation / low-pass.
        Add(root, box, new AudioPlayer3D
        {
            Name = "Ambience",
            Stream = new AudioStream
            {
                File = "Content/Audio/ambient_hum.ogg",
                LoadMode = AudioLoadMode.Stream,
                Loop = true,
                LoopEnd = 6f, // the file is a 6 s seamless loop (the encoder pads its tail)
            },
            Bus = "SFX",
            VolumeDb = -18f,
            Autoplay = true,
            UnitSize = 3f,
            MaxDistance = 60f,
            LowPassAtMaxDistance = 1500f,
        });
        AddPhysics(root);

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

    /// <summary>
    /// M6 physics: a static collider under the floor plane and a tumble of crates dropped onto it (each a
    /// <see cref="RigidBody3D"/> with a shared <see cref="BoxShape3D"/> and a <see cref="MeshInstance3D"/> visual sharing
    /// one <see cref="BoxMesh"/>, with one material per crate colour).
    /// </summary>
    private static void AddPhysics(Node root)
    {
        var floor = new StaticBody3D { Name = "FloorCollider" };
        Add(root, floor);
        Add(root, floor, new CollisionShape3D { Name = "Shape", Position = new Vector3(0, -0.5f, 0), Shape = new BoxShape3D { Size = new Vector3(10, 1, 10) } });

        // Back right, clear of the M3 showcase meshes (which have no colliders).
        var crates = new Node3D { Name = "Crates", Position = new Vector3(1.5f, 0, -2.5f) };
        Add(root, crates);
        var cube = new BoxShape3D { Size = new Vector3(0.5f) };
        var mesh = new BoxMesh { Size = new Vector3(0.5f) };
        StandardMaterial3D[] materials =
        [
            new() { AlbedoColor = Color.FromArgb(255, 210, 140, 70) },
            new() { AlbedoColor = Color.FromArgb(255, 180, 110, 60) },
            new() { AlbedoColor = Color.FromArgb(255, 150, 95, 55) },
        ];
        for (var i = 0; i < 9; i++)
        {
            var crate = new RigidBody3D
            {
                Name = $"Crate{i}",
                Position = new Vector3(i % 3 * 0.55f - 0.55f + i / 3 * 0.1f, 1.5f + i * 0.6f, i / 3 * 0.3f - 0.3f),
                RotationDegrees = new Vector3(0, i * 23 % 90, i * 11 % 25),
                Mass = 2,
            };
            Add(root, crates, crate);
            Add(root, crate, new CollisionShape3D { Name = "Shape", Shape = cube });
            Add(root, crate, new MeshInstance3D { Name = "Visual", Mesh = mesh, MaterialOverride = materials[i % materials.Length] });
        }
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

    private static void Add(Node root, Node child) => Add(root, root, child);

    private static void Add(Node root, Node parent, Node child)
    {
        parent.AddChild(child);
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

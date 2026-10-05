using System.Drawing;
using System.Numerics;
using MainframeEngine;
using static Demo.DemoBuild;

namespace Demo;

/// <summary>Primitives, glass, emissive lamps, a glTF model, panoramic sky, cascaded + spot shadows, orbit camera.</summary>
public static class Basic3DScene
{
    public static Node Build()
    {
        var root = new Node3D { Name = "basic_3d" };
        Add(root, root, new OrbitCamera { Name = "Camera", Current = true, Radius = 11.5f, Height = 3.6f, Target = new Vector3(0, 1.4f, 0), Fov = 50f });
        Add(root, root, new WorldEnvironment
        {
            Name = "Environment",
            Sky = new Sky { Mode = SkyEnvironmentType.Panoramic, Panorama = "Content/Sky/sky_10_2k.png" },
            AmbientColor = new Vector3(0.36f, 0.4f, 0.5f),
        });

        Add(root, root, Mesh("Plinth", new CylinderMesh { TopRadius = 6f, BottomRadius = 6.2f, Height = 0.4f, RadialSegments = 96 },
            Lit(Color.FromArgb(78, 88, 110), shininess: 96f), new Vector3(0, -0.2f, 0)));
        Add(root, root, Mesh("Floor", new PlaneMesh { Size = new Vector2(600, 600) },
            Lit(Color.FromArgb(60, 76, 96), shininess: 8f), new Vector3(0, -0.4f, 0)));

        // A ring of primitives with distinct materials.
        var ring = Add(root, root, new Node3D { Name = "Ring" });
        Add(root, ring, Mesh("Box", new BoxMesh { Size = new Vector3(1.4f) }, Lit(Color.FromArgb(232, 93, 63)), new Vector3(3.2f, 0.7f, 0), rotationY: 25f));
        Add(root, ring, Mesh("Sphere", new SphereMesh { Radius = 0.8f, Height = 1.6f, RadialSegments = 64, Rings = 32 }, Lit(Color.FromArgb(70, 140, 240), shininess: 128f), new Vector3(-3.2f, 0.8f, 0)));
        Add(root, ring, Mesh("Capsule", new CapsuleMesh { Radius = 0.5f, Height = 2f }, Lit(Color.FromArgb(250, 204, 21)), new Vector3(0, 1f, 3.2f)));
        Add(root, ring, Mesh("Column", new CylinderMesh { TopRadius = 0.45f, BottomRadius = 0.6f, Height = 3f }, Lit(Color.FromArgb(226, 232, 240)), new Vector3(0, 1.5f, -3.2f)));
        Add(root, ring, Mesh("Glass", new SphereMesh { Radius = 0.9f, Height = 1.8f, RadialSegments = 64, Rings = 32 },
            new StandardMaterial3D { AlbedoColor = Color.FromArgb(90, 180, 220, 255), Transparency = AlphaMode.Blend, Shininess = 256f, Specular = 2f },
            new Vector3(2.3f, 0.9f, 2.3f)));
        Add(root, ring, Mesh("Lamp", new CapsuleMesh { Radius = 0.25f, Height = 1.2f },
            new StandardMaterial3D { AlbedoColor = Color.FromArgb(255, 120, 30), EmissionColor = Color.FromArgb(255, 170, 80), EmissionEnergy = 1.2f, ShadingMode = ShadingMode.Unshaded },
            new Vector3(-2.3f, 0.6f, -2.3f)));

        var model = ResourceLoader.Load<PackedScene>("Content/Models/TestModel/test_model.gltf");
        try
        {
            var instance = model.Instantiate<Node3D>();
            instance.Name = "Model";
            instance.Position = new Vector3(0, 0.05f, 0);
            Add(root, root, instance);
        }
        finally
        {
            model.Release();
        }

        var lights = Add(root, root, new Node3D { Name = "Lights" });
        Aim(root, lights, new DirectionalLight3D { Name = "Sun", Color = new Vector3(1f, 0.95f, 0.86f), Energy = 1.8f, CastsShadows = true, ShadowCascades = 3 },
            new Vector3(6, 10, 4), Vector3.Zero);
        Add(root, lights, new OmniLight3D { Name = "Blue", Color = new Vector3(0.35f, 0.55f, 1f), Energy = 2.5f, Range = 7f, CastsShadows = true, Position = new Vector3(-3, 2.5f, 3) });
        Aim(root, lights, new SpotLight3D { Name = "WarmSpot", Color = new Vector3(1f, 0.6f, 0.3f), Energy = 9f, Range = 16f, InnerConeAngle = 18f, OuterConeAngle = 28f, CastsShadows = true },
            new Vector3(4, 6, -4), new Vector3(0, 0, 0));
        Aim(root, lights, new SpotLight3D { Name = "MintSpot", Color = new Vector3(0.4f, 1f, 0.75f), Energy = 7f, Range = 16f, InnerConeAngle = 14f, OuterConeAngle = 24f, CastsShadows = true },
            new Vector3(-5, 5, -2), new Vector3(0, 1, 0));

        var ui = Add(root, root, new UiLayer { Name = "Ui", Layer = 10 });
        Add(root, ui, new Basic3DPanel { Name = "Panel" });
        return root;
    }

    private static StandardMaterial3D Lit(Color color, float shininess = 48f) =>
        new() { AlbedoColor = color, Shininess = shininess, Specular = 0.8f };

    private static MeshInstance3D Mesh(string name, Mesh mesh, Material material, Vector3 position, float rotationY = 0f) =>
        new() { Name = name, Mesh = mesh, MaterialOverride = material, Position = position, RotationDegrees = new Vector3(0, rotationY, 0) };

    private static T Aim<T>(Node root, Node parent, T light, Vector3 from, Vector3 to) where T : Light3D
    {
        Add(root, parent, light);
        light.Position = from;
        light.LookAt(to);
        light.RotationDegrees = light.RotationDegrees; // pin the Euler angles the saver writes
        return light;
    }
}

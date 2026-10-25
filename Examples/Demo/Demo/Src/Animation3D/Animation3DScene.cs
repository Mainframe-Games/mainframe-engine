using Color = System.Drawing.Color;
using System.Numerics;
using MainframeEngine;
using static Demo.DemoBuild;

namespace Demo;

/// <summary>
/// A skinned glTF character (ADR 0207: <c>Assets/Models/Character/character.glb</c>, 31 bones, eight clips) played by its
/// imported <see cref="AnimationPlayer"/>, with a hammer and a torch light on its right hand through a
/// <see cref="BoneAttachment3D"/>, under a shadowing sun. <see cref="Animation3DPanel"/> picks the clip, the crossfade and
/// the speed, and shows the clip's events as they fire.
/// </summary>
public static class Animation3DScene
{
    public const string CharacterPath = "Assets/Models/Character/character.glb";

    public static Node Build()
    {
        var root = new Node3D { Name = "animation_3d" };
        var camera = Add(root, root, new Camera3D { Name = "Camera", Current = true, Position = new Vector3(0.6f, 1.55f, 3.9f) });
        // Looking at (0, 0.95f, 0), as literal angles: LookAt's float result differs between x64 and arm64.
        camera.RotationDegrees = new Vector3(-8.646005f, 8.746162f, 0);
        Add(root, root, new WorldEnvironment { Name = "Environment", Sky = new Sky { Mode = SkyEnvironmentType.Procedural } });
        Add(root, root, new DirectionalLight3D { Name = "Sun", Energy = 1.1f, CastsShadows = true, RotationDegrees = new Vector3(-50, 35, 0) });
        Add(root, root, new MeshInstance3D
        {
            Name = "Floor",
            Mesh = new PlaneMesh { Size = new Vector2(30, 30) },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = Color.FromArgb(92, 104, 96) },
        });

        var model = ResourceLoader.Load<PackedScene>(CharacterPath);
        try
        {
            var crew = model.Instantiate<Node3D>();
            crew.Name = "Crew";
            crew.RotationDegrees = new Vector3(0, 20, 0);
            Add(root, root, crew);

            // Added to the instance: a hammer and a torch ride on the right hand.
            var hand = Add(root, crew.GetNode<Skeleton3D>("Armature"), new BoneAttachment3D { Name = "HandSlot", BoneName = "Hand.R" });
            Add(root, hand, new MeshInstance3D
            {
                Name = "Hammer",
                Position = new Vector3(-0.08f, 0, 0.14f),
                Mesh = new BoxMesh { Size = new Vector3(0.05f, 0.05f, 0.34f) },
                MaterialOverride = new StandardMaterial3D { AlbedoColor = Color.FromArgb(128, 86, 50) },
            });
            Add(root, hand, new OmniLight3D
            {
                Name = "Torch",
                Position = new Vector3(-0.1f, -0.12f, 0.05f),
                Color = new Vector3(1f, 0.62f, 0.25f),
                Energy = 1.6f,
                Range = 3f,
            });
        }
        finally
        {
            model.Release();
        }

        var ui = Add(root, root, new UiLayer { Name = "Ui", Layer = 10 });
        Add(root, ui, new Animation3DPanel { Name = "Panel" });
        return root;
    }
}

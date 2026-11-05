using Color = System.Drawing.Color;
using System.Numerics;
using MainframeEngine;
using static Demo.DemoBuild;

namespace Demo;

/// <summary>
/// A skinned glTF character (ADR 0207: <c>Assets/Models/Character/character.glb</c>, 31 bones, ten clips) played by its
/// imported <see cref="AnimationPlayer"/>, with a hammer and a torch light on its right hand through a
/// <see cref="BoneAttachment3D"/>, under a shadowing sun. A physical skeleton (ADR 0212,
/// <see cref="PhysicalBoneSimulator3D.Build"/>: capsules and cone-twist joints) follows the animation and turns the
/// character into a ragdoll on demand. <see cref="Animation3DPanel"/> picks the clip, the crossfade and the speed, shows
/// the clip's events as they fire, and drops or stands up the ragdoll. Behind it a second instance rides a
/// <see cref="RootMotionWalker"/> body, moved only by root motion (ADR 0210): half circles and forward jumps around a
/// closed loop.
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
        var ground = Add(root, root, new StaticBody3D { Name = "Ground", Position = new Vector3(0, -0.5f, 0) });
        Add(root, ground, new CollisionShape3D { Name = "Shape", Shape = new BoxShape3D { Size = new Vector3(30, 1, 30) } });

        var model = ResourceLoader.Load<PackedScene>(CharacterPath);
        try
        {
            var crew = model.Instantiate<Node3D>();
            crew.Name = "Crew";
            crew.RotationDegrees = new Vector3(0, 20, 0);
            Add(root, root, crew);

            // Added to the instance: a hammer and a torch ride on the right hand.
            var armature = crew.GetNode<Skeleton3D>("Armature");
            var hand = Add(root, armature, new BoneAttachment3D { Name = "HandSlot", BoneName = "Hand.R" });
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

            // Root motion (ADR 0210): a body walking the circle clip and jumping forward, facing −X at the start.
            var walker = Add(root, root, new RootMotionWalker { Name = "Walker", Position = new Vector3(2.2f, 0, -5f), RotationDegrees = new Vector3(0, -90, 0) });
            Add(root, walker, new CollisionShape3D
            {
                Name = "Shape",
                Position = new Vector3(0, 0.9f, 0),
                Shape = new CapsuleShape3D { Radius = 0.3f, Height = 1.8f },
            });
            var walkerModel = model.Instantiate<Node3D>();
            walkerModel.Name = "Model";
            Add(root, walker, walkerModel);
            var walkerPlayer = walkerModel.GetNode<AnimationPlayer>("AnimationPlayer");
            walkerPlayer.CallbackModeProcess = AnimationCallbackMode.Physics;
            walkerPlayer.RootMotionTrack = "Armature";
            walkerPlayer.RootMotionBone = "Hips";
        }
        finally
        {
            model.Release();
        }

        // The physical skeleton, as the editor's "Create physical skeleton" makes it (generic 45°/30° cone-twists), with a
        // hold on the head as well as the pelvis for the active ragdoll (G9.10c): a spine drive alone lets the tall upper
        // body sag. The project runs 2 physics substeps for it.
        var skeleton = root.GetNode<Skeleton3D>("Crew/Armature");
        var simulator = Add(root, skeleton, PhysicalBoneSimulator3D.Build(skeleton));
        foreach (var bone in simulator.Children)
        {
            bone.Owner = root;
            if (bone is PhysicalBone3D { BoneName: "Head" } head)
                head.HoldStrength = 1f;
            // Pin the Euler angles the saver writes, rounded: the quaternion-to-Euler trig differs in the last bit between
            // x64 and arm64, and the committed scene file must match the builder on every platform.
            if (bone is PhysicalBone3D physical)
            {
                var degrees = physical.RotationDegrees;
                physical.RotationDegrees = new Vector3(MathF.Round(degrees.X, 3), MathF.Round(degrees.Y, 3), MathF.Round(degrees.Z, 3));
            }

            foreach (var shape in bone.Children)
                shape.Owner = root;
        }

        BuildWorkshop(root);
        var ui = Add(root, root, new UiLayer { Name = "Ui", Layer = 10 });
        Add(root, ui, new Animation3DPanel { Name = "Panel" });
        return root;
    }

    /// <summary>The workshop animation's length in seconds (it loops).</summary>
    public const float WorkshopLength = 6f;

    // G1a keyframe animation, authored as tracks (the editor's Animation panel edits the same data): a lift that is an
    // AnimatableBody3D animated on the physics tick, a door that unlocks (a method key), swings open and shut and locks
    // again, a flickering lamp (a cubic value track) and a chime (an audio track).
    private static void BuildWorkshop(Node root)
    {
        var workshop = Add(root, root, new Node3D { Name = "Workshop", Position = new Vector3(-1.9f, 0, -3.2f) });
        var lift = Add(root, workshop, new AnimatableBody3D { Name = "Lift" });
        Add(root, lift, new CollisionShape3D { Name = "Shape", Shape = new BoxShape3D { Size = new Vector3(1f, 0.15f, 1f) } });
        Add(root, lift, new MeshInstance3D { Name = "Mesh", Mesh = new BoxMesh { Size = new Vector3(1f, 0.15f, 1f) }, MaterialOverride = new StandardMaterial3D { AlbedoColor = Color.FromArgb(245, 158, 11) } });

        var hinge = Add(root, workshop, new Node3D { Name = "Hinge", Position = new Vector3(0.9f, 0, 0.6f) });
        Add(root, hinge, new MeshInstance3D
        {
            Name = "Door",
            Position = new Vector3(0.4f, 0.9f, 0),
            Mesh = new BoxMesh { Size = new Vector3(0.8f, 1.8f, 0.06f) },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = Color.FromArgb(120, 84, 55) },
        });
        var latch = Add(root, workshop, new DoorLatch { Name = "Latch", Position = new Vector3(1.7f, 1.1f, 0.62f) });
        DoorLatch.Build(root, latch);
        Add(root, workshop, new OmniLight3D { Name = "Lamp", Position = new Vector3(0.5f, 2.2f, 0.2f), Energy = 1.5f, Range = 4f, Color = new Vector3(1f, 0.85f, 0.6f) });
        Add(root, workshop, new AudioPlayer3D { Name = "Chime", Position = new Vector3(0.5f, 1.5f, 0f) });

        var animation = new MainframeEngine.Animation { ResourceName = "workshop", Length = WorkshopLength, LoopMode = AnimationLoopMode.Linear };
        animation.AddTrack(new Position3DTrack
        {
            Path = "Lift",
            Times = [0f, 0.5f, 2.5f, 3.5f, 5.5f],
            Values = [Vector3.Zero, Vector3.Zero, new Vector3(0, 1.4f, 0), new Vector3(0, 1.4f, 0), Vector3.Zero],
            Transitions = [1f, -2f, 1f, -2f, 1f], // ease in-out on the moving segments
        });
        animation.AddTrack(new Rotation3DTrack
        {
            Path = "Hinge",
            Times = [0f, 1.5f, 2.3f, 4.2f, 5f],
            Values = [Quaternion.Identity, Quaternion.Identity, Quaternion.CreateFromAxisAngle(Vector3.UnitY, -1.6f), Quaternion.CreateFromAxisAngle(Vector3.UnitY, -1.6f), Quaternion.Identity],
            Interpolation = AnimationInterpolation.Cubic,
            LoopWrap = false,
        });
        animation.AddTrack(new MethodTrack { Path = "Latch", Times = [1.4f, 5.1f], Methods = ["Unlock", "Lock"], Arguments = ["[]", "[]"] });
        animation.AddTrack(new ValueTrack
        {
            Path = "Lamp",
            Property = "Energy",
            Times = [0f, 0.4f, 0.55f, 0.7f, 2f, 3.1f, 3.2f, 3.35f, 4.5f],
            Values = [1.5f, 1.4f, 0.3f, 1.6f, 1.5f, 1.45f, 0.4f, 1.5f, 1.5f],
            Interpolation = AnimationInterpolation.Cubic,
        });
        animation.AddTrack(new AudioTrack
        {
            Path = "Chime",
            Times = [2.3f],
            Streams = [new ZzfxStream { ResourceName = "Chime", Parameters = ZzfxPresets.Coin(new Random(7)) }],
            StartOffsets = [0f],
        });
        var library = new AnimationLibrary();
        library.Animations.Add(animation);
        var player = Add(root, workshop, new AnimationPlayer
        {
            Name = "WorkshopPlayer",
            CallbackModeProcess = AnimationCallbackMode.Physics, // the lift is a body: it moves on the physics tick
            Autoplay = "workshop",
        });
        player.Libraries.Add(library);
    }
}

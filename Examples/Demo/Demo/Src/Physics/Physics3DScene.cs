using Color = System.Drawing.Color;
using System.Drawing;
using System.Numerics;
using MainframeEngine;
using static Demo.DemoBuild;

namespace Demo;

/// <summary>Jitter2 rigid bodies: a crate stack, a ramp, a character body that shoves, and click-to-drop crates.</summary>
public static class Physics3DScene
{
    public static Node Build()
    {
        var root = new Node3D { Name = "physics_3d" };
        var camera = Add(root, root, new Camera3D { Name = "Camera", Current = true, Position = new Vector3(0, 6, 13) });
        camera.LookAt(new Vector3(0, 1.5f, 0));
        camera.RotationDegrees = camera.RotationDegrees; // pin the Euler angles the saver writes
        Add(root, root, new WorldEnvironment { Name = "Environment", Sky = new Sky { Mode = SkyEnvironmentType.Procedural } });
        Add(root, root, new DirectionalLight3D { Name = "Sun", Energy = 1.2f, CastsShadows = true, RotationDegrees = new Vector3(-55, -30, 0) });

        var floor = Add(root, root, new StaticBody3D { Name = "Floor", Position = new Vector3(0, -0.5f, 0) });
        Add(root, floor, new CollisionShape3D { Name = "Shape", Shape = new BoxShape3D { Size = new Vector3(30, 1, 16) } });
        Add(root, floor, new MeshInstance3D { Name = "Visual", Mesh = new BoxMesh { Size = new Vector3(30, 1, 16) },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = Color.FromArgb(78, 90, 112) } });

        var ramp = Add(root, root, new StaticBody3D { Name = "Ramp", Position = new Vector3(5.5f, 1f, 0), RotationDegrees = new Vector3(0, 0, 18) });
        Add(root, ramp, new CollisionShape3D { Name = "Shape", Shape = new BoxShape3D { Size = new Vector3(6, 0.3f, 4) } });
        Add(root, ramp, new MeshInstance3D { Name = "Visual", Mesh = new BoxMesh { Size = new Vector3(6, 0.3f, 4) },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = Color.FromArgb(100, 116, 139) } });

        var pusher = Add(root, root, new Pusher3D { Name = "Pusher", Position = new Vector3(-6, 1.05f, 0), CollisionMask = CollisionLayers.Default });
        Add(root, pusher, new CollisionShape3D { Name = "Shape", Shape = new CapsuleShape3D { Radius = 0.5f, Height = 2f } });
        Add(root, pusher, new MeshInstance3D { Name = "Visual", Mesh = new CapsuleMesh { Radius = 0.5f, Height = 2f },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = Color.FromArgb(226, 232, 240) } });

        Add(root, root, new Dropper3D { Name = "Crates" });
        var ui = Add(root, root, new UiLayer { Name = "Ui", Layer = 10 });
        Add(root, ui, new Physics3DPanel { Name = "Panel" });
        return root;
    }
}

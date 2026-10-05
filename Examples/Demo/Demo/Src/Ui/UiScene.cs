using System.Drawing;
using System.Numerics;
using MainframeEngine;
using static Demo.DemoBuild;

namespace Demo;

/// <summary>Game UI (RmlUi): a widget gallery with data binding, and a live counter of UI hot reloads.</summary>
public static class UiScene
{
    public static Node Build()
    {
        var root = new Node3D { Name = "ui" };
        var camera = Add(root, root, new Camera3D { Name = "Camera", Current = true, Position = new Vector3(0, 3.5f, 9) });
        camera.LookAt(new Vector3(0, 0.5f, 0));
        camera.RotationDegrees = camera.RotationDegrees; // pin the Euler angles the saver writes
        Add(root, root, new WorldEnvironment { Name = "Environment", Sky = new Sky { Mode = SkyEnvironmentType.Procedural } });
        Add(root, root, new DirectionalLight3D { Name = "Sun", Energy = 1.4f, CastsShadows = true, RotationDegrees = new Vector3(-50, 30, 0) });
        Add(root, root, new MeshInstance3D
        {
            Name = "Floor",
            Mesh = new PlaneMesh { Size = new Vector2(40, 40) },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = Color.FromArgb(58, 68, 92) },
        });
        Add(root, root, new MeshInstance3D
        {
            Name = "Cube",
            Mesh = new BoxMesh { Size = new Vector3(1.2f, 1.2f, 1.2f) },
            Position = new Vector3(-1.4f, 0.6f, 0), RotationDegrees = new Vector3(0, 30, 0),
            MaterialOverride = new StandardMaterial3D { AlbedoColor = Color.FromArgb(37, 99, 235) },
        });
        Add(root, root, new MeshInstance3D
        {
            Name = "Ball",
            Mesh = new SphereMesh { Radius = 0.6f, Height = 1.2f },
            Position = new Vector3(1.4f, 0.6f, 0),
            MaterialOverride = new StandardMaterial3D { AlbedoColor = Color.FromArgb(225, 29, 72) },
        });
        var ui = Add(root, root, new UiLayer { Name = "Ui", Layer = 10 });
        Add(root, ui, new UiShowcase { Name = "Showcase" });
        return root;
    }
}

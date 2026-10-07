using Color = System.Drawing.Color;
using System.Drawing;
using System.Numerics;
using MainframeEngine;
using static Demo.DemoBuild;

namespace Demo;

/// <summary>
/// SpineBoy drawn through the 3D pipeline, lit and shadow-casting (onto a backdrop wall behind him), seen by a Camera3D
/// or (the panel's toggle) a Camera2D. Both cameras live in the scene; <see cref="SpinePanel"/> switches between them.
/// </summary>
public static class SpineScene
{
    public static Node Build()
    {
        var root = new Node3D { Name = "spine" };
        var camera3d = Add(root, root, new Camera3D { Name = "Camera3D", Current = true, Position = new Vector3(0, 1.5f, 7) });
        camera3d.LookAt(new Vector3(0, 1.3f, 0));
        camera3d.RotationDegrees = camera3d.RotationDegrees; // pin the Euler angles the saver writes
        Add(root, root, new Camera2D { Name = "Camera2D", Current = false, Enabled = false });
        Add(root, root, new WorldEnvironment { Name = "Environment", Sky = new Sky { Mode = SkyEnvironmentType.Procedural } });
        Add(root, root, new DirectionalLight3D { Name = "Sun", Energy = 1.2f, CastsShadows = true, RotationDegrees = SpinePanel.SunRotation3D });
        Add(root, root, new MeshInstance3D
        {
            Name = "Floor",
            Mesh = new PlaneMesh { Size = new Vector2(30, 30) },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = Color.FromArgb(78, 90, 112) }
        });
        // The flat skeleton's floor shadow hides behind it from the camera; the wall catches it where it shows.
        Add(root, root, new MeshInstance3D
        {
            Name = "Backdrop",
            Mesh = new BoxMesh { Size = new Vector3(14, 6, 0.2f) },
            Position = new Vector3(0, 3, SpinePanel.BackdropZ),
            MaterialOverride = new StandardMaterial3D { AlbedoColor = Color.FromArgb(196, 202, 214) }
        });

        var pivot = Add(root, root, new Node3D { Name = "Pivot" });
        Add(root, pivot, new SpineNode { Name = "SpineBoy", Folder = "Content/Models/Spine/SpineBoy", Animation = "walk", SpineScale = SpinePanel.Scale3D });

        var ui = Add(root, root, new UiLayer { Name = "Ui", Layer = 10 });
        Add(root, ui, new SpinePanel { Name = "Panel" });
        return root;
    }
}

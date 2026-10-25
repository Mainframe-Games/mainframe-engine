using System.Numerics;
using MainframeEngine;
using static Demo.DemoBuild;
using Color = System.Drawing.Color;

namespace Demo;

/// <summary>
/// Godot spatial shaders on meshes (ADR 0205): a procedural ground computed from the world position
/// (<c>Assets/Shaders/ground.gdshader</c>), fungal stalks in one <see cref="MultiMeshInstance3D"/> swaying with their shadows
/// (<c>sway.gdshader</c>, out of step by <c>INSTANCE_CUSTOM</c>), and props dissolving with a glowing edge and a hit flash
/// (<c>dissolve.gdshader</c>), under a shadowed sun and a warm omni light. <see cref="ShadersPanel"/> drives the uniforms.
/// </summary>
public static class ShadersScene
{
    /// <summary>The dissolving props: name, mesh, position, colour (sRGB), phase of the dissolve cycle.</summary>
    public static readonly (string Name, Func<Mesh> Mesh, Vector3 Position, Color Color, float Phase)[] Props =
    [
        ("Crate", () => new BoxMesh { Size = new Vector3(1.3f) }, new Vector3(1.2f, 0.65f, 0.6f), Color.FromArgb(196, 140, 84), 0f),
        ("Pod", () => new SphereMesh { Radius = 0.8f, Height = 1.6f, RadialSegments = 48, Rings = 24 }, new Vector3(3.4f, 0.8f, -1.2f), Color.FromArgb(110, 150, 220), 0.33f),
        ("Pillar", () => new CylinderMesh { TopRadius = 0.45f, BottomRadius = 0.55f, Height = 2.4f, RadialSegments = 32 }, new Vector3(-0.6f, 1.2f, -2.4f), Color.FromArgb(170, 176, 190), 0.66f),
    ];

    public static Node Build()
    {
        var root = new Node3D { Name = "shaders" };
        var camera = Add(root, root, new Camera3D { Name = "Camera", Current = true, Position = new Vector3(0.5f, 4.6f, 10.5f), Fov = 50f });
        camera.LookAt(new Vector3(0.5f, 0.8f, -0.5f));
        camera.RotationDegrees = camera.RotationDegrees; // pin the Euler angles the saver writes
        Add(root, root, new WorldEnvironment { Name = "Environment", Sky = new Sky { Mode = SkyEnvironmentType.Procedural } });
        var sun = Add(root, root, new DirectionalLight3D { Name = "Sun", Color = new Vector3(1f, 0.95f, 0.86f), Energy = 1.4f, CastsShadows = true });
        sun.RotationDegrees = new Vector3(-50, -35, 0);
        Add(root, root, new OmniLight3D { Name = "Lamp", Color = new Vector3(1f, 0.6f, 0.3f), Energy = 3f, Range = 6f, Position = new Vector3(2.6f, 1.6f, 2.4f) });

        // The ground: one material whose albedo is a function of the world position only.
        Add(root, root, new MeshInstance3D
        {
            Name = "Ground",
            Mesh = new PlaneMesh { Size = new Vector2(60, 60) },
            MaterialOverride = new ShaderMaterial { Shader = ResourceLoader.Load<Shader>("Assets/Shaders/ground.gdshader") },
        });

        // Stalks: one multimesh, each instance with a phase and a cap colour as custom data.
        var multimesh = new MultiMesh
        {
            Mesh = new CylinderMesh { TopRadius = 0.07f, BottomRadius = 0.12f, Height = 2.4f, RadialSegments = 12, Rings = 10, CapTop = false, CapBottom = false },
            UseCustomData = true,
            InstanceCount = 14,
        };
        Vector3[] caps = [new(0.95f, 0.35f, 0.65f), new(0.35f, 0.8f, 0.95f), new(0.98f, 0.82f, 0.3f)];
        for (var i = 0; i < multimesh.InstanceCount; i++)
        {
            var position = new Vector3(-6.4f + i * 0.42f, 1.2f, -2.6f + (i * 7 % 5) * 0.55f);
            multimesh.SetInstanceTransform(i, new Transform3D(Basis.Identity, position));
            var cap = caps[i % caps.Length];
            multimesh.SetInstanceCustomData(i, new Vector4(i * 0.137f % 1f, cap.X, cap.Y, cap.Z));
        }

        Add(root, root, new MultiMeshInstance3D
        {
            Name = "Stalks",
            Multimesh = multimesh,
            MaterialOverride = new ShaderMaterial { Shader = ResourceLoader.Load<Shader>("Assets/Shaders/sway.gdshader") },
        });

        // Props: one dissolve material each (their progress differs).
        var dissolve = ResourceLoader.Load<Shader>("Assets/Shaders/dissolve.gdshader");
        var props = Add(root, root, new Node3D { Name = "Props" });
        foreach (var (name, mesh, position, color, _) in Props)
        {
            var material = new ShaderMaterial { Shader = dissolve };
            material.SetShaderParameter("base_color", new Vector4(color.R / 255f, color.G / 255f, color.B / 255f, 1f));
            Add(root, props, new MeshInstance3D { Name = name, Mesh = mesh(), Position = position, MaterialOverride = material });
        }

        var ui = Add(root, root, new UiLayer { Name = "Ui", Layer = 10 });
        Add(root, ui, new ShadersPanel { Name = "Panel" });
        return root;
    }
}

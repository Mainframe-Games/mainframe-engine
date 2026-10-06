using System.Drawing;
using System.Numerics;

namespace MainframeEngine.RenderTests.Host.Scenes;

using Color = System.Drawing.Color;

/// <summary>
/// M6 physics: boxes dropped onto a static floor and a ramp (Jitter2), lit by one shadow-casting light. The physics
/// server uses Jitter2's deterministic solver on one thread so the simulation (and therefore frame N) is identical run to
/// run; the fixed frame delta equals the physics step, so every frame runs exactly one step. <see cref="PhysicsDebugScene"/> adds the collision-shape debug draw.
/// </summary>
public class PhysicsScene(HostOptions host) : RenderTestGame(host)
{
    private static readonly Color[] Palette =
    [
        Color.FromArgb(255, 230, 120, 80), Color.FromArgb(255, 90, 160, 230), Color.FromArgb(255, 240, 200, 90),
        Color.FromArgb(255, 120, 200, 120), Color.FromArgb(255, 200, 110, 200),
    ];

    /// <summary>Draw collision shapes too.</summary>
    protected virtual bool DebugDraw => false;

    protected override void LoadScene()
    {
        var physics = Servers.Get<PhysicsServer3D>()!;
        physics.Settings.MultiThreaded = false; // reproducible frames: single thread, island solver
        physics.Settings.Deterministic = true;
        physics.DebugDrawEnabled = DebugDraw;

        var scene = new Node3D { Name = "Physics" };
        var camera = new Camera3D { Name = "Camera", Position = new Vector3(0, 4f, 9f) };
        camera.LookAt(new Vector3(0, 1f, 0));
        scene.AddChild(camera);
        scene.AddChild(new WorldEnvironment { Name = "Environment", Sky = new Sky { Mode = SkyEnvironmentType.Procedural } });
        var sun = new DirectionalLight3D { Name = "Sun", Position = new Vector3(0, 6, 0), Color = new Vector3(1f, 0.95f, 0.8f), Energy = 0.9f };
        sun.LookAt(sun.Position + Vector3.Normalize(new Vector3(-0.4f, -1f, -0.5f)));
        scene.AddChild(sun);

        // Static floor: a visual plane and a box collider whose top face is y = 0.
        var floor = new StaticBody3D { Name = "Floor" };
        floor.AddChild(new CollisionShape3D { Name = "Shape", Position = new Vector3(0, -0.5f, 0), Shape = new BoxShape3D { Size = new Vector3(12, 1, 12) } });
        floor.AddChild(new MeshInstance3D { Name = "Visual", Mesh = new PlaneMesh { Size = new Vector2(12, 12) } });
        scene.AddChild(floor);

        // A ramp the boxes tumble down, and a pile of boxes dropped from different heights and angles.
        var ramp = new StaticBody3D { Name = "Ramp", Position = new Vector3(2.5f, 0.6f, -1f), RotationDegrees = new Vector3(0, 0, 20) };
        ramp.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(3, 0.2f, 2) } });
        ramp.AddChild(new MeshInstance3D
        {
            Name = "Visual",
            Mesh = new BoxMesh { Size = new Vector3(3, 0.2f, 2) },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = Color.FromArgb(255, 150, 150, 160) },
        });
        scene.AddChild(ramp);

        var cube = new BoxShape3D(); // one shared shape resource
        var crateMesh = new BoxMesh { Size = new Vector3(0.98f) }; // one mesh, one material per palette colour: few batches
        var crateMaterials = new StandardMaterial3D[Palette.Length];
        for (var i = 0; i < Palette.Length; i++)
            crateMaterials[i] = new StandardMaterial3D { AlbedoColor = Palette[i] };
        for (var i = 0; i < 10; i++)
        {
            var body = new RigidBody3D
            {
                Name = $"Crate{i}",
                Position = new Vector3(-2.2f + i % 5 * 1.1f + (i / 5) * 0.4f, 1.2f + i * 0.55f, (i % 3 - 1) * 0.6f),
                RotationDegrees = new Vector3(i * 17 % 45, i * 31 % 90, i * 13 % 30),
            };
            body.AddChild(new CollisionShape3D { Shape = cube });
            body.AddChild(new MeshInstance3D { Name = "Visual", Mesh = crateMesh, MaterialOverride = crateMaterials[i % Palette.Length] });
            scene.AddChild(body);
        }

        var ball = new RigidBody3D { Name = "Ball", Position = new Vector3(3.2f, 3.5f, -1f) };
        ball.AddChild(new CollisionShape3D { Shape = new SphereShape3D { Radius = 0.35f } });
        ball.AddChild(new MeshInstance3D
        {
            Name = "Visual",
            Mesh = new BoxMesh { Size = new Vector3(0.5f) },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = Color.FromArgb(255, 250, 250, 250) },
        });
        scene.AddChild(ball);

        Tree.ChangeScene(scene);
    }

    protected override void UpdateScene(in GameTime gameTime)
    {
    }

    protected override void DisposeScene()
    {
    }
}

/// <summary><see cref="PhysicsScene"/> with the collision shapes drawn (<see cref="PhysicsServer3D.DebugDrawEnabled"/>).</summary>
public sealed class PhysicsDebugScene(HostOptions host) : PhysicsScene(host)
{
    protected override bool DebugDraw => true;
}

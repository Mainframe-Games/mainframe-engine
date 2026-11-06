using System.Numerics;

namespace MainframeEngine.RenderTests.Host.Scenes;

/// <summary>
/// <see cref="UiMarker3D"/>s on the engine's marker document (G11.4, ADR 0250): three nameplates over posts (one with a
/// title, bars full, half and a quarter), a health bar, an interaction prompt over a crate, a waypoint ahead and one
/// off-screen to the right that clamps to the edge with its arrow, and a nameplate behind a wall hidden by physics
/// occlusion. The bars' fill is <c>#3366cc</c>, checked exact after the tonemap.
/// </summary>
public sealed class UiMarkersScene(HostOptions host) : ShadowSceneBase(host)
{
    protected override void Build(Node3D scene)
    {
        Camera.Position = new Vector3(0, 2.4f, 8f);
        Camera.LookAt(new Vector3(0, 1.2f, 0));
        scene.AddChild(new MeshInstance3D { Name = "Floor", Mesh = new PlaneMesh { Size = new Vector2(40, 40) }, MaterialOverride = Plain(205, 200, 190) });
        scene.AddChild(Sun(new Vector3(0.3f, -0.8f, -0.5f)));

        foreach (var (name, title, x, z, value) in new[] { ("Nell", "Trader", -3f, 0f, 1f), ("Whitlock", "", 0f, -2f, 0.5f), ("Ada", "", 3f, 1f, 0.25f) })
        {
            var post = new MeshInstance3D
            {
                Name = name + "Post",
                Position = new Vector3(x, 0.9f, z),
                Mesh = new CylinderMesh { TopRadius = 0.25f, BottomRadius = 0.25f, Height = 1.8f },
                MaterialOverride = Plain(150, 110, 80),
            };
            post.AddChild(new UiMarker3D { Name = name, Text = name, Subtext = title, Value = value, WorldOffset = new Vector3(0, 1.2f, 0) });
            scene.AddChild(post);
        }

        var crate = new MeshInstance3D { Name = "Crate", Position = new Vector3(-1.4f, 0.4f, 3f), Mesh = new BoxMesh { Size = new Vector3(0.8f, 0.8f, 0.8f) }, MaterialOverride = Plain(170, 130, 70) };
        crate.AddChild(new UiMarker3D { Name = "Prompt", Kind = "prompt", Subtext = "E", Text = "Open", WorldOffset = new Vector3(0, 0.6f, 0) });
        crate.AddChild(new UiMarker3D { Name = "CrateHealth", Kind = "health", Value = 0.7f, WorldOffset = new Vector3(0, -0.2f, 0.5f) });
        scene.AddChild(crate);

        scene.AddChild(new UiMarker3D { Name = "Gate", Kind = "waypoint", Text = "Gate", Position = new Vector3(1.5f, 0.5f, -14f), WorldOffset = Vector3.Zero, MaxDistance = 0 });
        scene.AddChild(new UiMarker3D
        {
            Name = "Camp",
            Kind = "waypoint",
            Text = "Camp",
            Position = new Vector3(30f, 1f, 4f),
            WorldOffset = Vector3.Zero,
            MaxDistance = 0,
            Offscreen = MarkerOffscreen.ClampToEdge,
        });

        // Behind a wall with a collider: occluded, so not drawn.
        var wall = new StaticBody3D { Name = "Wall", Position = new Vector3(1.7f, 1f, 0f) };
        wall.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(0.9f, 2f, 0.3f) } });
        wall.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.9f, 2f, 0.3f) }, MaterialOverride = Plain(60, 90, 170) });
        scene.AddChild(wall);
        scene.AddChild(new UiMarker3D
        {
            Name = "Hidden",
            Text = "Hidden",
            Position = new Vector3(1.7f, 1.2f, -1.5f),
            WorldOffset = Vector3.Zero,
            Occlusion = MarkerOcclusion.Physics,
        });

        var layer = new UiLayer { Name = "Hud", Layer = 0 };
        layer.AddChild(new UiMarkerHost { Name = "Markers" });
        scene.AddChild(layer);
    }
}

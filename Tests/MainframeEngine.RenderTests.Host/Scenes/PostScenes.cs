using System.Numerics;

namespace MainframeEngine.RenderTests.Host.Scenes;

using Color = System.Drawing.Color;

/// <summary>
/// FXAA (ADR 0154): an unshaded white box turned so its silhouette edges are oblique, over the dark clear colour, and an
/// opaque red screen-gizmo square (overlay pass) on the background beside it. <c>--count</c>: 0 FXAA, 1 no
/// anti-aliasing. FXAA must soften the box's edges and leave the gizmo (drawn after it) exact.
/// </summary>
public sealed class FxaaScene(HostOptions host) : RenderTestGame(host)
{
    /// <summary>The gizmo square in layout points (× the scale in pixels).</summary>
    public static readonly (Vector2 Min, Vector2 Max) GizmoRect = (new Vector2(16, 16), new Vector2(56, 56));
    public static readonly Vector4 GizmoColor = new(1f, 0f, 0f, 1f);

    protected override void LoadScene()
    {
        Vulkan.AntiAliasing = Host.Count == 1 ? AntiAliasing.None : AntiAliasing.Fxaa;
        var scene = new Node3D { Name = nameof(FxaaScene) };
        scene.AddChild(new Camera3D { Name = "Camera", Position = new Vector3(0f, 0f, 3f) });
        scene.AddChild(new MeshInstance3D
        {
            Name = "Box",
            RotationDegrees = new Vector3(17f, 31f, 23f),
            Mesh = new BoxMesh(),
            MaterialOverride = new StandardMaterial3D { AlbedoColor = Color.White, ShadingMode = ShadingMode.Unshaded },
        });
        Tree.ChangeScene(scene);
    }

    protected override void UpdateScene(in GameTime gameTime)
    {
        // The gizmo batch is cleared after every drawn frame, so the square is re-added each update.
        Servers.Render!.ScreenGizmos.FilledRect(GizmoRect.Min * Host.Scale, GizmoRect.Max * Host.Scale, GizmoColor);
    }

    protected override void DisposeScene()
    {
    }
}

/// <summary>
/// Auto exposure (ADR 0154): a lit floor and boxes, a <see cref="WorldEnvironment"/> with auto exposure (speed 2/s) and
/// no ambient light. The sun has energy 3 until <see cref="DimFrame"/>, then 0.2: the frame after the drop is dark, and
/// the exposure then rises over the following frames (1/60 s each).
/// </summary>
public sealed class AutoExposureScene(HostOptions host) : RenderTestGame(host)
{
    public const uint DimFrame = 20;
    public const float BrightEnergy = 3f;
    public const float DimEnergy = 0.2f;

    private DirectionalLight3D _sun = null!;

    protected override void LoadScene()
    {
        var scene = new Node3D { Name = nameof(AutoExposureScene) };
        var camera = new Camera3D { Name = "Camera", Position = new Vector3(0f, 2.5f, 6f) };
        camera.Rotation = Transform3D.BasisLookingAlong(new Vector3(0f, -0.35f, -1f), Vector3.UnitY).GetRotation();
        scene.AddChild(camera);
        scene.AddChild(new MeshInstance3D
        {
            Name = "Floor",
            Mesh = new PlaneMesh { Size = new Vector2(40f, 40f) },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = Color.FromArgb(255, 150, 140, 120) },
        });
        for (var i = 0; i < 3; i++)
        {
            scene.AddChild(new MeshInstance3D
            {
                Name = $"Box{i}",
                Position = new Vector3(-2f + 2f * i, 0.5f, -i * 0.5f),
                RotationDegrees = new Vector3(0f, 20f * i, 0f),
                Mesh = new BoxMesh(),
                MaterialOverride = new StandardMaterial3D { AlbedoColor = Color.FromArgb(255, 200, 80 + 50 * i, 60) },
            });
        }

        _sun = new DirectionalLight3D { Name = "Sun", Energy = BrightEnergy };
        _sun.Rotation = Transform3D.BasisLookingAlong(new Vector3(-0.4f, -1f, -0.5f), Vector3.UnitY).GetRotation();
        scene.AddChild(_sun);
        scene.AddChild(new WorldEnvironment
        {
            Name = "Environment",
            AmbientColor = new Vector3(0.05f, 0.05f, 0.05f),
            AutoExposureEnabled = true,
            AutoExposureSpeed = 2f,
        });
        Tree.ChangeScene(scene);
    }

    protected override void UpdateScene(in GameTime gameTime)
    {
        if (gameTime.FrameCount == DimFrame)
            _sun.Energy = DimEnergy;
    }

    protected override void DisposeScene()
    {
    }
}

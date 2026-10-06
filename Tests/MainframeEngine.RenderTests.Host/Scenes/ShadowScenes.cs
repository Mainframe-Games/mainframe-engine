using System.Drawing;
using System.Globalization;
using System.Numerics;

namespace MainframeEngine.RenderTests.Host.Scenes;

/// <summary>Shared set-up for the M4 shadow scenes: a camera, a procedural sky, a plain floor and helpers.</summary>
public abstract class ShadowSceneBase(HostOptions host) : RenderTestGame(host)
{
    protected Camera3D Camera { get; private set; } = null!;
    protected Node3D Scene { get; private set; } = null!;
    protected ShadowSystem? Shadows => Servers.Render?.ExistingShadows;
    protected WorldEnvironment Environment { get; private set; } = null!;

    protected static StandardMaterial3D Plain(int r, int g, int b, float specular = 0.1f) =>
        new() { AlbedoColor = Color.FromArgb(255, r, g, b), Specular = specular };

    protected override void LoadScene()
    {
        Scene = new Node3D { Name = GetType().Name };
        Camera = new Camera3D { Name = "Camera" };
        Scene.AddChild(Camera);
        Environment = new WorldEnvironment { Name = "Environment", Sky = HasSky ? new Sky { Mode = SkyEnvironmentType.Procedural } : null };
        Scene.AddChild(Environment);
        Build(Scene);
        Tree.ChangeScene(Scene);
    }

    protected virtual bool HasSky => true;

    protected abstract void Build(Node3D scene);

    protected override void UpdateScene(in GameTime gameTime)
    {
    }

    protected override void DisposeScene()
    {
    }

    protected static DirectionalLight3D Sun(Vector3 direction, float energy = 0.9f)
    {
        var sun = new DirectionalLight3D { Name = "Sun", Position = new Vector3(0, 10, 0), Color = new Vector3(1f, 0.96f, 0.88f), Energy = energy };
        sun.LookAt(sun.Position + Vector3.Normalize(direction));
        return sun;
    }

    protected static T Aim<T>(T light, Vector3 direction) where T : Node3D
    {
        light.LookAt(light.Position + Vector3.Normalize(direction));
        return light;
    }
}

/// <summary>
/// Cascaded shadow maps: a 220-unit floor lined with posts receding into the distance under a low sun, seen from a
/// slightly tilted camera, so all four cascades are on screen. Frame 6 is the normal view; frame 12 tints the
/// cascades (red, green, blue, yellow) with <see cref="ShadowSystem.DebugCascades"/>.
/// </summary>
public sealed class CsmScene(HostOptions host) : ShadowSceneBase(host)
{
    public const uint NormalFrame = 6;
    public const uint DebugFrame = 12;

    protected override void Build(Node3D scene)
    {
        Camera.Position = new Vector3(0, 2.2f, 4f);
        Camera.LookAt(new Vector3(0, 0.6f, -14f));

        scene.AddChild(new MeshInstance3D
        {
            Name = "Floor",
            Position = new Vector3(0, 0, -100),
            Mesh = new PlaneMesh { Size = new Vector2(16, 220) },
            MaterialOverride = Plain(205, 200, 190),
        });

        var post = new BoxMesh { Size = new Vector3(0.25f, 2.4f, 0.25f) };
        var wood = Plain(200, 120, 70);
        for (var i = 0; i < 34; i++)
        {
            var z = 2f - i * 6f;
            foreach (var x in new[] { -3.2f, 3.2f })
            {
                scene.AddChild(new MeshInstance3D
                {
                    Name = string.Create(CultureInfo.InvariantCulture, $"Post{i}{(x < 0 ? "L" : "R")}"),
                    Position = new Vector3(x, 1.2f, z),
                    Mesh = post,
                    MaterialOverride = wood,
                });
            }
        }

        scene.AddChild(Sun(new Vector3(0.85f, -0.75f, 0.2f)));
    }

    protected override void UpdateScene(in GameTime gameTime)
    {
        if (gameTime.FrameCount == DebugFrame - 3 && Shadows is { } shadows)
            shadows.DebugCascades = true;
    }
}

/// <summary>
/// Close-up of a box's shadow edge (top-down) to measure the penumbra: <c>--count</c> picks the filter (0 Poisson 16,
/// the default; 1 hard; 2 3×3).
/// </summary>
public sealed class ShadowPcfScene(HostOptions host) : ShadowSceneBase(host)
{
    protected override bool HasSky => false;

    protected override void Build(Node3D scene)
    {
        Camera.Position = new Vector3(1.2f, 1.6f, 0.01f);
        Camera.LookAt(new Vector3(1.2f, 0f, 0f));

        scene.AddChild(new MeshInstance3D { Name = "Floor", Mesh = new PlaneMesh { Size = new Vector2(20, 20) }, MaterialOverride = Plain(210, 210, 210, 0f) });
        scene.AddChild(new MeshInstance3D
        {
            Name = "Box",
            Position = new Vector3(0, 0.5f, 0),
            RotationDegrees = new Vector3(0, 25f, 0),
            Mesh = new BoxMesh(),
            MaterialOverride = Plain(90, 140, 220),
        });
        var sun = Sun(new Vector3(1f, -1.5f, 0.15f));
        sun.ShadowResolution = 1024; // texels of about a centimetre: a few pixels each, so the filters differ clearly
        scene.AddChild(sun);
    }

    protected override void UpdateScene(in GameTime gameTime)
    {
        if (Shadows is { } shadows) // every frame: the shadow system is created on first use
        {
            shadows.Filter = Host.Count switch
            {
                1 => ShadowFilter.Hard,
                2 => ShadowFilter.Pcf3x3,
                _ => ShadowFilter.Poisson16,
            };
        }
    }
}

/// <summary>
/// Every shadow type casting at once (GitHub issue #2): a sun (cascades), three spot lights (atlas tiles) and two
/// point lights (cubes) over boxes, a column and a sphere. Self-checks that every pass rendered with its own matrix and
/// that the atlas tiles are disjoint.
/// </summary>
public sealed class ShadowLightsScene(HostOptions host) : ShadowSceneBase(host)
{
    private const uint CheckFrame = 10, OverlayCheckFrame = 19; // --count 2 resizes on frame 20
    private bool _checked, _overlayChecked;

    protected override void Build(Node3D scene)
    {
        Camera.Position = new Vector3(0, 6f, 9f);
        Camera.LookAt(new Vector3(0, 0.3f, 0));
        scene.AddChild(new Grid3D { Name = "Grid" });
        scene.AddChild(new MeshInstance3D { Name = "Floor", Mesh = new PlaneMesh { Size = new Vector2(14, 14) }, MaterialOverride = Plain(215, 210, 200) });

        // --count 2 shows the dev overlay with only the Shadows panel open: its depth images (cascade layers, atlas) are
        // sampled in their read-only depth layout and must be validation-clean.
        if (Host.Count == 2)
        {
            DevOverlayVisible = true;
            foreach (var panel in DevOverlay!.Panels)
                panel.Expanded = panel.Id == "shadows";
        }

        var box = new BoxMesh();
        scene.AddChild(new MeshInstance3D { Name = "BoxA", Position = new Vector3(-2.5f, 0.5f, 0.5f), RotationDegrees = new Vector3(0, 30, 0), Mesh = box, MaterialOverride = Plain(230, 120, 80) });
        scene.AddChild(new MeshInstance3D { Name = "BoxB", Position = new Vector3(2.5f, 0.5f, 1f), Mesh = box, MaterialOverride = Plain(90, 160, 230) });
        scene.AddChild(new MeshInstance3D { Name = "Column", Position = new Vector3(0, 1.25f, -1.5f), Mesh = new CylinderMesh { TopRadius = 0.3f, BottomRadius = 0.35f, Height = 2.5f }, MaterialOverride = Plain(220, 215, 200) });
        scene.AddChild(new MeshInstance3D { Name = "Ball", Position = new Vector3(0.2f, 0.5f, 2f), Mesh = new SphereMesh(), MaterialOverride = Plain(120, 210, 120) });

        Environment.AmbientColor = new Vector3(0.1f, 0.1f, 0.12f);
        scene.AddChild(Sun(new Vector3(-0.6f, -1f, -0.35f), energy: 0.35f));
        scene.AddChild(Aim(new SpotLight3D { Name = "SpotRed", Position = new Vector3(-4f, 4.5f, 3f), Color = new Vector3(1f, 0.45f, 0.35f), Energy = 1.2f, Range = 14f, InnerConeAngle = 20f, OuterConeAngle = 32f }, new Vector3(0.7f, -1f, -0.5f)));
        scene.AddChild(Aim(new SpotLight3D { Name = "SpotGreen", Position = new Vector3(4f, 4.5f, 2.5f), Color = new Vector3(0.45f, 1f, 0.5f), Energy = 1.1f, Range = 14f, InnerConeAngle = 18f, OuterConeAngle = 30f, ShadowResolution = 512 }, new Vector3(-0.8f, -1f, -0.4f)));
        scene.AddChild(Aim(new SpotLight3D { Name = "SpotBlue", Position = new Vector3(0f, 5f, -4.5f), Color = new Vector3(0.45f, 0.6f, 1f), Energy = 1.1f, Range = 14f, InnerConeAngle = 20f, OuterConeAngle = 34f }, new Vector3(0f, -1f, 0.75f)));
        scene.AddChild(new OmniLight3D { Name = "LampWarm", Position = new Vector3(-1.2f, 1.4f, 1.6f), Color = new Vector3(1f, 0.75f, 0.4f), Energy = 1.2f, Range = 6f });
        scene.AddChild(new OmniLight3D { Name = "LampCool", Position = new Vector3(1.6f, 1.2f, -0.2f), Color = new Vector3(0.4f, 0.7f, 1f), Energy = 1.2f, Range = 6f, ShadowResolution = 256 });
    }

    protected override void OnRenderMainPass(in GameTime gameTime)
    {
        base.OnRenderMainPass(gameTime);
        if (Host.Count == 2 && gameTime.FrameCount == OverlayCheckFrame)
            CheckOverlayMaps();
        if (gameTime.FrameCount != CheckFrame || Shadows is not { } shadows)
            return;
        _checked = true;

        var slot = Vulkan.FrameSlot;
        int cascades = 0, tiles = 0, faces = 0;
        var tileRects = new List<(int X, int Y, int Size)>();
        var matrices = new HashSet<Matrix4x4>();
        foreach (var pass in shadows.Passes)
        {
            if (!shadows.PassRendered(pass.Index))
            {
                Fail(string.Create(CultureInfo.InvariantCulture, $"{pass.Kind} pass {pass.Index} has casters in view but did not render"));
                continue;
            }

            switch (pass.Kind)
            {
                case ShadowPassKind.Cascade: cascades++; break;
                case ShadowPassKind.AtlasTile: tiles++; tileRects.Add((pass.X, pass.Y, pass.Size)); break;
                case ShadowPassKind.CubeFace: faces++; break;
            }

            if (shadows.ReadPassMatrix(slot, pass.Index) != pass.ViewProjection)
                Fail(string.Create(CultureInfo.InvariantCulture, $"pass {pass.Index}: the ring does not hold its matrix"));
            if (!matrices.Add(pass.ViewProjection))
                Fail(string.Create(CultureInfo.InvariantCulture, $"pass {pass.Index} shares its matrix with another pass"));
        }

        if (cascades != 4 || tiles != 3 || faces != 12)
            Fail(string.Create(CultureInfo.InvariantCulture, $"expected 4 cascades, 3 atlas tiles and 12 cube faces; got {cascades}, {tiles}, {faces}"));
        for (var a = 0; a < tileRects.Count; a++)
            for (var b = a + 1; b < tileRects.Count; b++)
                if (new ShadowAtlasTile(tileRects[a].X, tileRects[a].Y, tileRects[a].Size).Overlaps(new ShadowAtlasTile(tileRects[b].X, tileRects[b].Y, tileRects[b].Size)))
                    Fail($"atlas tiles {tileRects[a]} and {tileRects[b]} overlap");
        if (!tileRects.Exists(t => t.Size == 512))
            Fail("the 512 spot light did not get a 512 tile");
        if (shadows.MapMemoryBytes >= 116L << 20)
            Fail(string.Create(CultureInfo.InvariantCulture, $"shadow maps use {shadows.MapMemoryBytes >> 20} MiB (the M1 layout used 116 MiB whatever the lights)"));
    }

    /// <summary>
    /// --count 2: before the resize, the overlay's Shadows panel must really have drawn the depth maps (its values refresh
    /// at 4 Hz, so the maps appear a few frames in); otherwise the validation test would not sample them at all.
    /// </summary>
    private void CheckOverlayMaps()
    {
        _overlayChecked = true;
        var renderer = Ui?.Renderer;
        if (renderer is null)
        {
            Fail("no Vulkan UI renderer: the dev overlay cannot draw the shadow maps");
            return;
        }

        if (renderer.EngineTextureDraws("dev-shadow-cascade-") == 0)
            Fail("the dev overlay's Shadows panel never drew a cascade image (engine://dev-shadow-cascade-*)");
        if (renderer.EngineTextureDraws("dev-shadow-atlas") == 0)
            Fail("the dev overlay's Shadows panel never drew the atlas image (engine://dev-shadow-atlas)");
    }

    protected override void DisposeScene()
    {
        if (!_checked && Host.AllocationMeasuredFrames == 0)
            Fail($"the shadow self-check did not run on frame {CheckFrame}");
        if (Host.Count == 2 && !_overlayChecked)
            Fail($"the dev overlay shadow-map check did not run on frame {OverlayCheckFrame}");
        base.DisposeScene();
    }
}

/// <summary>
/// A cutout (alpha-tested) fence casting a dotted shadow towards the camera. <c>--count 1</c> makes the fence opaque,
/// for comparison.
/// </summary>
public sealed class ShadowCutoutScene(HostOptions host) : ShadowSceneBase(host)
{
    protected override void Build(Node3D scene)
    {
        Camera.Position = new Vector3(0, 3.4f, 4.6f);
        Camera.LookAt(new Vector3(0, 0.2f, 1.2f));
        scene.AddChild(new MeshInstance3D { Name = "Floor", Mesh = new PlaneMesh { Size = new Vector2(12, 12) }, MaterialOverride = Plain(215, 210, 200) });

        var cutout = Host.Count != 1;
        scene.AddChild(new MeshInstance3D
        {
            Name = "Fence",
            Position = new Vector3(0, 1.1f, -0.6f),
            Mesh = new QuadMesh { Size = new Vector2(3f, 1.8f) },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoTexture = Texture2D.FromPixels(32, 32, MaterialsScene.Holes(32),
                    TextureImportSettings.Default with { Filter = TextureFilter.Nearest, Mipmaps = false }),
                UvScale = new Vector2(2.5f, 1.5f),
                AlbedoColor = Color.FromArgb(255, 120, 200, 120),
                Transparency = cutout ? AlphaMode.Cutout : AlphaMode.Opaque,
                DoubleSided = true,
            },
        });
        scene.AddChild(Sun(new Vector3(0.15f, -0.9f, 1f)));
        // A dim lamp behind the fence: its cube faces render the fence through the point-light cutout pipeline.
        scene.AddChild(new OmniLight3D { Name = "Lamp", Position = new Vector3(2.2f, 1.2f, -2.2f), Color = new Vector3(1f, 0.8f, 0.6f), Energy = 0.4f, Range = 7f });
    }
}

/// <summary>
/// Shimmer test: a straight-down camera over a plain floor that is shadowed by bars above the camera (out of view).
/// On frame 15 the camera moves sideways by exactly one screen pixel at the floor (less than one shadow texel), so
/// frame 20 must equal frame 10 shifted by one pixel when the cascades are texel-snapped. <c>--count 1</c> turns
/// snapping off (the shimmering it prevents).
/// </summary>
public sealed class ShadowShimmerScene(HostOptions host) : ShadowSceneBase(host)
{
    public const float Height = 6f;
    public const uint MoveFrame = 15;

    protected override bool HasSky => false;

    protected override void Build(Node3D scene)
    {
        Camera.Position = new Vector3(0.3f, Height, 0.2f);
        Camera.RotationDegrees = new Vector3(-90f, 0f, 0f); // looks down -Y; screen right = +X
        scene.AddChild(new MeshInstance3D { Name = "Floor", Mesh = new PlaneMesh { Size = new Vector2(60, 60) }, MaterialOverride = Plain(215, 215, 215, 0f) });

        // Thin bars at different angles to the texel grid, two units above the camera: invisible, but they shadow the
        // floor in view.
        var bar = new BoxMesh { Size = new Vector3(2.6f, 0.2f, 0.18f) };
        var material = Plain(100, 100, 100);
        for (var i = 0; i < 5; i++)
        {
            scene.AddChild(new MeshInstance3D
            {
                Name = string.Create(CultureInfo.InvariantCulture, $"Bar{i}"),
                Position = new Vector3(-3.6f + (i % 3) * 1.1f, Height + 2f, -2.6f + i * 0.75f),
                RotationDegrees = new Vector3(0, 17f + i * 23f, 0),
                Mesh = bar,
                MaterialOverride = material,
            });
        }

        var sun = Sun(new Vector3(0.45f, -1f, 0.3f));
        sun.ShadowCascades = 1;
        sun.ShadowMaxDistance = 12f;
        sun.ShadowResolution = 512; // a texel (~3.7 cm) is larger than the one-pixel camera move
        scene.AddChild(sun);
    }

    protected override void UpdateScene(in GameTime gameTime)
    {
        if (Shadows is { } shadows) // every frame: the shadow system is created on first use
            shadows.StableCascades = Host.Count != 1;
        if (gameTime.FrameCount == MoveFrame)
        {
            // One pixel at the floor: 2·h·tan(fov/2) / height in pixels.
            var extent = Vulkan.SwapchainExtent;
            var pixel = 2.0 * Height * Math.Tan(double.DegreesToRadians(Camera.Fov) / 2.0) / extent.Height;
            Camera.Position += new Vector3((float)pixel, 0, 0);
        }
    }
}

/// <summary>
/// Godot's <c>shadow_opacity</c> (ADR 0123): a box's shadow on a plain floor seen from above, the sun's
/// <see cref="Light3D.ShadowOpacity"/> picked by <c>--count</c> (0 full shadow, the default; 1 0.45, the Driving Range
/// sun; 2 no shadow).
/// </summary>
public sealed class ShadowOpacityScene(HostOptions host) : ShadowSceneBase(host)
{
    protected override bool HasSky => false;

    protected override void Build(Node3D scene)
    {
        Camera.Position = new Vector3(1.2f, 4f, 0.01f);
        Camera.LookAt(new Vector3(1.2f, 0f, 0f));

        scene.AddChild(new MeshInstance3D { Name = "Floor", Mesh = new PlaneMesh { Size = new Vector2(20, 20) }, MaterialOverride = Plain(210, 210, 210, 0f) });
        scene.AddChild(new MeshInstance3D
        {
            Name = "Box",
            Position = new Vector3(0, 0.5f, 0),
            Mesh = new BoxMesh(),
            MaterialOverride = Plain(90, 140, 220),
        });
        var sun = Sun(new Vector3(1f, -1.5f, 0.15f));
        sun.ShadowOpacity = Host.Count switch { 1 => 0.45f, 2 => 0f, _ => 1f };
        scene.AddChild(sun);
    }
}

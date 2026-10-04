using System.Drawing;
using System.Globalization;
using System.Numerics;
using ImGuiNET;

namespace MainframeEngine.RenderTests.Host.Scenes;

/// <summary>Shared set-up for the M3 mesh scenes: camera, procedural sky, a sun and a checker-textured floor.</summary>
public abstract class MeshSceneBase(HostOptions host) : RenderTestGame(host)
{
    public const string TestModelPath = "Content/Models/TestModel/test_model.gltf";
    public const string CheckerPath = "Content/Models/TestModel/checker.png";

    protected Camera3D Camera { get; private set; } = null!;
    protected Node3D Scene { get; private set; } = null!;

    protected virtual Vector3 CameraPosition => new(0, 2.5f, 6.5f);
    protected virtual Vector3 CameraTarget => new(0, 0.6f, 0);

    protected override void LoadScene()
    {
        Scene = new Node3D { Name = GetType().Name };
        Camera = new Camera3D { Name = "Camera", Position = CameraPosition };
        Camera.LookAt(CameraTarget);
        Scene.AddChild(Camera);
        Scene.AddChild(new WorldEnvironment { Name = "Environment", Sky = new Sky { Mode = SkyEnvironmentType.Procedural } });

        var sun = new DirectionalLight3D { Name = "Sun", Position = new Vector3(0, 5, 0), Color = new Vector3(1f, 0.96f, 0.88f), Energy = 0.9f };
        sun.LookAt(sun.Position + Vector3.Normalize(new Vector3(-0.5f, -1f, -0.7f)));
        Scene.AddChild(sun);

        // Built-in texture via the loader: its .meta asks for nearest filtering (crisp checks) without mipmaps.
        var checker = ResourceLoader.Load<Texture2D>(CheckerPath);
        Scene.AddChild(new MeshInstance3D
        {
            Name = "Floor",
            Mesh = new PlaneMesh { Size = new Vector2(14, 14) },
            MaterialOverride = new StandardMaterial3D { AlbedoTexture = checker, UvScale = new Vector2(7, 7), Specular = 0.1f },
        });

        Build(Scene);
        Tree.ChangeScene(Scene);
    }

    protected abstract void Build(Node3D scene);

    protected override void UpdateScene(in GameTime gameTime)
    {
    }

    protected override void DisposeScene()
    {
    }

    /// <summary>Pixel position of a world point in the main view (origin top-left), or null when behind the camera.</summary>
    protected Vector2? Project(Vector3 world)
    {
        var extent = Vulkan.SwapchainExtent;
        var camera = Camera.SyncRenderCamera((float)extent.Width / extent.Height);
        var clip = Vector4.Transform(new Vector4(world, 1f), camera.ViewMatrix * camera.ProjectionMatrix);
        if (clip.W <= 0f)
            return null;
        var ndc = new Vector2(clip.X, clip.Y) / clip.W;
        return new Vector2((ndc.X * 0.5f + 0.5f) * extent.Width, (0.5f - ndc.Y * 0.5f) * extent.Height);
    }
}

/// <summary>
/// One of each material feature on primitive meshes: textured box, alpha-cutout quad (double-sided), normal-mapped
/// sphere, emissive unshaded capsule, a mirrored box, a cylinder and a blended glass sphere in front of them.
/// </summary>
public sealed class MaterialsScene(HostOptions host) : MeshSceneBase(host)
{
    protected override void Build(Node3D scene)
    {
        var checker = ResourceLoader.Load<Texture2D>(CheckerPath);
        var box = new BoxMesh();

        scene.AddChild(new MeshInstance3D
        {
            Name = "TexturedBox",
            Position = new Vector3(-3f, 0.5f, 0),
            RotationDegrees = new Vector3(0, 25, 0),
            Mesh = box,
            MaterialOverride = new StandardMaterial3D { AlbedoTexture = checker, AlbedoColor = Color.FromArgb(255, 255, 230, 200) },
        });

        scene.AddChild(new MeshInstance3D
        {
            Name = "Cutout",
            Position = new Vector3(-1.5f, 0.75f, 0),
            Mesh = new QuadMesh { Size = new Vector2(1.4f, 1.4f) },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoTexture = Texture2D.FromPixels(32, 32, Holes(32),
                    TextureImportSettings.Default with { Filter = TextureFilter.Nearest, Mipmaps = false }),
                AlbedoColor = Color.FromArgb(255, 120, 220, 120),
                Transparency = AlphaMode.Cutout,
                DoubleSided = true,
            },
        });

        scene.AddChild(new MeshInstance3D
        {
            Name = "NormalMapped",
            Position = new Vector3(0, 0.7f, 0),
            Mesh = new SphereMesh { Radius = 0.7f, Height = 1.4f },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = Color.FromArgb(255, 200, 200, 210),
                NormalTexture = Texture2D.FromPixels(64, 64, Bumps(64)),
                Specular = 0.6f,
                Shininess = 48f,
                UvScale = new Vector2(4, 2),
            },
        });

        scene.AddChild(new MeshInstance3D
        {
            Name = "Emissive",
            Position = new Vector3(1.5f, 0.75f, 0),
            Mesh = new CapsuleMesh { Radius = 0.3f, Height = 1.5f },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = Color.Black,
                EmissionColor = Color.FromArgb(255, 255, 140, 40),
                EmissionEnergy = 1.5f,
                ShadingMode = ShadingMode.Unshaded,
            },
        });

        // Negative scale: the batcher must flip the front face (and the normals stay outward).
        scene.AddChild(new MeshInstance3D
        {
            Name = "Mirrored",
            Position = new Vector3(3f, 0.4f, 0),
            Scale = new Vector3(-0.8f, 0.8f, 0.8f),
            RotationDegrees = new Vector3(0, -20, 0),
            Mesh = box,
            MaterialOverride = new StandardMaterial3D { AlbedoColor = Color.FromArgb(255, 90, 150, 230) },
        });

        scene.AddChild(new MeshInstance3D
        {
            Name = "Cylinder",
            Position = new Vector3(-2.2f, 0.5f, -2f),
            Mesh = new CylinderMesh { TopRadius = 0.3f, BottomRadius = 0.5f, Height = 1f },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = Color.FromArgb(255, 200, 60, 60) },
        });

        scene.AddChild(new MeshInstance3D
        {
            Name = "Glass",
            Position = new Vector3(0.8f, 0.6f, 1.6f),
            Mesh = new SphereMesh { Radius = 0.5f, Height = 1f },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = Color.FromArgb(110, 120, 180, 255),
                Transparency = AlphaMode.Blend,
                Specular = 1f,
                Shininess = 96f,
            },
        });
    }

    /// <summary>Opaque white with round transparent holes (alpha 0) on a 8-pixel grid.</summary>
    public static byte[] Holes(int size)
    {
        var pixels = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var dx = x % 8 - 3.5f;
                var dy = y % 8 - 3.5f;
                var i = (y * size + x) * 4;
                pixels[i] = pixels[i + 1] = pixels[i + 2] = 255;
                pixels[i + 3] = dx * dx + dy * dy < 6f ? (byte)0 : (byte)255;
            }

        return pixels;
    }

    /// <summary>A tangent-space normal map of round bumps (OpenGL convention, encoded 0..255).</summary>
    public static byte[] Bumps(int size)
    {
        var pixels = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var u = (x % 16 + 0.5f) / 16f * 2f - 1f;
                var v = (y % 16 + 0.5f) / 16f * 2f - 1f;
                var r2 = u * u + v * v;
                var n = r2 < 0.8f ? Vector3.Normalize(new Vector3(u, -v, MathF.Sqrt(1f - r2 * 0.9f))) : Vector3.UnitZ;
                var i = (y * size + x) * 4;
                pixels[i] = (byte)MathF.Round((n.X * 0.5f + 0.5f) * 255f);
                pixels[i + 1] = (byte)MathF.Round((n.Y * 0.5f + 0.5f) * 255f);
                pixels[i + 2] = (byte)MathF.Round((n.Z * 0.5f + 0.5f) * 255f);
                pixels[i + 3] = 255;
            }

        return pixels;
    }
}

/// <summary>The generated glTF test model imported through Assimp, instanced twice (once mirrored).</summary>
public sealed class GltfScene(HostOptions host) : MeshSceneBase(host)
{
    protected override Vector3 CameraPosition => new(0.5f, 3f, 6f);
    protected override Vector3 CameraTarget => new(0.5f, 1.2f, 0);

    protected override void Build(Node3D scene)
    {
        var model = ResourceLoader.Load<PackedScene>(TestModelPath);
        var first = model.Instantiate<Node3D>();
        first.Position = new Vector3(-1.2f, 0, 0);
        scene.AddChild(first);

        var second = model.Instantiate<Node3D>();
        second.Name = "Mirror";
        second.Position = new Vector3(1.6f, 0, -0.5f);
        second.Scale = new Vector3(-1, 1, 1);
        second.RotationDegrees = new Vector3(0, 30, 0);
        scene.AddChild(second);
        model.Release();
    }
}

/// <summary>
/// <see cref="HostOptions.Count"/> boxes (default 1 000) sharing one mesh and one material in a grid, plus a sun
/// with shadows: the batcher must draw them in one instanced draw (two with the floor) and one shadow draw per
/// light pass. Self-checks the statistics; the performance test runs it with 10 000.
/// </summary>
public sealed class InstancesScene(HostOptions host) : MeshSceneBase(host)
{
    private const uint CheckFrame = 10;

    protected override Vector3 CameraPosition => new(0, 14f, 24f);
    protected override Vector3 CameraTarget => new(0, 0, -2f);

    private int Count => Host.Count > 0 ? Host.Count : 1000;

    protected override void Build(Node3D scene)
    {
        var mesh = new BoxMesh { Size = new Vector3(0.35f) };
        var material = new StandardMaterial3D { AlbedoColor = Color.FromArgb(255, 220, 160, 90) };
        var side = (int)MathF.Ceiling(MathF.Sqrt(Count));
        var spacing = 12f / side;
        var group = new Node3D { Name = "Boxes" };
        for (var i = 0; i < Count; i++)
        {
            int x = i % side, z = i / side;
            group.AddChild(new MeshInstance3D
            {
                Name = string.Create(CultureInfo.InvariantCulture, $"Box{i}"),
                Mesh = mesh,
                MaterialOverride = material,
                Position = new Vector3((x - side / 2f) * spacing, 0.2f + 0.15f * MathF.Sin(i * 0.37f), (z - side / 2f) * spacing),
                RotationDegrees = new Vector3(0, i * 7 % 90, 0),
            });
        }

        scene.AddChild(group);
    }

    protected override void UpdateScene(in GameTime gameTime)
    {
        if (gameTime.FrameCount != CheckFrame || Servers.Render is not { } render)
            return;
        // Previous frame's statistics: every box drawn, all of them in a handful of instanced draws.
        var stats = render.MeshStats;
        if (stats.Instances != Count + 1)
            Fail($"expected {Count + 1} mesh instances, got {stats.Instances}");
        if (stats.DrawCalls > 2)
            Fail($"{Count} boxes with one mesh and material took {stats.DrawCalls} draw calls (expected 2 with the floor)");
        if (stats.ShadowDrawCalls > 2)
            Fail($"shadow casters took {stats.ShadowDrawCalls} draws for one light (expected ≤ 2)");
        if (render.PipelineStates is not { Count: >= 1 and <= 2 })
            Fail($"expected 1–2 mesh pipelines, got {render.PipelineStates?.Count}");
    }
}

/// <summary>
/// Object-ID picking and an offscreen view. Frame 3 requests picks at the projected centres of three boxes and at a
/// sky pixel of the main view, and at the centre of a <see cref="SubViewport"/> (its own world: a sphere under its
/// own camera and light) shown in the corner through ImGui. Frame 15 checks every result.
/// </summary>
public sealed class PickingScene(HostOptions host) : MeshSceneBase(host)
{
    private const uint PickFrame = 3, CheckFrame = 15;

    private readonly List<(string What, Task<PickResult> Task, Node? Expected)> _picks = [];
    private readonly MeshInstance3D[] _boxes = new MeshInstance3D[3];
    private SubViewport _view = null!;
    private SubViewport _frozen = null!;
    private MeshInstance3D _sphere = null!;
    private MeshInstance3D _frozenBox = null!;
    private PickHandle _polled;
    private bool _checked;

    protected override void Build(Node3D scene)
    {
        Color[] colors = [Color.FromArgb(255, 220, 80, 80), Color.FromArgb(255, 80, 200, 80), Color.FromArgb(255, 80, 120, 220)];
        for (var i = 0; i < 3; i++)
        {
            _boxes[i] = new MeshInstance3D
            {
                Name = $"Box{i}",
                Position = new Vector3((i - 1) * 2f, 0.5f, 0),
                Mesh = new BoxMesh(),
                MaterialOverride = new StandardMaterial3D { AlbedoColor = colors[i] },
            };
            scene.AddChild(_boxes[i]);
        }

        // Offscreen view of another world: rendered before the main pass, shown with ImGui.Image.
        _view = new SubViewport { Name = "Preview", Width = 96, Height = 96, ClearColor = Color.FromArgb(255, 30, 30, 40) };
        var camera = new Camera3D { Name = "PreviewCamera", Position = new Vector3(0, 0.5f, 3f) };
        camera.LookAt(Vector3.Zero);
        _view.AddChild(camera);
        var light = new DirectionalLight3D { Name = "PreviewLight" };
        light.LookAt(Vector3.Normalize(new Vector3(-1, -1, -1)));
        _view.AddChild(light);
        _sphere = new MeshInstance3D
        {
            Name = "PreviewSphere",
            Mesh = new SphereMesh(),
            MaterialOverride = new StandardMaterial3D { AlbedoColor = Color.FromArgb(255, 240, 200, 80) },
        };
        _view.AddChild(_sphere);
        scene.AddChild(_view);

        // Rendered once, then frozen: picks must still be answered (an ID-only pass).
        _frozen = new SubViewport { Name = "Frozen", Width = 32, Height = 32, UpdateMode = SubViewportUpdateMode.Once };
        var frozenCamera = new Camera3D { Name = "FrozenCamera", Position = new Vector3(0, 0, 3f) };
        _frozen.AddChild(frozenCamera);
        _frozenBox = new MeshInstance3D { Name = "FrozenBox", Mesh = new BoxMesh() };
        _frozen.AddChild(_frozenBox);
        scene.AddChild(_frozen);
    }

    protected override void UpdateScene(in GameTime gameTime)
    {
        var render = Servers.Render!;
        if (gameTime.FrameCount == PickFrame)
        {
            foreach (var box in _boxes)
                if (Project(box.GlobalPosition) is { } pixel)
                    _picks.Add((box.Name, render.PickAsync((int)pixel.X, (int)pixel.Y), box));
            _picks.Add(("sky", render.PickAsync((int)Vulkan.SwapchainExtent.Width / 2, 2), null));
            _picks.Add(("outside", render.PickAsync(-5, 10), null));
            _picks.Add(("preview", _view.PickAsync(_view.Width / 2, _view.Height / 2), _sphere));
            _picks.Add(("preview corner", _view.PickAsync(2, 2), null));
            _picks.Add(("frozen view", _frozen.PickAsync(16, 16), _frozenBox));
            _polled = render.RequestPick((int)Project(_boxes[0].GlobalPosition)!.Value.X, (int)Project(_boxes[0].GlobalPosition)!.Value.Y);
        }

        if (gameTime.FrameCount == CheckFrame)
        {
            _checked = true;
            foreach (var (what, task, expected) in _picks)
            {
                if (!task.IsCompletedSuccessfully)
                {
                    Fail($"pick '{what}' did not complete within {CheckFrame - PickFrame} frames ({task.Status})");
                    continue;
                }

                var result = task.Result;
                if (!ReferenceEquals(result.Node, expected))
                    Fail($"pick '{what}': expected {expected?.Name ?? "nothing"}, got {result.Node?.Name ?? "nothing"} (id {result.ObjectId})");
                if (expected is GeometryInstance3D g && result.ObjectId != g.ObjectId)
                    Fail($"pick '{what}': id {result.ObjectId}, expected {g.ObjectId}");
            }

            if (!render.TryGetPickResult(_polled, out var polled) || !ReferenceEquals(polled.Node, _boxes[0]))
                Fail($"polled pick: expected {_boxes[0].Name}, got {polled.Node?.Name ?? "nothing"}");
            if (render.TryGetPickResult(_polled, out _))
                Fail("a polled result was returned twice");
            if (_view.RenderCount == 0 || _view.ColorImage is null || _view.ImGuiTextureId == 0)
                Fail("the sub-viewport was not rendered or registered with ImGui");
            if (_frozen.RenderCount != 1 || _frozen.UpdateMode != SubViewportUpdateMode.Disabled)
                Fail($"the UpdateMode.Once view rendered {_frozen.RenderCount} times");
        }
    }

    protected override void OnImGui(in GameTime gameTime)
    {
        if (_view.ImGuiTextureId != 0)
            ImGui.GetForegroundDrawList().AddImage(_view.ImGuiTextureId, new Vector2(8, 8), new Vector2(8 + 96, 8 + 96));
    }

    protected override void DisposeScene()
    {
        if (!_checked)
            Fail($"the picking self-check did not run (frame {CheckFrame})");
    }
}

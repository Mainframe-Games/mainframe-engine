using System.Numerics;

namespace MainframeEngine.RenderTests.Host.Scenes;

using Color = System.Drawing.Color;

/// <summary>
/// Spatial shaders (ADR 0205), lit by a sun with cascaded shadows and an omni light:
/// <list type="bullet">
/// <item>the ground: a plane whose albedo is procedural in world space (<c>spatial_ground.gdshader</c>: a biome map,
/// value noise, specks; a colour array, scalars and a nearest sampler set from C#);</item>
/// <item>fungal stalks in one <see cref="MultiMeshInstance3D"/> swaying by world position and <c>TIME</c>, out of step by
/// <c>INSTANCE_CUSTOM</c>, with matching swaying shadows (<c>spatial_sway.gdshader</c>, Blinn-Phong, cull_disabled);</item>
/// <item>a dissolving sphere loaded from <c>spatial_dissolve.mres</c> (its uniforms from the file, a hit-flash tint from
/// C#): a noise alpha scissor with an emissive edge, cut out in its shadow and in picking too;</item>
/// <item>a box with a standard material and an additive rim glow as its <see cref="Material.NextPass"/>, and a small
/// sphere with the rim as its <see cref="GeometryInstance3D.MaterialOverlay"/> (<c>spatial_rim.gdshader</c>).</item>
/// </list>
/// Self-checks picks: the ground, a stalk, the box, a solid dissolve sphere (progress 0) and one cut away completely
/// (progress 1.01, so the pick passes through to the ground).
/// </summary>
public sealed class SpatialShaderScene(HostOptions host) : ShadowSceneBase(host)
{
    public const uint PickFrame = 4, CheckFrame = 16;

    /// <summary>The frames the test compares (the stalks and their shadows move between them).</summary>
    public const uint Frame = 30, LaterFrame = 50;

    private readonly List<(string What, Task<PickResult> Task, Node? Expected)> _picks = [];
    private MeshInstance3D _ground = null!, _box = null!, _solid = null!, _gone = null!;
    private MultiMeshInstance3D _stalks = null!;
    private bool _checked;

    protected override void Build(Node3D scene)
    {
        Camera.Position = new Vector3(0, 4.2f, 8.5f);
        Camera.LookAt(new Vector3(0, 0.6f, 0));
        scene.AddChild(Sun(new Vector3(-0.45f, -1f, -0.55f), energy: 1.0f));
        scene.AddChild(new OmniLight3D { Name = "Omni", Position = new Vector3(2.8f, 1.2f, 2.6f), Color = new Vector3(1f, 0.55f, 0.25f), Energy = 2.5f, Range = 4.5f });

        // Ground: one material, the albedo entirely from the world position.
        var ground = new ShaderMaterial { Shader = Shader.Load(ContentPaths.Resolve("Assets/Shaders/spatial_ground.gdshader")) };
        ground.SetShaderParameter("palette", new[]
        {
            Srgb(96, 112, 64), Srgb(122, 128, 74), // grass, low biome
            Srgb(150, 118, 84), Srgb(176, 150, 112), // dust, high biome
        });
        ground.SetShaderParameter("cell_size", 4f);
        ground.SetShaderParameter("biome_map", BiomeMap());
        _ground = new MeshInstance3D { Name = "Ground", Mesh = new PlaneMesh { Size = new Vector2(16, 16) }, MaterialOverride = ground };
        scene.AddChild(_ground);

        // Stalks: a multimesh with custom data (phase, cap tint).
        var stalk = new CylinderMesh { TopRadius = 0.07f, BottomRadius = 0.12f, Height = 2.2f, RadialSegments = 12, Rings = 10, CapTop = false, CapBottom = false };
        var multimesh = new MultiMesh { Mesh = stalk, UseCustomData = true, InstanceCount = 10 };
        for (var i = 0; i < multimesh.InstanceCount; i++)
        {
            var position = new Vector3(-5.2f + i * 0.45f, 1.1f, -1.8f + (i % 3) * 0.9f);
            multimesh.SetInstanceTransform(i, new Transform3D(Basis.Identity, position));
            var tint = (i % 3) switch { 0 => new Vector3(0.9f, 0.3f, 0.6f), 1 => new Vector3(0.35f, 0.75f, 0.9f), _ => new Vector3(0.95f, 0.8f, 0.25f) };
            multimesh.SetInstanceCustomData(i, new Vector4(i * 0.137f, tint.X, tint.Y, tint.Z));
        }

        var sway = new ShaderMaterial { Shader = Shader.Load(ContentPaths.Resolve("Assets/Shaders/spatial_sway.gdshader")) };
        _stalks = new MultiMeshInstance3D { Name = "Stalks", Multimesh = multimesh, MaterialOverride = sway };
        scene.AddChild(_stalks);

        // The dissolve from a .mres (progress 0.42, colour, edge energy), tinted by a hit flash from C#.
        var dissolve = ResourceLoader.Load<ShaderMaterial>("Assets/Materials/spatial_dissolve.mres");
        dissolve.SetShaderParameter("hit_flash", Srgb(255, 70, 70, 0.3f));
        scene.AddChild(new MeshInstance3D
        {
            Name = "Dissolve",
            Position = new Vector3(1.4f, 1.1f, -0.4f),
            Mesh = new SphereMesh { Radius = 1.05f, Height = 2.1f },
            MaterialOverride = dissolve,
        });

        // Picking targets: a solid and a vanished dissolve sphere (same shader, progress 0 and above every noise value).
        _solid = Dissolved("SolidDissolve", new Vector3(4.6f, 0.6f, 0.8f), 0f);
        _gone = Dissolved("GoneDissolve", new Vector3(-1.2f, 0.6f, 2.6f), 1.01f);
        scene.AddChild(_solid);
        scene.AddChild(_gone);

        // A next pass and an overlay: the additive rim over standard materials.
        var rimShader = Shader.Load(ContentPaths.Resolve("Assets/Shaders/spatial_rim.gdshader"));
        var boxMaterial = Plain(200, 200, 205, 0.2f);
        boxMaterial.NextPass = new ShaderMaterial { Shader = rimShader };
        _box = new MeshInstance3D { Name = "RimBox", Position = new Vector3(-1.3f, 0.6f, 0.4f), RotationDegrees = new Vector3(0, 30, 0), Mesh = new BoxMesh { Size = new Vector3(1.2f, 1.2f, 1.2f) }, MaterialOverride = boxMaterial };
        scene.AddChild(_box);
        var overlay = new ShaderMaterial { Shader = rimShader };
        overlay.SetShaderParameter("rim_color", Srgb(255, 120, 40));
        scene.AddChild(new MeshInstance3D
        {
            Name = "OverlaySphere",
            Position = new Vector3(3.6f, 0.5f, 2.4f),
            Mesh = new SphereMesh { Radius = 0.5f, Height = 1f },
            MaterialOverride = Plain(60, 70, 90),
            MaterialOverlay = overlay,
        });
    }

    private static MeshInstance3D Dissolved(string name, Vector3 position, float progress)
    {
        var material = new ShaderMaterial { Shader = Shader.Load(ContentPaths.Resolve("Assets/Shaders/spatial_dissolve.gdshader")) };
        material.SetShaderParameter("progress", progress);
        material.SetShaderParameter("base_color", Srgb(120, 200, 120));
        return new MeshInstance3D { Name = name, Position = position, Mesh = new SphereMesh { Radius = 0.6f, Height = 1.2f }, MaterialOverride = material };
    }

    private static Vector4 Srgb(int r, int g, int b, float a = 1f) => new(r / 255f, g / 255f, b / 255f, a);

    // A 4 × 4 biome map (one texel per 4 m cell of the 16 m ground): 0 or 255 in red, sampled nearest.
    private static Texture2D BiomeMap()
    {
        int[] cells = [0, 1, 1, 0, 0, 0, 1, 1, 1, 0, 0, 1, 1, 1, 0, 0];
        var pixels = new byte[16 * 4];
        for (var i = 0; i < 16; i++)
        {
            pixels[i * 4] = (byte)(cells[i] * 255);
            pixels[i * 4 + 3] = 255;
        }

        return Texture2D.FromPixels(4, 4, pixels, new TextureImportSettings { ColorSpace = TextureImportColorSpace.Linear, Mipmaps = false });
    }

    protected override void UpdateScene(in GameTime gameTime)
    {
        var render = Servers.Render!;
        if (gameTime.FrameCount == PickFrame)
        {
            Pick("ground", new Vector3(0.5f, 0f, 3.2f), _ground);
            Pick("stalk", new Vector3(-5.2f + 4 * 0.45f, 0.15f, -1.8f + 0.9f), _stalks);
            Pick("box", _box.GlobalPosition, _box);
            Pick("solid dissolve", _solid.GlobalPosition, _solid);
            Pick("vanished dissolve", _gone.GlobalPosition, _ground); // cut away in the ID pass: the ground behind it
        }

        if (gameTime.FrameCount == CheckFrame)
        {
            _checked = true;
            foreach (var (what, task, expected) in _picks)
            {
                if (!task.IsCompletedSuccessfully)
                    Fail($"pick '{what}' did not complete ({task.Status})");
                else if (!ReferenceEquals(task.Result.Node, expected))
                    Fail($"pick '{what}': expected {expected?.Name ?? "nothing"}, got {task.Result.Node?.Name ?? "nothing"} (id {task.Result.ObjectId})");
            }
        }

        void Pick(string what, Vector3 world, Node expected)
        {
            if (Project(world) is { } pixel)
                _picks.Add((what, render.PickAsync((int)pixel.X, (int)pixel.Y), expected));
            else
                Fail($"pick '{what}' is behind the camera");
        }
    }

    protected override void DisposeScene()
    {
        if (!_checked)
            Fail($"the picking self-check did not run (frame {CheckFrame})");
    }

    private Vector2? Project(Vector3 world)
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

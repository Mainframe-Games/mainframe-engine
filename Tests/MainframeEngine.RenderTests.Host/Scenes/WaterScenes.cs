using System.Numerics;

namespace MainframeEngine.RenderTests.Host.Scenes;

using Color = System.Drawing.Color;

/// <summary>Shared pieces of the ADR 0173 water scenes: a flat water quad with a column depth, probes into captures.</summary>
internal static class WaterSceneKit
{
    /// <summary>A flat, upward water surface over [x0, x1] × [z0, z1] at height y, with a constant column depth (Custom0.x).</summary>
    public static ArrayMesh Quad(float x0, float x1, float z0, float z1, float y, float column, int cells = 16)
    {
        var columns = cells + 1;
        var positions = new Vector3[columns * columns];
        var normals = new Vector3[positions.Length];
        var uvs = new Vector2[positions.Length];
        var custom = new Vector4[positions.Length];
        for (var j = 0; j < columns; j++)
        {
            for (var i = 0; i < columns; i++)
            {
                var v = j * columns + i;
                positions[v] = new Vector3(x0 + (x1 - x0) * i / cells, y, z0 + (z1 - z0) * j / cells);
                normals[v] = Vector3.UnitY;
                uvs[v] = new Vector2(0.5f, 0f); // no ribbon-edge foam
                custom[v] = new Vector4(column, 0f, 0f, 0f);
            }
        }

        var indices = new int[cells * cells * 6];
        var n = 0;
        for (var j = 0; j < cells; j++)
        {
            for (var i = 0; i < cells; i++)
            {
                var a = j * columns + i;
                var b = a + 1;
                var c = a + columns;
                var d = c + 1;
                // Counter-clockwise seen from above (+Y): (a, c, b) and (b, c, d) with +Z towards the viewer.
                indices[n++] = a;
                indices[n++] = c;
                indices[n++] = b;
                indices[n++] = b;
                indices[n++] = c;
                indices[n++] = d;
            }
        }

        var surface = new MeshSurface(positions, normals, uvs, indices) { Custom0 = custom };
        var mesh = new ArrayMesh();
        mesh.AddSurface(surface);
        return mesh;
    }

    /// <summary>Black and white stripes along X (a period of <paramref name="period"/> texels), tileable.</summary>
    public static Texture2D Stripes(int size = 64, int period = 16)
    {
        var pixels = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var o = (y * size + x) * 4;
                var white = x % period < period / 2;
                pixels[o] = pixels[o + 1] = pixels[o + 2] = white ? (byte)230 : (byte)20;
                pixels[o + 3] = 255;
            }
        }

        return Texture2D.FromPixels(size, size, pixels, new TextureImportSettings
        {
            ColorSpace = TextureImportColorSpace.Srgb,
            Mipmaps = false,
            Filter = TextureFilter.Nearest,
            Wrap = TextureWrap.Repeat,
        });
    }

    /// <summary>The pixel a world point lands on, or null off screen.</summary>
    public static (int X, int Y)? Pixel(Camera3D camera, FrameCapture capture, Vector3 world)
    {
        var render = camera.SyncRenderCamera((float)capture.Width / capture.Height);
        var clip = Vector4.Transform(new Vector4(world, 1f), render.ViewMatrix * render.ProjectionMatrix);
        if (clip.W <= 0f)
            return null;
        var ndc = new Vector2(clip.X, clip.Y) / clip.W;
        var x = (int)((ndc.X * 0.5f + 0.5f) * capture.Width);
        var y = (int)((0.5f - ndc.Y * 0.5f) * capture.Height);
        return x < 2 || y < 2 || x >= capture.Width - 2 || y >= capture.Height - 2 ? null : (x, y);
    }

    /// <summary>The mean RGB of the 5 × 5 pixels around (x, y).</summary>
    public static Vector3 Mean(FrameCapture capture, int cx, int cy)
    {
        var sum = Vector3.Zero;
        for (var y = cy - 2; y <= cy + 2; y++)
        {
            for (var x = cx - 2; x <= cx + 2; x++)
            {
                var i = (y * capture.Width + x) * 4;
                sum += new Vector3(capture.Pixels[i], capture.Pixels[i + 1], capture.Pixels[i + 2]);
            }
        }

        return sum / 25f;
    }
}

/// <summary>
/// ADR 0173: refraction through <see cref="WaterMaterial3D.RefractionEnabled"/>. A striped bed (unshaded, black and
/// white stripes across X) 1 m below a clear pool, seen at an angle. Frame 10 hides the water (the bed alone), frame 20
/// draws it flat with refraction strength 0 (no bend: the bed exactly as behind, × 1 − Fresnel), frame 30 rippled with
/// strength 1 (Snell at the rippled surface). Self-checks: the unbent pool shows the bed straight behind it; the bent
/// pool moves the stripes (many pixels change from black to white or back) while the bed beside the pool stays put.
/// Golden at frame 30. With <c>--alloc</c> the camera orbits with the projection jittered.
/// </summary>
public sealed class WaterRefractionScene(HostOptions host) : RenderTestGame(host)
{
    private const uint BedFrame = 10, StraightFrame = 20, BentFrame = 30;
    private Camera3D _camera = null!;
    private MeshInstance3D _water = null!;
    private WaterMaterial3D _material = null!;
    private FrameCapture? _bed;
    private FrameCapture? _straight;
    private uint _frame;

    private bool Orbit => Host.AllocationMeasuredFrames > 0;

    protected override void LoadScene()
    {
        var scene = new Node3D { Name = nameof(WaterRefractionScene) };
        _camera = new Camera3D { Name = "Camera", Position = new Vector3(0f, 3.2f, 4.5f), Fov = 55f };
        _camera.LookAt(new Vector3(0f, -0.6f, -0.5f));
        scene.AddChild(_camera);

        var bed = new StandardMaterial3D
        {
            ShadingMode = ShadingMode.Unshaded,
            AlbedoTexture = WaterSceneKit.Stripes(),
            UvScale = new Vector2(4f, 4f), // 0.5 m stripes on the 8 m plane
        };
        scene.AddChild(new MeshInstance3D { Name = "Bed", Mesh = new PlaneMesh { Size = new Vector2(8f, 8f) }, Position = new Vector3(0f, -1f, 0f), MaterialOverride = bed });

        _material = new WaterMaterial3D
        {
            RefractionEnabled = true,
            RefractionStrength = 0f,
            RefractionRoughness = 0f,
            Absorption = Vector3.Zero,
            ScatterStrength = 0f,
            ReflectionStrength = 0f,
            FoamStrength = 0f,
            ShoreFoamDistance = 0f,
            SoftEdgeDistance = 0f,
            CausticsStrength = 0f,
            NormalStrength = 0f,
            NormalScaleNear = 1.5f,
            NormalScaleFar = 4f,
            ScreenSpaceReflections = false,
        };
        _water = new MeshInstance3D
        {
            Name = "Water",
            Mesh = WaterSceneKit.Quad(-2f, 2f, -2.5f, 1.5f, 0f, 1f),
            MaterialOverride = _material,
            Visible = false,
        };
        scene.AddChild(_water);
        Tree.ChangeScene(scene);
    }

    protected override void UpdateScene(in GameTime gameTime)
    {
        _frame = gameTime.FrameCount;
        if (Orbit)
        {
            _water.Visible = true;
            _material.RefractionStrength = 1f;
            var angle = gameTime.FrameCount % 120 / 120f * MathF.Tau;
            _camera.Position = new Vector3(5f * MathF.Sin(angle), 3.2f, 5f * MathF.Cos(angle));
            _camera.LookAt(new Vector3(0f, -0.6f, 0f));
            return;
        }

        _water.Visible = _frame > BedFrame;
        var bent = _frame > StraightFrame;
        _material.RefractionStrength = bent ? 1f : 0f;
        _material.NormalStrength = bent ? 0.5f : 0f;
    }

    protected override void OnFrameCaptured(FrameCapture capture)
    {
        base.OnFrameCaptured(capture);
        if (Orbit)
            return;
        if (_frame == BedFrame)
            _bed = capture;
        else if (_frame == StraightFrame)
            _straight = capture;
        else if (_frame == BentFrame && _bed is not null && _straight is not null)
            Check(_bed, _straight, capture);
    }

    // Probes on a grid inside the pool (its middle 70 %) and outside it (the bed beyond its far edge).
    private void Check(FrameCapture bed, FrameCapture straight, FrameCapture bent)
    {
        int inside = 0, straightMismatch = 0, moved = 0;
        for (var j = 0; j < 12; j++)
        {
            for (var i = 0; i < 12; i++)
            {
                var world = new Vector3(-1.4f + 2.8f * i / 11f, 0f, -1.9f + 2.8f * j / 11f);
                if (WaterSceneKit.Pixel(_camera, bed, world) is not { } p)
                    continue;
                inside++;
                var b = Lum(bed, p.X, p.Y);
                var s = Lum(straight, p.X, p.Y);
                var r = Lum(bent, p.X, p.Y);
                // Unbent: the bed straight behind, darkened only by the surface's Fresnel reflectance (≤ 12 %).
                if (s > b * 1.02f + 3f || s < b * 0.86f - 3f)
                    straightMismatch++;
                if (MathF.Abs(r - b) > 80f)
                    moved++;
            }
        }

        Log.Info($"Water refraction: {inside} probes, {straightMismatch} unbent mismatches, {moved} moved by the bend");
        if (inside < 100)
            Fail($"Only {inside} pool probes are on screen.");
        if (straightMismatch > inside / 20)
            Fail($"Without bending, {straightMismatch} of {inside} pool probes differ from the bed straight behind.");
        if (moved < inside / 6)
            Fail($"With refraction only {moved} of {inside} pool probes changed between stripes: the bed is not bent.");

        // Beside the pool (seen past its sides, not through it) nothing changes.
        var outside = 0;
        for (var i = 0; i < 10; i++)
        {
            var world = new Vector3(i < 5 ? -3.4f : 3.4f, -1f, -2f + 4f * (i % 5) / 4f);
            if (WaterSceneKit.Pixel(_camera, bed, world) is not { } p)
                continue;
            if (MathF.Abs(Lum(bent, p.X, p.Y) - Lum(bed, p.X, p.Y)) > 2f)
                outside++;
        }

        if (outside > 0)
            Fail($"{outside} probes of the bed beyond the pool changed when the water refracted.");
    }

    private static float Lum(FrameCapture capture, int x, int y)
    {
        var i = (y * capture.Width + x) * 4;
        return 0.2126f * capture.Pixels[i] + 0.7152f * capture.Pixels[i + 1] + 0.0722f * capture.Pixels[i + 2];
    }

    protected override void DisposeScene()
    {
    }
}

/// <summary>
/// ADR 0173: screen-space reflections. A mirror-flat, dark pool (strong absorption, no scatter) under a grey sky colour
/// with a bright red pillar standing beyond its far edge; the camera looks across the pool at the pillar. Frame 20 has
/// <see cref="WaterMaterial3D.ScreenSpaceReflections"/> on, frame 30 off. Self-checks: where the pillar's mirror image
/// falls on the water it is red with SSR and not without; the water away from it is not red either way. Golden at
/// frame 20. <c>--count 1</c> runs with TAA (the reflection must survive the reactive mask); <c>--count 2</c> renders
/// the world in a post-processed <see cref="SubViewport"/> with TAA (the editor's case: its own scene copy and resumed
/// pass) and checks the view's captures.
/// </summary>
public sealed class WaterSsrScene(HostOptions host) : RenderTestGame(host)
{
    private const uint OnFrame = 20, OffFrame = 30;
    private static readonly Vector3 PillarCentre = new(0.4f, 1f, -4.6f);
    private Camera3D _camera = null!;
    private WaterMaterial3D _material = null!;
    private SubViewport? _view;
    private uint _frame;

    protected override void LoadScene()
    {
        if (Host.Count == 1)
            Vulkan.AntiAliasing = AntiAliasing.Taa;
        var scene = new Node3D { Name = nameof(WaterSsrScene) };
        Node world = scene;
        if (Host.Count == 2)
        {
            var extent = Vulkan.SwapchainExtent;
            _view = new SubViewport
            {
                Name = "View",
                Width = (int)extent.Width,
                Height = (int)extent.Height,
                PostProcessing = true,
                AntiAliasing = AntiAliasing.Taa,
            };
            scene.AddChild(_view);
            world = _view;
        }

        _camera = new Camera3D { Name = "Camera", Position = new Vector3(0f, 1.4f, 3.5f), Fov = 60f, Current = true };
        _camera.LookAt(new Vector3(0f, 0.2f, -3f));
        world.AddChild(_camera);
        world.AddChild(new WorldEnvironment { Name = "Environment", AmbientColor = new Vector3(0.43f, 0.47f, 0.51f) });

        var ground = new StandardMaterial3D { ShadingMode = ShadingMode.Unshaded, AlbedoColor = Color.FromArgb(255, 60, 70, 60) };
        world.AddChild(new MeshInstance3D { Name = "Bed", Mesh = new PlaneMesh { Size = new Vector2(30f, 30f) }, Position = new Vector3(0f, -1f, 0f), MaterialOverride = ground });
        world.AddChild(new MeshInstance3D
        {
            Name = "Pillar",
            Mesh = new BoxMesh { Size = new Vector3(0.8f, 3f, 0.8f) },
            Position = PillarCentre with { Y = 0.5f },
            MaterialOverride = new StandardMaterial3D { ShadingMode = ShadingMode.Unshaded, AlbedoColor = Color.FromArgb(255, 235, 30, 25) },
        });

        _material = new WaterMaterial3D
        {
            RefractionEnabled = true,
            Absorption = new Vector3(4f, 4f, 4f),
            ScatterStrength = 0f,
            NormalStrength = 0f,
            Roughness = 0.02f,
            FoamStrength = 0f,
            ShoreFoamDistance = 0f,
            SoftEdgeDistance = 0f,
            CausticsStrength = 0f,
        };
        world.AddChild(new MeshInstance3D { Name = "Water", Mesh = WaterSceneKit.Quad(-4f, 4f, -4.1f, 3f, 0f, 1f), MaterialOverride = _material });
        Tree.ChangeScene(scene);
    }

    protected override void UpdateScene(in GameTime gameTime)
    {
        _frame = gameTime.FrameCount;
        _material.ScreenSpaceReflections = _frame <= OnFrame;
        if (_view is not null && _frame is OnFrame or OffFrame)
        {
            var ssr = _frame == OnFrame;
            _view.CaptureImage(capture => Check(capture, ssr));
        }
    }

    protected override void OnFrameCaptured(FrameCapture capture)
    {
        base.OnFrameCaptured(capture);
        if (_view is null && _frame is OnFrame or OffFrame)
            Check(capture, _frame == OnFrame);
    }

    private void Check(FrameCapture capture, bool ssr)
    {
        // The pillar's mirror image across the water plane (y = 0), seen where the camera ray to it crosses the surface.
        var mirror = new Vector3(PillarCentre.X, -0.6f, PillarCentre.Z);
        var redness = Redness(capture, mirror, "the pillar's reflection");
        var away = Redness(capture, new Vector3(-2.6f, 0f, -2.5f), "open water");
        Log.Info($"Water SSR ({(ssr ? "on" : "off")}{(_view is null ? "" : ", sub-viewport")}): reflection redness {redness:F0}, open water {away:F0}");
        if (ssr && !(redness > 60f))
            Fail($"With SSR the pillar's reflection is not red (redness {redness:F0}).");
        if (!ssr && redness > 20f)
            Fail($"Without SSR the water shows something red where the pillar's reflection would be (redness {redness:F0}).");
        if (away > 20f)
            Fail($"Open water is red (redness {away:F0}).");
    }

    // Red minus the larger of green and blue (display values) around a world point.
    private float Redness(FrameCapture capture, Vector3 world, string what)
    {
        if (WaterSceneKit.Pixel(_camera, capture, world) is not { } p)
        {
            Fail($"The probe at {what} is off screen.");
            return 0f;
        }

        var c = WaterSceneKit.Mean(capture, p.X, p.Y);
        return c.X - MathF.Max(c.Y, c.Z);
    }

    protected override void DisposeScene()
    {
    }
}

/// <summary>
/// ADR 0173: a <see cref="River3D"/> stream dropping 4 m over a cliff in a carved terrain: the ribbon stops at the lip, the
/// fall's jet (<see cref="WaterfallMaterial3D"/>) runs to the foot, plunge foam and <see cref="SprayCards3D"/> at the foot,
/// refracting water with caustics above and below, under a procedural sky with the sun behind the fall. Self-checks at
/// frame 5: one fall found, its jet and spray exist, the stream refracts. Golden at frame 30.
/// </summary>
public sealed class WaterFallScene(HostOptions host) : RenderTestGame(host)
{
    private const uint CheckFrame = 5;
    private Camera3D _camera = null!;
    private River3D _river = null!;

    private static float Ground(float x, float z)
    {
        var t = Math.Clamp((z - 28f) / 5f, 0f, 1f);
        var step = 7f - 4f * t * t * (3f - 2f * t); // a plateau, a cliff, the lower valley
        var across = x - 32f;
        return step - 1.2f * MathF.Exp(-across * across / 18f) + 0.012f * across * across + 0.15f * MathF.Sin(x * 0.9f) * MathF.Cos(z * 0.7f);
    }

    protected override void LoadScene()
    {
        var scene = new Node3D { Name = nameof(WaterFallScene) };
        _camera = new Camera3D { Name = "Camera", Position = new Vector3(5f, 4.5f, 16f) };
        _camera.LookAt(new Vector3(0f, 2.8f, -1.5f));
        scene.AddChild(_camera);

        var lightDirection = Vector3.Normalize(new Vector3(0.35f, -0.6f, 0.72f)); // from behind the fall
        scene.AddChild(new WorldEnvironment
        {
            Name = "Environment",
            Sky = new Sky { Mode = SkyEnvironmentType.Procedural, SunDirection = -lightDirection },
            AmbientSource = AmbientSource.Sky,
            ReflectedLightSource = ReflectedLightSource.Sky,
        });
        var sun = new DirectionalLight3D { Name = "Sun", Color = new Vector3(1f, 0.93f, 0.82f), Energy = 1.1f };
        sun.LookAt(lightDirection);
        scene.AddChild(sun);

        var data = TerrainData.Create(TerrainProfile.Realistic, 64, 0.5f, 16);
        data.CollisionMode = TerrainCollisionMode.None;
        data.SetHeightsFrom(Ground);
        scene.AddChild(new Terrain3D
        {
            Name = "Terrain",
            Position = new Vector3(-32, 0, -32),
            Data = data,
            Material = new StandardMaterial3D { ShadingMode = ShadingMode.Pbr, AlbedoColor = Color.FromArgb(255, 118, 112, 92), Roughness = 0.9f },
        });

        var curve = new Curve3D();
        float[] zs = [2f, 16f, 27.5f, 33.5f, 46f, 62f];
        for (var i = 0; i < zs.Length; i++)
        {
            var handle = new Vector3(0f, 0f, i is 2 or 3 ? 1.2f : 4f); // flat at the lip and the foot: the drop stays a step
            curve.AddPoint(new Vector3(0f, 0f, zs[i] - 32f), -handle, handle);
            curve.SetPointWidth(i, 2.6f);
            curve.SetPointDepth(i, 0.5f);
        }

        _river = new River3D
        {
            Name = "Stream",
            Curve = curve,
            Material = new WaterMaterial3D { RefractionEnabled = true, CausticsStrength = 0.8f, NormalScaleNear = 1.2f, NormalScaleFar = 5f },
            Spray = Host.Count != 2,
        };
        scene.AddChild(_river);
        Tree.ChangeScene(scene);
        if (!_river.FitToTerrain(0.35f))
            Fail("the stream found no terrain to fit to");
        if (!_river.Carve())
            Fail("the stream could not carve the terrain");
    }

    protected override void UpdateScene(in GameTime gameTime)
    {
        if (gameTime.FrameCount != CheckFrame)
            return;
        if (_river.Falls.Count != 1)
            Fail($"the stream has {_river.Falls.Count} falls, expected 1");
        else if (!(_river.Falls[0].Drop > 2.5f))
            Fail($"the fall drops {_river.Falls[0].Drop:0.00} m");
        if (_river.FallsNode?.Mesh is null)
            Fail("the fall has no jet");
        if (_river.SprayNodes.Count != 1)
            Fail($"the stream has {_river.SprayNodes.Count} spray nodes, expected 1");
        if (_river.Ribbon?.MaterialOverride is not WaterMaterial3D { RefractionEnabled: true })
            Fail("the stream does not refract");
    }

    protected override void DisposeScene()
    {
    }
}

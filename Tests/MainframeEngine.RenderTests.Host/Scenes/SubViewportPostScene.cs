using System.Numerics;

namespace MainframeEngine.RenderTests.Host.Scenes;

using Color = System.Drawing.Color;

/// <summary>
/// A post-processed <see cref="SubViewport"/> (ADR 0169), the editor's case: the main world is empty and a UI image shows the
/// view full-window (<c>engine://subviewport-post</c>, one view texel per pixel). The view's world: a floor, a box and a PBR
/// sphere under a sun with shadows and the procedural sky, a <see cref="Grid3D"/>, a patch of red debug lines (depth-tested)
/// and a patch of green overlay lines, and a <see cref="WorldEnvironment"/> whose profile turns the image grey (saturation
/// 0.02), adds glow, SSAO and a strong vignette. Self-checked on <see cref="SubViewport.CaptureImage"/>: with
/// post-processing the scene is grey and the line patches keep their exact colours (drawn after the effects); without it the
/// scene keeps its colours. <c>--count</c>: 0 post on, 1 post off (the same scene: the engine tonemap alone), 2 post on with
/// TAA (the history accumulates; the overlay lines do not move with the jitter), 3 post on with TAA and an orbiting camera
/// (the allocation run).
/// </summary>
public sealed class SubViewportPostScene(HostOptions host) : RenderTestGame(host)
{
    /// <summary>The frame the self-checks capture the view (and the window is captured for the golden).</summary>
    public const uint CheckFrame = 8;

    private static readonly Vector3 CameraPosition = new(0f, 2.2f, 4.2f);
    private static readonly Vector3 CameraTarget = new(0f, 0.4f, 0f);
    private static readonly Vector3 RedPatch = new(-1.2f, 1.1f, 1.6f);   // in front of everything
    private static readonly Vector3 GreenPatch = new(1.2f, 1.1f, 1.6f);
    private static readonly Vector3 SphereProbe = new(-0.75f, 0.55f, 0.55f); // the sphere's lit front
    private static readonly Vector3 BoxProbe = new(0.8f, 0.5f, 0.31f);       // the box's front face

    private SubViewport _view = null!;
    private Camera3D _camera = null!;
    private bool _registered;
    private readonly List<(uint Frame, byte[] Pixels, int Width, int Height)> _captures = [];
    private int _pendingCaptures;
    private bool _checked;

    private bool PostOn => Host.Count != 1;
    private bool Taa => Host.Count >= 2;

    protected override void LoadScene()
    {
        var root = new Node3D { Name = nameof(SubViewportPostScene) };
        var extent = Vulkan.SwapchainExtent;
        _view = new SubViewport
        {
            Name = "View",
            Width = (int)extent.Width,
            Height = (int)extent.Height,
            ClearColor = Color.FromArgb(255, 40, 40, 48),
            Shadows = true, // the main world draws nothing: the view gets the shadow maps (the editor's case)
            PostProcessing = PostOn,
            AntiAliasing = Taa ? AntiAliasing.Taa : AntiAliasing.None,
        };
        _camera = new Camera3D { Name = "Camera", Position = CameraPosition, Current = true };
        _camera.LookAt(CameraTarget);
        _view.AddChild(_camera);

        var sun = new DirectionalLight3D { Name = "Sun", Energy = 1.1f, CastsShadows = true };
        sun.LookAt(Vector3.Normalize(new Vector3(-0.4f, -1f, -0.5f)));
        _view.AddChild(sun);
        _view.AddChild(new MeshInstance3D
        {
            Name = "Floor",
            Mesh = new PlaneMesh { Size = new Vector2(12f, 12f) },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = Color.FromArgb(255, 120, 150, 90) },
        });
        _view.AddChild(new MeshInstance3D
        {
            Name = "Box",
            Position = new Vector3(0.8f, 0.5f, -0.2f),
            Mesh = new BoxMesh(),
            MaterialOverride = new StandardMaterial3D { AlbedoColor = Color.FromArgb(255, 210, 90, 60) },
        });
        _view.AddChild(new MeshInstance3D
        {
            Name = "Sphere",
            Position = new Vector3(-0.9f, 0.6f, 0f),
            Mesh = new SphereMesh { Radius = 0.6f, Height = 1.2f },
            MaterialOverride = new StandardMaterial3D { ShadingMode = ShadingMode.Pbr, AlbedoColor = Color.FromArgb(255, 70, 120, 220), Roughness = 0.4f },
        });
        _view.AddChild(new Grid3D { Name = "Grid", GridSize = 20 });
        _view.AddChild(new WorldEnvironment
        {
            Name = "Environment",
            Sky = new Sky { Mode = SkyEnvironmentType.Procedural },
            AmbientSource = AmbientSource.Sky,
            PostProcess = new PostProcessProfile
            {
                Tonemapper = Tonemapper.Filmic,
                GlowEnabled = true,
                GlowHdrThreshold = 0.8f,
                GlowIntensity = 0.6f,
                SsaoEnabled = true,
                AdjustmentEnabled = true,
                AdjustmentSaturation = 0.02f,
                AdjustmentContrast = 1.15f,
            },
            CameraAttributes = new CameraAttributesPractical { VignetteIntensity = 0.6f },
        });
        root.AddChild(_view);
        Tree.ChangeScene(root);
    }

    protected override void UpdateScene(in GameTime gameTime)
    {
        if (!_registered && _view.ColorTarget is { } target)
        {
            // engine:// textures must be registered before a document loads them: the document is added only now.
            Servers.Get<UiServer>()!.RegisterTexture("subviewport-post", target);
            _registered = true;
            var layer = new UiLayer { Name = "ViewLayer", Layer = 0 };
            Tree.Root.AddChild(layer);
            layer.AddChild(new UiDocument
            {
                Name = "ViewDoc",
                AutoFocus = false,
                Rml = """<rml><head><style>body{pointer-events:none;width:100%;height:100%;} img{position:absolute;left:0;top:0;width:100%;height:100%;}</style></head><body><img src="engine://subviewport-post"/></body></rml>""",
            });
        }

        if (Host.Count == 3)
        {
            var angle = gameTime.FrameCount * 0.01f;
            _camera.Position = new Vector3(MathF.Sin(angle) * 4.2f, 2.2f, MathF.Cos(angle) * 4.2f);
            _camera.LookAt(CameraTarget);
        }

        // Immediate mode: the lines are drawn this frame, then cleared. A patch of 24 lines, about 12 pixels square.
        for (var i = 0; i < 24; i++)
        {
            var y = (i - 12) * 0.004f;
            _view.DebugLines.AddLine(RedPatch + new Vector3(-0.05f, y, 0f), RedPatch + new Vector3(0.05f, y, 0f), new Vector4(1f, 0f, 0f, 1f));
            _view.OverlayLines.AddLine(GreenPatch + new Vector3(-0.05f, y, 0f), GreenPatch + new Vector3(0.05f, y, 0f), new Vector4(0f, 1f, 0f, 1f));
        }

        if (Host.Count != 3 && gameTime.FrameCount is CheckFrame or CheckFrame + 12 or CheckFrame + 13)
            RequestCapture((uint)gameTime.FrameCount);

        if (Host.Count != 3 && gameTime.FrameCount == CheckFrame + 24 && !_checked)
            Check();
    }

    // Its own method: a lambda capturing a local of UpdateScene would allocate its closure on every call (the allocation run).
    private void RequestCapture(uint frame)
    {
        _pendingCaptures++;
        _view.CaptureImage(capture =>
        {
            _captures.Add((frame, capture.Pixels.ToArray(), capture.Width, capture.Height));
            _pendingCaptures--;
        });
    }

    private void Check()
    {
        _checked = true;
        if (_captures.Count != 3 || _pendingCaptures != 0)
        {
            Fail($"{_captures.Count} of 3 view captures arrived.");
            return;
        }

        _captures.Sort(static (a, b) => a.Frame.CompareTo(b.Frame));
        var (_, pixels, width, height) = _captures[0];
        var red = Pixel(pixels, width, height, RedPatch);
        var green = Pixel(pixels, width, height, GreenPatch);
        Log.Info($"Sub-viewport post ({(PostOn ? "on" : "off")}): red patch {red}, green patch {green}");
        if (PostOn)
        {
            // Debug and overlay lines keep their exact colour: drawn after the grade, the vignette and the glow.
            if (!(red.X >= 250 && red.Y <= 5 && red.Z <= 5))
                Fail($"The red debug lines are {red}: graded or missing.");
            if (!(green.Y >= 250 && green.X <= 5 && green.Z <= 5))
                Fail($"The green overlay lines are {green}: graded or missing.");
        }
        else if (!(red.X > red.Y + 150 && green.Y > green.X + 60 && green.Y > green.Z + 60))
        {
            // Without post-processing they are in the scene (tonemapped with it).
            Fail($"The line patches are missing: red {red}, green {green}.");
        }

        var sphere = Pixel(pixels, width, height, SphereProbe);
        var box = Pixel(pixels, width, height, BoxProbe);
        Log.Info($"Sub-viewport post ({(PostOn ? "on" : "off")}): sphere {sphere}, box {box}");
        if (PostOn)
        {
            // The profile's saturation 0.02: grey.
            if (Chroma(sphere) > 8 || Chroma(box) > 8)
                Fail($"With post-processing the scene should be grey: sphere {sphere}, box {box}.");
            var corner = Pixel(pixels, width, height, 2, 2);
            var top = Pixel(pixels, width, height, width / 2, 2); // the same sky, nearer the centre
            if (!(Luma(corner) < Luma(top) * 0.85f))
                Fail($"No vignette: the sky's corner {corner}, its top centre {top}.");
        }
        else if (!(sphere.Z > sphere.X + 40 && box.X > box.Z + 40))
        {
            Fail($"Without post-processing the scene keeps its colours: sphere {sphere}, box {box}.");
        }

        if (PostOn)
        {
            // The view registers the main view's built-in effects (VulkanRenderer.RegisterPostEffects), in the same order.
            var main = Servers.Render!.PostEffects!.Effects.Select(e => e.GetType().Name);
            var view = _view.Targets?.Post?.Effects.Effects.Select(e => e.GetType().Name) ?? [];
            if (!main.SequenceEqual(view))
                Fail($"The view's effects ({string.Join(", ", view)}) are not the main view's ({string.Join(", ", main)}).");
        }

        if (Taa)
        {
            var taa = _view.Targets?.Post?.Effects.Find<TaaEffect>();
            if (taa is null || taa.AccumulatedFrames < 10)
                Fail($"TAA did not accumulate a history in the view ({taa?.AccumulatedFrames ?? 0} frames).");
            // The overlay pass has an unjittered camera: the line patches do not move from frame to frame.
            var (_, a, _, _) = _captures[1];
            var (_, b, _, _) = _captures[2];
            foreach (var patch in (ReadOnlySpan<Vector3>)[RedPatch, GreenPatch])
            {
                for (var dy = -0.04f; dy <= 0.04f; dy += 0.02f)
                {
                    var probe = patch + new Vector3(0.03f, dy, 0f); // inside the patch: every pixel is a line
                    if (Pixel(a, width, height, probe) != Pixel(b, width, height, probe))
                        Fail($"A line patch changed between two frames at {probe}: the overlay is jittered.");
                }
            }
        }
    }

    private static int Chroma(Vector3 c) => (int)(MathF.Max(c.X, MathF.Max(c.Y, c.Z)) - MathF.Min(c.X, MathF.Min(c.Y, c.Z)));

    private static float Luma(Vector3 c) => 0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z;

    // The view pixel a world point projects to.
    private Vector3 Pixel(byte[] pixels, int width, int height, Vector3 world)
    {
        var camera = _camera.SyncRenderCamera((float)width / height);
        var clip = Vector4.Transform(new Vector4(world, 1f), camera.ViewMatrix * camera.ProjectionMatrix);
        var ndc = new Vector2(clip.X, clip.Y) / clip.W;
        return Pixel(pixels, width, height, (int)((ndc.X * 0.5f + 0.5f) * width), (int)((0.5f - ndc.Y * 0.5f) * height));
    }

    private static Vector3 Pixel(byte[] pixels, int width, int height, int x, int y)
    {
        x = Math.Clamp(x, 0, width - 1);
        y = Math.Clamp(y, 0, height - 1);
        var i = (y * width + x) * 4;
        return new Vector3(pixels[i], pixels[i + 1], pixels[i + 2]);
    }

    protected override void DisposeScene()
    {
        if (Host.Count != 3 && !_checked)
            Fail($"The sub-viewport post self-check did not run (frame {CheckFrame + 24}).");
    }
}

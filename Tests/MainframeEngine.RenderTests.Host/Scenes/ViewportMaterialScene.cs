using System.Numerics;

namespace MainframeEngine.RenderTests.Host.Scenes;

using Color = System.Drawing.Color;

/// <summary>
/// Sub-viewport textures on 3D materials (ADR 0216): four 16:9 screens facing the camera. The top two are unshaded
/// <see cref="StandardMaterial3D"/>s: the left shows a 2D sub-viewport (<see cref="SubViewport.Disable3D"/>) drawing four
/// coloured quadrants, the right a 3D sub-viewport's camera view (an orange box up and to the left on a teal clear colour).
/// Bottom left: a spatial <see cref="ShaderMaterial"/> (<c>spatial_screen.gdshader</c>, channels rotated rgb → gbr)
/// sampling the 2D view; bottom right: a lit black material whose <see cref="StandardMaterial3D.EmissionTexture"/> is the
/// 3D view. Until <see cref="ResizeFrame"/> the panel is plain grey and the box hidden; then both views double in size and
/// show their content, so the screens can only show it from the new images. <c>--count 1</c> spins the 3D view's box every
/// frame (the allocation run).
/// </summary>
public sealed class ViewportMaterialScene(HostOptions host) : RenderTestGame(host)
{
    public const uint ResizeFrame = 6;
    public const uint CheckFrame = 12;

    public static readonly Color TopLeft = Color.FromArgb(255, 230, 40, 40);
    public static readonly Color TopRight = Color.FromArgb(255, 40, 200, 60);
    public static readonly Color BottomLeft = Color.FromArgb(255, 50, 80, 230);
    public static readonly Color BottomRight = Color.FromArgb(255, 240, 240, 240);
    public static readonly Color ViewClear = Color.FromArgb(255, 30, 110, 120);
    public static readonly Color Box = Color.FromArgb(255, 250, 150, 20);

    /// <summary>The main camera: at <see cref="CameraZ"/> on +Z looking down -Z, <see cref="Fov"/>° vertically.</summary>
    public const float CameraZ = 2.6f, Fov = 60f;

    /// <summary>Screen size (m) and the centres of the four screens (z = 0).</summary>
    public static readonly Vector2 ScreenSize = new(1.6f, 0.9f);

    public static readonly Vector3 PanelAlbedo = new(-0.9f, 0.55f, 0f), ViewAlbedo = new(0.9f, 0.55f, 0f),
        PanelShader = new(-0.9f, -0.55f, 0f), ViewEmission = new(0.9f, -0.55f, 0f);

    /// <summary>Where the box's front face is in the 3D view, in UV (0..1, v down).</summary>
    public static readonly Vector2 BoxUv = new(0.354f, 0.37f);

    private SubViewport _panel = null!;
    private SubViewport _view = null!;
    private Pattern _pattern = null!;
    private MeshInstance3D _box = null!;

    /// <summary>The window pixel of a point on a screen, given in its UV (0..1, v down).</summary>
    public static Vector2 ToPixel(Vector3 screenCentre, Vector2 uv, int width, int height)
    {
        var point = screenCentre + new Vector3((uv.X - 0.5f) * ScreenSize.X, (0.5f - uv.Y) * ScreenSize.Y, 0f);
        var halfHeight = CameraZ * MathF.Tan(Fov * MathF.PI / 360f);
        var halfWidth = halfHeight * width / height;
        return new Vector2((point.X / halfWidth + 1f) * 0.5f * width, (1f - point.Y / halfHeight) * 0.5f * height);
    }

    protected override void LoadScene()
    {
        var root = new Node3D { Name = nameof(ViewportMaterialScene) };
        var camera = new Camera3D { Name = "Camera", Position = new Vector3(0, 0, CameraZ), Fov = Fov, Current = true };
        root.AddChild(camera);

        _panel = new SubViewport { Name = "Panel", Width = 128, Height = 72, Disable3D = true };
        _pattern = new Pattern(_panel) { Name = "Pattern" };
        _panel.AddChild(_pattern);
        root.AddChild(_panel);

        _view = new SubViewport { Name = "View", Width = 160, Height = 90, ClearColor = ViewClear };
        var viewCamera = new Camera3D { Name = "ViewCamera", Position = new Vector3(0, 0, 3f), Fov = 60f, Current = true };
        _view.AddChild(viewCamera);
        _box = new MeshInstance3D
        {
            Name = "Box",
            Position = new Vector3(-0.9f, 0.45f, 0f),
            Mesh = new BoxMesh { Size = new Vector3(0.6f, 0.6f, 0.6f) },
            MaterialOverride = new StandardMaterial3D { ShadingMode = ShadingMode.Unshaded, AlbedoColor = Box },
        };
        _box.Visible = false; // until the resize
        _view.AddChild(_box);
        root.AddChild(_view);

        root.AddChild(Screen("PanelAlbedo", PanelAlbedo,
            new StandardMaterial3D { ShadingMode = ShadingMode.Unshaded, AlbedoTexture = _panel.GetTexture() }));
        root.AddChild(Screen("ViewAlbedo", ViewAlbedo,
            new StandardMaterial3D { ShadingMode = ShadingMode.Unshaded, AlbedoTexture = _view.GetTexture() }));
        var shader = new ShaderMaterial { Shader = Shader.Load(ContentPaths.Resolve("Assets/Shaders/spatial_screen.gdshader")) };
        shader.SetShaderParameter("screen", _panel.GetTexture());
        root.AddChild(Screen("PanelShader", PanelShader, shader));
        root.AddChild(Screen("ViewEmission", ViewEmission, new StandardMaterial3D
        {
            AlbedoColor = Color.Black,
            Specular = 0f,
            EmissionColor = Color.White,
            EmissionEnergy = 1f,
            EmissionTexture = _view.GetTexture(),
        }));
        Tree.ChangeScene(root);
    }

    private static MeshInstance3D Screen(string name, Vector3 position, Material material) => new()
    {
        Name = name,
        Position = position,
        Mesh = new QuadMesh { Size = ScreenSize },
        MaterialOverride = material,
    };

    protected override void UpdateScene(in GameTime gameTime)
    {
        if (gameTime.FrameCount == ResizeFrame)
        {
            _panel.Width *= 2;
            _panel.Height *= 2;
            _pattern.Resized = true;
            _pattern.QueueRedraw();
            _view.Width *= 2;
            _view.Height *= 2;
            _box.Visible = true;
        }

        if (Host.Count == 1)
            _box.RotationDegrees = new Vector3(0f, gameTime.FrameCount * 3f, 0f);
    }

    protected override void DisposeScene()
    {
    }

    /// <summary>Four quadrants filling the panel; plain grey before the resize.</summary>
    private sealed class Pattern(SubViewport panel) : Node2D
    {
        public bool Resized { get; set; }

        protected override void OnDraw()
        {
            var half = new Vector2(panel.Width, panel.Height) * 0.5f;
            if (!Resized)
            {
                DrawRect(new Rect2(Vector2.Zero, half * 2f), new Vector4(0.5f, 0.5f, 0.5f, 1f));
                return;
            }

            DrawRect(new Rect2(Vector2.Zero, half), Rgba(TopLeft));
            DrawRect(new Rect2(new Vector2(half.X, 0), half), Rgba(TopRight));
            DrawRect(new Rect2(new Vector2(0, half.Y), half), Rgba(BottomLeft));
            DrawRect(new Rect2(half, half), Rgba(BottomRight));
        }

        private static Vector4 Rgba(Color c) => new(c.R / 255f, c.G / 255f, c.B / 255f, 1f);
    }
}

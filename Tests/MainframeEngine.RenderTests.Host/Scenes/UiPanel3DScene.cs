using System.Numerics;

namespace MainframeEngine.RenderTests.Host.Scenes;

using Color = System.Drawing.Color;

/// <summary>
/// World-space UI panels (G11.5/G11.6, ADRs 0252/0253). A 512×320 terminal panel turned 25° from the camera shows a
/// gradient header, a rounded clip mask over stripes and a blurred box (<c>Assets/UI/panel.rml</c>); a small swatch panel
/// faces the camera, red until <see cref="CheckFrame"/>, when its document turns green: the frame captured then must show
/// green (the panel renders before the scene pass, so the scene samples the same frame's UI). A 3D sub-viewport (an orange
/// box on teal) is on a monitor mesh through the same borrowed-image path. <c>--count 1</c> changes the terminal's text
/// every frame (the allocation run).
/// </summary>
public sealed class UiPanel3DScene(HostOptions host) : RenderTestGame(host)
{
    public const uint CheckFrame = 14;

    /// <summary>The camera at <see cref="CameraZ"/> on +Z looks down -Z, <see cref="Fov"/>° vertically.</summary>
    public const float CameraZ = 3f, Fov = 60f;

    /// <summary>The swatch panel's centre (it faces the camera) and size in metres.</summary>
    public static readonly Vector3 SwatchCentre = new(1.15f, -0.65f, 0f);

    public const float SwatchSize = 0.4f;

    /// <summary>The monitor's centre (faces the camera).</summary>
    public static readonly Vector3 MonitorCentre = new(1.15f, 0.55f, 0f);

    public static readonly Vector2 MonitorSize = new(0.8f, 0.45f);

    private UiDocument _swatch = null!;
    private Terminal _terminal = null!;

    /// <summary>The window pixel of a point on the z = 0 plane.</summary>
    public static Vector2 ToPixel(Vector3 point, int width, int height)
    {
        var halfHeight = CameraZ * MathF.Tan(Fov * MathF.PI / 360f);
        var halfWidth = halfHeight * width / height;
        return new Vector2((point.X / halfWidth + 1f) * 0.5f * width, (1f - point.Y / halfHeight) * 0.5f * height);
    }

    protected override void LoadScene()
    {
        var root = new Node3D { Name = nameof(UiPanel3DScene) };
        root.AddChild(new Camera3D { Name = "Camera", Position = new Vector3(0, 0, CameraZ), Fov = Fov, Current = true });
        root.AddChild(new MeshInstance3D
        {
            Name = "Wall",
            Position = new Vector3(0, 0, -0.6f),
            Mesh = new QuadMesh { Size = new Vector2(8, 6) },
            MaterialOverride = new StandardMaterial3D { ShadingMode = ShadingMode.Unshaded, AlbedoColor = Color.FromArgb(255, 60, 62, 70) },
        });

        var terminal = new UiPanel3D
        {
            Name = "Terminal",
            Resolution = new Vector2I(512, 320),
            PixelSize = 0.004f,
            Position = new Vector3(-0.55f, 0.1f, 0f),
            RotationDegrees = new Vector3(0, 25, 0),
            Background = Color.FromArgb(255, 24, 28, 36),
        };
        _terminal = new Terminal { Name = "Ui", Source = "Assets/UI/panel.rml" };
        terminal.AddChild(_terminal);
        root.AddChild(terminal);

        var swatch = new UiPanel3D
        {
            Name = "Swatch",
            Resolution = new Vector2I(100, 100),
            PixelSize = SwatchSize / 100f,
            Position = SwatchCentre,
            Background = Color.Black,
        };
        _swatch = new UiDocument
        {
            Name = "Swatch",
            Rml = "<rml><head><style>body { width: 100%; height: 100%; background-color: #ff0000; }</style></head><body id=\"swatch\"></body></rml>",
        };
        swatch.AddChild(_swatch);
        root.AddChild(swatch);

        var view = new SubViewport { Name = "View", Width = 160, Height = 90, ClearColor = Color.FromArgb(255, 30, 110, 120) };
        view.AddChild(new Camera3D { Name = "ViewCamera", Position = new Vector3(0, 0, 3f), Fov = 60f, Current = true });
        view.AddChild(new MeshInstance3D
        {
            Name = "Box",
            Position = new Vector3(-0.9f, 0.45f, 0f),
            Mesh = new BoxMesh { Size = new Vector3(0.6f, 0.6f, 0.6f) },
            MaterialOverride = new StandardMaterial3D { ShadingMode = ShadingMode.Unshaded, AlbedoColor = Color.FromArgb(255, 250, 150, 20) },
        });
        root.AddChild(view);
        root.AddChild(new MeshInstance3D
        {
            Name = "Monitor",
            Position = MonitorCentre,
            Mesh = new QuadMesh { Size = MonitorSize },
            MaterialOverride = new StandardMaterial3D { ShadingMode = ShadingMode.Unshaded, AlbedoTexture = view.GetTexture() },
        });
        Tree.ChangeScene(root);
    }

    protected override void UpdateScene(in GameTime gameTime)
    {

        // Before this frame's UI update: the capture of this frame must already show it.
        if (gameTime.FrameCount == CheckFrame && _swatch.Document is { IsNull: false } document)
            document.AsElement().SetProperty("background-color", "#00ff00");
        if (Host.Count == 1 || gameTime.FrameCount <= 2)
            _terminal.Frame = (int)gameTime.FrameCount;
    }

    protected override void DisposeScene()
    {
    }

    private sealed class Terminal : UiDocument
    {
        private UI.Rml.RmlDataModel? _model;

        public int Frame
        {
            get;
            set
            {
                if (field == value)
                    return;
                field = value;
                _model?.Dirty("frame");
            }
        }

        protected override void OnReady() => _model = CreateDataModel("panel").Bind("frame", this, static t => t.Frame);
    }
}

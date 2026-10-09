namespace MainframeEngine.RenderTests.Host.Scenes;

/// <summary>
/// Reference-resolution UI scaling (ADR 0181): a document authored for a 640×360 reference in a
/// <see cref="UiScaleMode.Project"/> layer, with the server scaling with the screen. Run at two window sizes (the test
/// resizes mid-run), the UI covers the same fraction of the frame at both; the scene checks the dp ratio follows the
/// framebuffer height every frame.
/// </summary>
public sealed class UiScalingScene(HostOptions host) : RenderTestGame(host)
{
    /// <summary>The reference resolution the document is authored for.</summary>
    public const float ReferenceWidth = 640, ReferenceHeight = 360;

    /// <summary>The box, in dp of the reference: left, top, width, height.</summary>
    public const float BoxX = 40, BoxY = 30, BoxWidth = 240, BoxHeight = 120;

    /// <summary>The box's opaque sRGB colour (#e0457b).</summary>
    public static readonly byte[] BoxColor = [0xe0, 0x45, 0x7b];

    private const string Document = """
        <rml><head><style>
        body { width: 100%; height: 100%; background-color: #1b1d23; font-family: LatoLatin; color: #ffffff; }
        div { display: block; }
        #box { position: absolute; left: 40dp; top: 30dp; width: 240dp; height: 120dp; background-color: #e0457b; }
        #label { position: absolute; left: 40dp; top: 170dp; width: 560dp; font-size: 32dp; }
        #bar { position: absolute; left: 320dp; top: 30dp; width: 280dp; height: 120dp; border: 4dp #93c5fd; border-radius: 16dp;
               background-color: #2563eb80; }
        #foot { position: absolute; left: 40dp; bottom: 30dp; width: 560dp; height: 60dp; background-color: #22c55e; border-radius: 30dp; }
        </style></head><body>
        <div id="box"/><div id="bar"/>
        <div id="label">Scale with screen size: 640×360</div>
        <div id="foot"/>
        </body></rml>
        """;

    private UiLayer _layer = null!;

    protected override void LoadScene()
    {
        Tree.Servers.Get<UiServer>()!.Scaling = UiScaling.ScaleWithScreenSize with
        {
            ReferenceResolution = new System.Numerics.Vector2(ReferenceWidth, ReferenceHeight),
        };
        _layer = new UiLayer { Name = "Ui" };
        _layer.AddChild(new UiDocument { Name = "Document", Rml = Document, AutoFocus = false });
        Tree.ChangeScene(_layer);
    }

    protected override void UpdateScene(in GameTime gameTime)
    {
        if (_layer.Context is not { } context || gameTime.FrameCount < 3)
            return;
        var expected = FramebufferSize.Y / ReferenceHeight;
        var ratio = context.DensityIndependentPixelRatio;
        // The ratio is applied in the UI server's frame step, after this update: allow the frame of a resize to lag.
        if (MathF.Abs(ratio - expected) > 1e-4f && MathF.Abs(ratio - _lastExpected) > 1e-4f)
            Fail($"frame {gameTime.FrameCount}: dp ratio {ratio} for a {FramebufferSize.X}x{FramebufferSize.Y} framebuffer, expected {expected}.");
        _lastExpected = expected;
    }

    private float _lastExpected;

    protected override void DisposeScene()
    {
    }
}

using System.Drawing;

namespace MainframeEngine.RenderTests.Host.Scenes;

/// <summary>
/// The effects document (clip masks, transforms, filters, gradients) in a layer limited to a rectangle of the window
/// (<see cref="UiLayer.Region"/>, as the editor's UI preview uses it), over a full-window layer: the document is laid
/// out at the region's size, drawn at its origin and clipped to it — its right column is cut off.
/// </summary>
public sealed class UiRegionScene(HostOptions host) : RenderTestGame(host)
{
    private const string Backdrop = """
        <rml><head><style>
        body { width: 100%; height: 100%; background-color: #1b1d23; }
        </style></head><body></body></rml>
        """;

    private UiLayer _region = null!;

    protected override void LoadScene()
    {
        var root = new Node { Name = "Root" };
        var back = new UiLayer { Name = "Back", Layer = 0 };
        back.AddChild(new UiDocument { Name = "Backdrop", Rml = Backdrop, AutoFocus = false });
        _region = new UiLayer { Name = "Region", Layer = 1 };
        _region.AddChild(new UiDocument { Name = "Document", Source = "Content/UI/effects.rml", AutoFocus = false });
        root.AddChild(back);
        root.AddChild(_region);
        Tree.ChangeScene(root);
        PlaceRegion();
    }

    protected override void UpdateScene(in GameTime gameTime) => PlaceRegion();

    // 20 % in from the left, 15 % from the top, 70 % × 80 % of the framebuffer (pixels).
    private void PlaceRegion()
    {
        var size = FramebufferSize;
        var region = new Rectangle((int)(size.X * 0.2f), (int)(size.Y * 0.15f), (int)(size.X * 0.7f), (int)(size.Y * 0.8f));
        if (_region.Region != region)
            _region.Region = region;
    }

    protected override void DisposeScene()
    {
    }
}

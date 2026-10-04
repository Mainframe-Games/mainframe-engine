namespace MainframeEngine.RenderTests.Host.Scenes;

/// <summary>
/// The lit-shapes 3D scene with an RmlUi HUD on top (game UI over the tonemapped scene): a semi-transparent panel with
/// data-bound text and a gradient progress bar, exact sRGB swatches (opaque, 50 % white, rounded), an image and a line
/// of small text, and the scene's own HDR target as an <c>engine://scene</c> picture-in-picture. Frame N always shows
/// the same values (fixed delta; the frame counter is bound).
/// </summary>
public sealed class UiHudScene(HostOptions host) : LitShapesScene(host)
{
    /// <summary>The opaque swatch colour (sRGB bytes) the test checks exactly.</summary>
    public static readonly byte[] SwatchColor = [0x33, 0x66, 0xCC];

    protected override void AddNodes(Node scene) => scene.AddChild(CreateHudLayer());

    /// <summary>The HUD layer (also added to the allocation-gate scene): its bindings are dirtied every frame.</summary>
    public static UiLayer CreateHudLayer()
    {
        var layer = new UiLayer { Name = "Hud", Layer = 0 };
        layer.AddChild(new HudDocument { Name = "HudDocument", Source = "Content/UI/hud.rml", AutoFocus = false });
        return layer;
    }

    private sealed class HudDocument : UiDocument
    {
        private uint _frame;
        private int _health = 72;
        private UI.Rml.RmlDataModel _model = null!;

        protected override void OnReady()
        {
            // engine:// textures must be registered before the document loads them.
            if (Tree?.Servers.Get<UiServer>() is { } ui && Tree.Servers.Render?.Vulkan is { } vk)
                ui.RegisterTexture("scene", vk.SceneTarget);
            _model = CreateDataModel("hud")
                .Bind("frame", this, static d => (int)d._frame)
                .Bind("health", this, static d => d._health);
        }

        protected override void OnProcess(in GameTime gameTime)
        {
            _frame = gameTime.FrameCount;
            _health = 40 + (int)(_frame % 50);
            _model.Dirty("frame");
            _model.Dirty("health");
        }
    }
}

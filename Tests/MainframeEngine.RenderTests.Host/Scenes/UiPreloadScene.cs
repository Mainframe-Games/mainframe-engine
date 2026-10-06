namespace MainframeEngine.RenderTests.Host.Scenes;

/// <summary>
/// Image preloading (ADR 0140): a document with an image shown from the start and images hidden until frame 20. The
/// hidden ones are decoded when the document loads, so showing them later decodes nothing (no main-thread hitch) and
/// allocates far less than their 360 KB of pixels. Self-checks only (no golden).
/// </summary>
public sealed class UiPreloadScene(HostOptions host) : UiDocumentScene(host, "")
{
    private const string Rml = """
        <rml>
        <head><style>body { width: 100%; height: 100%; } img { width: 64dp; height: 64dp; }</style></head>
        <body>
          <img id="shown" src="/Content/UI/logo.png"/>
          <div id="later" style="display: none;"><img src="/Content/UI/logo.png"/><img src="../UI/logo.png"/></div>
        </body>
        </rml>
        """;

    private const uint ShowFrame = 20, CheckFrame = 30;
    private long _bytesAtShow;
    private int _fromPreloadAtShow, _onDemandAtShow;

    protected override UiDocument CreateDocument(string path) => new() { Name = "Preload", Rml = Rml, AutoFocus = false };

    private VulkanUiRenderer? Renderer2D => Ui?.RenderInterface as VulkanUiRenderer;

    protected override void UpdateScene(in GameTime gameTime)
    {
        var frame = gameTime.FrameCount;
        if (Renderer2D is not { } ui)
        {
            if (frame == 2)
                Fail("No VulkanUiRenderer.");
            return;
        }

        // RmlUi asks for the shown image while it loads the document (before any preload: no gain to have). RmlUi caches
        // textures by source, so the hidden image with the same source shares it, and the other spelling of the file
        // (UI/logo.png) waits decoded.
        if (frame == ShowFrame - 1 && ui.PendingPreloads != 1)
            Fail($"Before the show: {ui.PendingPreloads} preloaded images pending; expected 1.");

        if (frame == ShowFrame)
        {
            _fromPreloadAtShow = ui.ImagesFromPreload;
            _onDemandAtShow = ui.ImagesDecodedOnDemand;
            Document.GetElementById("later")!.SetProperty("display", "block");
            _bytesAtShow = GC.GetAllocatedBytesForCurrentThread();
        }

        if (frame == CheckFrame)
        {
            var bytes = GC.GetAllocatedBytesForCurrentThread() - _bytesAtShow;
            var fromPreload = ui.ImagesFromPreload - _fromPreloadAtShow;
            var onDemand = ui.ImagesDecodedOnDemand - _onDemandAtShow;
            if (fromPreload != 1 || onDemand != 0 || ui.PendingPreloads != 0)
                Fail($"After the show: {fromPreload} from preload, {onDemand} decoded on demand, {ui.PendingPreloads} pending; expected 1, 0, 0.");
            if (bytes > 64 * 1024)
                Fail($"Showing a preloaded image allocated {bytes} B over {CheckFrame - ShowFrame} frames; its pixels are 360 KB.");
        }
    }
}

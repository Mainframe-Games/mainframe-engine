using MainframeEngine.Tests.UI;

namespace MainframeEngine.Tests.Debugging;

[Collection(nameof(SerialRmlUi))]
public sealed class DevOverlayPanelsTests
{
    [Fact]
    public void BuiltInsAreTheSevenPanelsInOrder()
    {
        using var ui = new UiTestTree();
        var overlay = new DevOverlay(ui.Tree);
        DevOverlayPanels.AddBuiltIns(overlay, engine: null);
        Assert.Equal(["frame", "renderer", "shadows", "gpu", "audio", "physics", "network"], overlay.Panels.Select(p => p.Id));
    }

    [Fact]
    public void PanelsWithoutTheirServerShowPlaceholdersAndDoNotThrow()
    {
        using var ui = new UiTestTree();
        var overlay = new DevOverlay(ui.Tree);
        ui.Servers.Register(overlay);
        DevOverlayPanels.AddBuiltIns(overlay, engine: null);
        var sink = new MemoryLogSink { Levels = Log.Level.Warning | Log.Level.Error };
        Log.AddSink(sink);
        try
        {
            overlay.Visible = true;
            for (var i = 0; i < 30; i++)
            {
                ui.Tick();
            }
        }
        finally
        {
            Log.RemoveSink(sink);
        }

        // No renderer/audio/physics/network servers registered: panels render "—" and RmlUi logs no warning or error.
        Assert.DoesNotContain(sink.Snapshot(), e => e.Category is "RmlUi" or "UI");
        var document = (UiDocument)ui.Tree.Root.GetNode<UiLayer>("DevOverlay").GetChild(0);
        foreach (var id in new[] { "frame", "renderer", "shadows", "gpu", "audio", "physics", "network" })
            Assert.NotNull(document.QuerySelector($"[data-model=dev_{id}]"));
    }

    [Fact]
    public void FramePanelShowsTheAccumulatedFrameTimes()
    {
        using var ui = new UiTestTree();
        var overlay = new DevOverlay(ui.Tree);
        ui.Servers.Register(overlay);
        DevOverlayPanels.AddBuiltIns(overlay, engine: null);
        overlay.Visible = true;
        for (var i = 0; i < 30; i++)
        {
            ui.Tick();
        }

        var document = (UiDocument)ui.Tree.Root.GetNode<UiLayer>("DevOverlay").GetChild(0);
        var text = document.QuerySelector("[data-model=dev_frame]")!.InnerRml;
        Assert.Contains("60.0", text); // the tree ticks the overlay at 1/60 s
        Assert.Contains("16.67 / 16.67 / 16.67", text);
    }

    [Fact]
    public void OverlayTreeCanBeRefreshedWithServersPresent()
    {
        using var ui = new UiTestTree();
        var overlay = new DevOverlay(ui.Tree);
        ui.Servers.Register(overlay);
        using var api = global::MainframeEngine.Networking.MultiplayerApi.Attach(ui.Tree);
        DevOverlayPanels.AddBuiltIns(overlay, engine: null);
        overlay.Visible = true;
        for (var i = 0; i < 20; i++)
        {
            ui.Tick();
        }

        var document = (UiDocument)ui.Tree.Root.GetNode<UiLayer>("DevOverlay").GetChild(0);
        // An idle MultiplayerApi: the network panel's body stays hidden.
        Assert.Contains("Offline", document.QuerySelector("[data-model=dev_network]")!.InnerRml);
    }

    [Fact]
    public void AudioPanelListsTheBusesAndWritesFadersBack()
    {
        using var ui = new UiTestTree();
        var overlay = new DevOverlay(ui.Tree);
        ui.Servers.Register(overlay);
        using var audio = Audio.AudioTestUtil.CreateServer(ui.Tree);
        DevOverlayPanels.AddBuiltIns(overlay, engine: null);
        overlay.Visible = true;
        ui.Tick(10);

        var document = (UiDocument)ui.Tree.Root.GetNode<UiLayer>("DevOverlay").GetChild(0);
        Assert.Equal(audio.Buses.Count, document.Document.AsElement().QuerySelectorAll(".dev-bus-name").Length);
        Assert.Contains(audio.DeviceName, document.QuerySelector("[data-model=dev_audio]")!.InnerRml, StringComparison.Ordinal);

        // A bus muted elsewhere shows up as a checked box after the next refresh.
        audio.Buses[1].Mute = true;
        ui.Tick(20);
        var boxes = document.Document.AsElement().QuerySelectorAll(".dev-bus input[type=checkbox]");
        Assert.True(boxes[2].IsPseudoClassSet("checked"));
        Assert.False(boxes[0].IsPseudoClassSet("checked"));
    }
}

using MainframeEngine.Audio.Music;
using MainframeEngine.Tests.Audio;
using MainframeEngine.Tests.Audio.Music;
using MainframeEngine.Tests.UI;
using static MainframeEngine.Tests.Audio.Music.MusicTestStems;

namespace MainframeEngine.Tests.Debugging;

/// <summary>
/// The dev overlay's Music panel: it appears once an adaptive music instance plays, shows its section and bar.beat, and
/// its buttons drive the instance (labels, Fire, Next); the snapshot refresh allocates nothing.
/// </summary>
[Collection(nameof(SerialRmlUi))]
public sealed class DevOverlayMusicTests : IDisposable
{
    private readonly MusicTestStems _stems = new();

    public void Dispose() => _stems.Dispose();

    private AdaptiveMusic Theme()
    {
        var manifest = Manifest(
            Section("explore", 1, stems: ("base", _stems.Body(1, 0.25f), null)),
            Section("combat", 1, stems: ("base", _stems.Body(1, 0.5f), null)),
            Section("hit", 1, loop: false, kind: MusicSectionKind.Stinger));
        manifest.Sections[2].Stems[MusicManifestSection.MixStem] = new MusicStemSet { Body = _stems.Impulse((int)Bar, 0, 0.9f) };
        manifest.Parameters.Add(new MusicParameterInfo { Name = "intensity", Kind = MusicParameterKind.Continuous, Max = 1 });
        manifest.Parameters.Add(new MusicParameterInfo { Name = "area", Kind = MusicParameterKind.Labeled, Labels = ["Calm", "Combat"] });
        manifest.Transitions.Add(new MusicTransitionInfo
        {
            From = ["explore"],
            To = "combat",
            Quantize = MusicQuantize.Bar,
            When = new MusicConditionGroup { All = [new MusicComparison { Parameter = "area", Op = MusicCompare.Equal, Value = new MusicValue("Combat") }] },
        });
        return InMemory(manifest);
    }

    [Fact]
    public void ThePanelAppearsWhenMusicPlaysAndItsControlsDriveTheInstance()
    {
        using var ui = new UiTestTree();
        var overlay = new DevOverlay(ui.Tree);
        ui.Servers.Register(overlay);
        using var audio = AudioTestUtil.CreateServer(ui.Tree);
        DevOverlayPanels.AddBuiltIns(overlay, engine: null);
        overlay.Visible = true;
        ui.Tick(20);
        Assert.DoesNotContain(overlay.Panels, p => p.Id == DevOverlayMusic.PanelId); // nothing plays yet

        using var instance = new AdaptiveMusicInstance(audio, Theme());
        instance.Play();
        var capture = new float[800 * 2];
        for (var i = 0; i < 30; i++)
        {
            ui.Tick();
            audio.RenderNullDevice(800, capture);
        }

        Assert.Equal(DevOverlayMusic.PanelId, overlay.Panels[^1].Id);
        var document = (UiDocument)ui.Tree.Root.GetNode<UiLayer>("DevOverlay").GetChild(0);
        var body = document.QuerySelector("[data-model=dev_music]")!;
        Assert.Contains("explore", body.InnerRml, StringComparison.Ordinal);
        Assert.Contains("Fire hit", body.InnerRml, StringComparison.Ordinal);

        // The area's labels are buttons: Combat sets the parameter, and the rule schedules Combat.
        Button(document, "Combat").Click();
        Assert.Equal(1f, instance.Director!.Parameters.GetTarget(1));
        for (var i = 0; i < 40 && instance.CurrentSection != "combat"; i++)
        {
            ui.Tick();
            audio.RenderNullDevice(800, capture);
        }

        Assert.Equal("combat", instance.CurrentSection);

        // Next takes the first transition from Combat: there is none that matches, and none from Combat at all: nothing.
        Button(document, "Next").Click();
        Assert.Null(instance.PendingTransition);

        // Fire plays the stinger (its impulse over the body).
        Button(document, "Fire hit").Click();
        var heard = false;
        for (var i = 0; i < 40 && !heard; i++)
        {
            ui.Tick();
            audio.RenderNullDevice(800, capture);
            heard = AudioTestUtil.Peak(capture, 0) > 0.8f;
        }

        Assert.True(heard);
    }

    [Fact]
    public void NextTakesTheMatchingTransitionElseTheFirstFromTheSection()
    {
        using var audio = AudioTestUtil.CreateServer();
        using var instance = new AdaptiveMusicInstance(audio, Theme());
        instance.Play();
        var capture = new float[800 * 2];
        for (var i = 0; i < 4; i++)
        {
            audio.Process(AudioTestUtil.Frame);
            audio.RenderNullDevice(800, capture);
        }

        var tree = new SceneTree();
        tree.Servers.Register(audio); // the panel reads the server of its tree
        var stats = new MusicStats(new DevOverlaySources(tree, null));
        stats.Refresh();
        Assert.Single(stats.Instances);
        Assert.Equal("explore", stats.Instances[0].Section);
        stats.Next(0); // area is Calm: no rule matches, so the first rule from Explore (to Combat) is taken
        audio.Process(AudioTestUtil.Frame);
        Assert.Equal("combat", instance.PendingTransition);

        // Steady refreshes copy numbers and the manifest's strings only.
        Assert.Equal(0, AllocationGate.SmallestWindow(() =>
        {
            for (var i = 0; i < 30; i++)
                stats.Refresh();
        }));
    }

    private static MainframeEngine.UI.Rml.RmlElement Button(UiDocument document, string text)
    {
        foreach (var button in document.Document.AsElement().QuerySelectorAll("[data-model=dev_music] button"))
        {
            // data-if hides rather than removes: a parameter's label row holds every label, the other parameters' hidden.
            if (button.InnerRml.Trim() == text && !(button.GetAttribute("style") ?? "").Contains("display: none", StringComparison.Ordinal))
                return button;
        }

        throw new InvalidOperationException($"No '{text}' button in the Music panel.");
    }
}

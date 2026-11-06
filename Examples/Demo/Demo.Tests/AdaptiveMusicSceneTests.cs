using System.Numerics;
using MainframeEngine;

namespace Demo.Tests;

/// <summary>
/// The Adaptive music scene plays the committed bake (<c>Assets/Music/adaptive_theme.mfmusic</c> and its stems) on the
/// manual null device: Intro continues to Explore, intensity brings the Perc and Lead layers in, area = Combat moves to
/// Combat on the next bar and back with the Hit stinger, and the panel's controls drive the player.
/// </summary>
[Collection(nameof(SerialRmlUi))]
public sealed class AdaptiveMusicSceneTests : IDisposable
{
    private const int FrameSamples = 800; // 1/60 s at 48 kHz

    private readonly ServerRegistry _servers = new();
    private readonly SceneTree _tree;
    private readonly AudioServer _audio;
    private readonly float[] _capture = new float[FrameSamples * 2];
    private uint _frame;

    public AdaptiveMusicSceneTests()
    {
        _servers.Register(new UiServer(options: new UiServerOptions { HotReload = false, HeadlessViewport = new Vector2(1280, 720) }));
        _audio = AudioServer.Create(new AudioOptions { Device = AudioDeviceMode.NullManual, BusLayoutPath = null }, null);
        _servers.Register(_audio);
        _tree = new SceneTree(_servers);
        _tree.ChangeScene(AdaptiveMusicScene.Build());
        Frame();
    }

    public void Dispose()
    {
        _tree.Shutdown();
        _servers.Dispose();
    }

    private Node Scene => _tree.CurrentScene!;
    private AdaptiveMusicPlayer Player => Scene.GetNode<AdaptiveMusicPlayer>("Music");
    private AdaptiveMusicPanel Panel => Scene.GetNode<AdaptiveMusicPanel>("Ui/Panel");

    // One game frame, then its audio (waiting for the streamed stems, as a real device's buffer would).
    private float Frame()
    {
        TestTime.Tick(_tree, ref _frame);
        _audio.WaitForStreams(FrameSamples * 2, TimeSpan.FromSeconds(2));
        _audio.RenderNullDevice(FrameSamples, _capture);
        var peak = 0f;
        foreach (var sample in _capture)
            peak = MathF.Max(peak, MathF.Abs(sample));
        return peak;
    }

    private void RunUntil(Func<bool> done, int maxFrames, string what)
    {
        for (var i = 0; i < maxFrames; i++)
        {
            if (done())
                return;
            Frame();
        }

        Assert.Fail($"{what} did not happen in {maxFrames} frames (section {Player.CurrentSection}, pending {Player.PendingTransition}).");
    }

    private float LayerDb(string id)
    {
        var director = Player.Instance!.Director!;
        return director.LayerGainDb(director.Plan.FindLayer(id));
    }

    [Fact]
    public void TheBakeLoadsAndPlaysFromTheIntroIntoExplore()
    {
        Assert.Equal(0, Player.Music!.MissingStems);
        Assert.Empty(Player.Music.Plan.Problems);
        Assert.Equal(["main", "perc", "lead"], Player.Music.Plan.Layers.Select(l => l.Id));
        Assert.True(Player.Playing);
        var loudest = 0f;
        RunUntil(() => Player.CurrentSection == "intro", 120, "the intro");
        for (var i = 0; i < 30; i++)
            loudest = MathF.Max(loudest, Frame());
        Assert.True(loudest > 0.05f, $"the intro is silent (peak {loudest})");
        RunUntil(() => Player.CurrentSection == "explore", 60 * 5, "Intro → Explore"); // two bars at 140 BPM: 3.4 s
    }

    [Fact]
    public void IntensityBringsTheLayersInAndAreaChangesTheSectionOnTheBar()
    {
        RunUntil(() => Player.CurrentSection == "explore", 60 * 5, "Explore");
        Panel.Intensity = 0f;
        RunUntil(() => LayerDb("perc") <= -79f && LayerDb("lead") <= -79f, 120, "the layers fading out");
        Assert.Equal(0f, LayerDb("main"));

        Panel.Intensity = 1f; // seeks at 0.6 per second
        RunUntil(() => LayerDb("perc") >= -0.01f && LayerDb("lead") >= -0.01f, 180, "the layers fading in");
        var director = Player.Instance!.Director!;
        Assert.True(director.LayerCutoffHz(director.Plan.FindLayer("lead")) > 15000f); // the lead's filter opened

        Panel.Area = 1;
        RunUntil(() => Player.PendingTransition == "combat", 10, "the transition to be scheduled");
        Assert.InRange(Player.PendingSeconds, 0f, 60f / 140f * 4f); // within a bar
        RunUntil(() => Player.CurrentSection == "combat", 60 * 3, "Explore → Combat");

        Panel.Area = 0; // back at the next bar, with the Hit stinger
        RunUntil(() => Player.PendingTransition == "explore", 10, "the way back to be scheduled");
        RunUntil(() => Player.CurrentSection == "explore", 60 * 3, "Combat → Explore");
    }

    [Fact]
    public void ThePanelFiresTheStingerAndStopsAndRestartsTheMusic()
    {
        RunUntil(() => Player.CurrentSection == "explore", 60 * 5, "Explore");
        var quiet = 0f;
        for (var i = 0; i < 20; i++)
            quiet = MathF.Max(quiet, Frame());
        Panel.Intensity = 0f;
        Panel.Hit(); // on the next beat: louder than the music under it
        var loudest = 0f;
        for (var i = 0; i < 40; i++)
            loudest = MathF.Max(loudest, Frame());
        Assert.True(loudest > quiet * 1.1f, $"the stinger was not heard ({loudest} over {quiet})");
        var document = Panel.Document.AsElement();
        Assert.Contains("Explore", document.InnerRml, StringComparison.Ordinal);
        Assert.Contains("Hit stinger", document.InnerRml, StringComparison.Ordinal);

        Panel.MusicOn = false; // stops at the next bar, the tails ring out
        RunUntil(() => !Player.Playing, 60 * 4, "the stop");
        Panel.MusicOn = true;
        Assert.True(Player.Playing);
    }
}

using System.Numerics;
using MainframeEngine;

namespace Demo.Tests;

[Collection(nameof(SerialRmlUi))]
public sealed class SoundFxSceneTests : IDisposable
{
    private readonly ServerRegistry _servers = new();
    private readonly SceneTree _tree;
    private readonly AudioServer _audio;
    private uint _frame;

    public SoundFxSceneTests()
    {
        _servers.Register(new UiServer(options: new UiServerOptions { HotReload = false, HeadlessViewport = new Vector2(1280, 720) }));
        _audio = AudioServer.Create(new AudioOptions { Device = AudioDeviceMode.NullManual, BusLayoutPath = null }, null);
        _servers.Register(_audio);
        _tree = new SceneTree(_servers);
        _tree.ChangeScene(SoundFxScene.Build());
        Tick();
    }

    public void Dispose()
    {
        _tree.Shutdown();
        _servers.Dispose();
    }

    private Node Scene => _tree.CurrentScene!;
    private SoundFxPanel Panel => Scene.GetNode<SoundFxPanel>("Ui/Panel");
    private AudioPlayer Sfx => Scene.GetNode<AudioPlayer>("Sfx");

    private void Tick() => TestTime.Tick(_tree, ref _frame);

    [Fact]
    public void TheCommittedPresetsAreTheFixedSeedSounds()
    {
        Assert.Equal(7, SoundFxScene.Presets.Count);
        foreach (var preset in SoundFxScene.Presets)
        {
            var saved = ResourceLoader.Load<ZzfxStream>(preset.Path);
            Assert.Equal(preset.Parameters.ToLine(), saved.ToLine());
            Assert.Equal(preset.Name, saved.ResourceName);
        }

        Assert.Equal(SoundFxScene.Presets.Count, SoundFxScene.Presets.Select(p => p.Parameters.ToLine()).Distinct().Count());
    }

    [Fact]
    public void EveryPresetButtonPlaysItsSoundOnTheSfxBus()
    {
        Assert.Equal("SFX", Sfx.Bus);
        Assert.False(Sfx.Playing); // the scene opens quietly, showing the coin
        Assert.NotNull(Panel.Current);
        for (var i = 0; i < SoundFxScene.Presets.Count; i++)
        {
            Panel.PlayPreset(i);
            Assert.Same(Panel.Current, Sfx.Stream);
            Assert.True(Sfx.Playing);
            Assert.Equal(SoundFxScene.Presets[i].Parameters.ToLine(), Panel.Current!.ToLine());
            Tick();
        }
    }

    [Fact]
    public void RandomizeAndMutateMakeNewSoundsAndPlayThem()
    {
        var coin = Panel.Current!.ToLine();
        Panel.Randomize();
        var random = Panel.Current!.ToLine();
        Assert.NotEqual(coin, random);
        Assert.True(Sfx.Playing);
        Panel.Mutate();
        Assert.NotEqual(random, Panel.Current!.ToLine());
        Assert.StartsWith("zzfx(", Panel.Current.ToLine(), StringComparison.Ordinal);
        Assert.True(Scene.GetNode<SoundWave2D>("Wave").Seconds > 0f);
        Tick();
    }

    [Fact]
    public void PitchRandomnessAppliesToTheCurrentAndNextSounds()
    {
        Panel.PitchRandomness = 0.3f;
        Assert.Equal(0.3f, Panel.Current!.PitchRandomness);
        Panel.PlayPreset(1);
        Assert.Equal(0.3f, Panel.Current!.PitchRandomness);
        // The loaded preset itself is not changed.
        Assert.Equal(0.05f, ResourceLoader.Load<ZzfxStream>(SoundFxScene.Presets[1].Path).PitchRandomness);
    }

    [Fact]
    public void TheMusicToggleLoopsTheRenderedSongOnTheMusicBus()
    {
        var music = Scene.GetNode<AudioPlayer>("Music");
        Assert.Equal("Music", music.Bus);
        Assert.Equal(SoundFxScene.MusicFile, music.Stream!.File);
        Assert.True(music.Stream.Loop);
        Assert.True(music.Stream.Preload());
        Assert.InRange(music.Stream.Length, 13.5, 14.0); // 8 bars at 140 BPM

        Panel.MusicOn = true;
        Assert.True(music.Playing);
        Tick();
        Panel.MusicOn = false;
        Assert.False(music.Playing);
    }

    [Fact]
    public void TheSongFileLoadsWithItsLoopImportSettings()
    {
        var stream = ResourceLoader.Load<AudioStream>(SoundFxScene.MusicFile);
        Assert.True(stream.Loop);
        Assert.Equal(AudioLoadMode.Stream, stream.LoadMode);
        Assert.Equal((0f, 0f), (stream.LoopStart, stream.LoopEnd)); // seamless: the whole file loops
    }
}

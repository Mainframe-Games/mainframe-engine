using MainframeEngine;
using MainframeEngine.Localization;
using MainframeEngine.UI.Rml;

namespace Demo;

/// <summary>
/// The Sound FX scene's controls: one button per ZzFX preset (<c>Content/Audio/Sfx/*.mres</c>), Randomize and Mutate
/// (synthesise a new sound and play it), the current sound's ZzFX line, the pitch randomness and the music toggle.
/// </summary>
public sealed class SoundFxPanel : UiDocument
{
    private readonly Random _random = new(2024);
    private RmlDataModel? _model;
    private ZzfxStream? _current;
    private string _kind = "coin";
    private float _randomness = 0.05f;

    public SoundFxPanel()
    {
        Source = "Content/SoundFx/panel.rml";
        AutoFocus = false;
    }

    private Node Scene => Parent!.Parent!;
    private AudioPlayer Sfx => Scene.GetNode<AudioPlayer>("Sfx");
    private AudioPlayer Music => Scene.GetNode<AudioPlayer>("Music");
    private SoundWave2D Wave => Scene.GetNode<SoundWave2D>("Wave");

    /// <summary>The sound the buttons last made (a copy: the loaded presets stay unchanged).</summary>
    public ZzfxStream? Current => _current;

    /// <summary>ZzFX's <c>randomness</c> for the next sounds (and the current one).</summary>
    public float PitchRandomness
    {
        get => _randomness;
        set
        {
            // Rounded to the slider's step: its value comes back as a double (0.05 → 0.049999997).
            _randomness = Math.Clamp(MathF.Round(value, 2), 0f, 1f);
            if (_current is not null)
                _current.PitchRandomness = _randomness;
            _model?.Dirty("line");
        }
    }

    public bool MusicOn
    {
        get => Music.Playing;
        set
        {
            if (value == Music.Playing)
                return;
            if (value)
                Music.Play();
            else
                Music.Stop();
            _model?.Dirty("music");
        }
    }

    protected override void OnReady()
    {
        var model = _model = CreateDataModel("soundfx")
            .Bind("name", this, static d => d.DisplayName)
            .Bind("line", this, static d => d._current?.ToLine() ?? "")
            .Bind("randomness", this, static d => d.PitchRandomness, static (d, v) => d.PitchRandomness = v)
            .Bind("music", this, static d => d.MusicOn, static (d, v) => d.MusicOn = v)
            .Event("randomize", Randomize)
            .Event("mutate", Mutate);
        for (var i = 0; i < SoundFxScene.Presets.Count; i++)
        {
            var index = i;
            model.Event("play_" + SoundFxScene.Presets[i].Id, () => PlayPreset(index));
        }

        Select("coin", Load(SoundFxScene.Presets[0]).Parameters, play: false);
    }

    /// <summary>Plays preset <paramref name="index"/> of <see cref="SoundFxScene.Presets"/> (its saved <c>.mres</c>).</summary>
    public void PlayPreset(int index)
    {
        var preset = SoundFxScene.Presets[index];
        Select(preset.Id, Load(preset).Parameters, play: true);
    }

    /// <summary>A new random sound (<see cref="ZzfxPresets.Randomize"/>), played.</summary>
    public void Randomize() => Select("random", ZzfxPresets.Randomize(_random), play: true);

    /// <summary>A small variation of the current sound (<see cref="ZzfxPresets.Mutate"/>), played.</summary>
    public void Mutate()
    {
        if (_current is { } current)
            Select("mutation", ZzfxPresets.Mutate(current.Parameters, _random), play: true);
    }

    protected override void OnLocaleChanged()
    {
        base.OnLocaleChanged();
        _model?.Dirty("name");
    }

    private static ZzfxStream Load(SoundFxPreset preset) => ResourceLoader.Load<ZzfxStream>(preset.Path);

    private void Select(string kind, in ZzfxParameters parameters, bool play)
    {
        _kind = kind;
        _current = new ZzfxStream { ResourceName = kind, Parameters = parameters with { Randomness = _randomness } };
        Wave.Show(parameters);
        if (play)
        {
            Sfx.Stream = _current;
            Sfx.Play();
            Wave.Flash();
        }

        _model?.Dirty("name");
        _model?.Dirty("line");
    }

    // Literals at the call site so the extractor sees them.
    private string DisplayName => _kind switch
    {
        "coin" => Tr._("Coin"),
        "laser" => Tr._("Laser"),
        "explosion" => Tr._("Explosion"),
        "hit" => Tr._("Hit"),
        "jump" => Tr._("Jump"),
        "blip" => Tr._("Blip"),
        "power_up" => Tr._("Power-up"),
        "random" => Tr._("Random sound"),
        "mutation" => Tr._("Mutation"),
        _ => _kind,
    };
}

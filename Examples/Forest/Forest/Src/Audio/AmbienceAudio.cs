using MainframeEngine;

namespace Forest;

/// <summary>
/// The ambient bed: wind in the trees (<see cref="ForestSoundBank.WindRoar"/>) and leaves rustling in the gusts
/// (<see cref="ForestSoundBank.LeafRustle"/>), two non-positional stereo loops on the <see cref="ForestAudio.AmbienceBus"/>.
/// The gusts are baked into the loops (24 s and 17 s, so the pair repeats only every 408 s); the wind strength of the
/// world's <see cref="WorldEnvironment"/> (the same value that sways the trees) sets their level and the roar's pitch,
/// eased over a couple of seconds. Property sets on two existing players: nothing allocates per frame.
/// </summary>
public sealed class AmbienceAudio : Node
{
    private AudioPlayer? _roar;
    private AudioPlayer? _rustle;
    private float _wind = -1f;

    /// <summary>The roar's level at full wind strength (dB, on top of the Ambience bus).</summary>
    [Export(Range = "-40,6,0.5")] public float RoarVolumeDb { get; set; } = -2f;

    /// <summary>The rustle's level at full wind strength.</summary>
    [Export(Range = "-40,6,0.5")] public float RustleVolumeDb { get; set; } = -5f;

    /// <summary>How much quieter each layer is in calm air (wind strength 0): the bed never goes silent.</summary>
    [Export(Range = "0,40,0.5")] public float CalmRoarDropDb { get; set; } = 6f;

    [Export(Range = "0,40,0.5")] public float CalmRustleDropDb { get; set; } = 12f;

    /// <summary>The wind strength used when the world has no <see cref="WorldEnvironment"/>.</summary>
    [Export(Range = "0,1,0.01")] public float FallbackWindStrength { get; set; } = 0.5f;

    /// <summary>Seconds the level takes to follow a change of wind strength (one-pole time constant).</summary>
    [Export(Range = "0,10,0.1")] public float Response { get; set; } = 2f;

    /// <summary>The eased wind strength in use (0–1).</summary>
    public float WindStrength => MathF.Max(_wind, 0f);

    public AudioPlayer? Roar => _roar;

    public AudioPlayer? Rustle => _rustle;

    protected override void OnReady()
    {
        base.OnReady();
        var bank = ForestSoundBank.Shared;
        _roar = new AudioPlayer { Name = "WindRoar", Stream = bank.WindRoar, Bus = ForestAudio.AmbienceBus, Loop = true };
        _rustle = new AudioPlayer { Name = "LeafRustle", Stream = bank.LeafRustle, Bus = ForestAudio.AmbienceBus, Loop = true };
        AddChild(_roar);
        AddChild(_rustle);
        Apply(TargetWind(), 0f);
        _roar.Play();
        _rustle.Play(fromSeconds: 6.5f); // start the layers apart, so their first gusts do not line up
    }

    protected override void OnProcess(in GameTime gameTime) => Apply(TargetWind(), gameTime.DeltaTime);

    private float TargetWind() =>
        Math.Clamp(GetWorld3D()?.Environment is { } environment ? environment.WindStrength : FallbackWindStrength, 0f, 1f);

    /// <summary>Eases towards <paramref name="target"/> and sets both layers (also the test hook).</summary>
    public void Apply(float target, float deltaSeconds)
    {
        _wind = _wind < 0f || Response <= 0f ? target : _wind + (target - _wind) * (1f - MathF.Exp(-deltaSeconds / Response));
        if (_roar is null || _rustle is null)
            return;
        var calm = 1f - _wind;
        _roar.VolumeDb = RoarVolumeDb - CalmRoarDropDb * calm;
        _roar.PitchScale = 0.9f + 0.18f * _wind; // stronger wind whistles higher
        _rustle.VolumeDb = RustleVolumeDb - CalmRustleDropDb * calm;
    }
}

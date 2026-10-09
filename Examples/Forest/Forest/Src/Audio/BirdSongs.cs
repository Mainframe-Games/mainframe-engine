using System.Numerics;
using MainframeEngine;

namespace Forest;

/// <summary>
/// Birds in the canopy: songs and calls of five ZzFX species (<see cref="BirdSynth"/>) played at Poisson-timed
/// intervals (mean <see cref="MeanInterval"/>) from random points <see cref="MinDistance"/>–<see cref="MaxDistance"/>
/// from the listener and <see cref="MinHeight"/>–<see cref="MaxHeight"/> above it, through a pool of
/// <see cref="PoolSize"/> <see cref="AudioPlayer3D"/>s on the <see cref="ForestAudio.AmbienceBus"/>. A species never
/// sings twice in a row. When <see cref="PineDensity"/> is set, dense pines quieten the songbirds (by up to
/// <see cref="PineQuietDb"/>) and make the woodpecker likelier. The schedule is seeded (<see cref="Seed"/>), and a
/// trigger only sets properties on a free pooled player: 0 B per trigger.
/// </summary>
public sealed class BirdSongs : Node3D
{
    private AudioPlayer3D[] _pool = [];
    private ForestRandom _rng;
    private float _untilNext;
    private int _lastSpecies = -1;
    private readonly int[] _lastVariant = new int[BirdSynth.AllSpecies.Length];

    [Export(Range = "1,16,1")] public int PoolSize { get; set; } = 6;

    /// <summary>Mean seconds between birds (a Poisson process; never closer than <see cref="MinInterval"/>).</summary>
    [Export(Range = "0.5,60,0.1")] public float MeanInterval { get; set; } = 4f;

    [Export(Range = "0,10,0.1")] public float MinInterval { get; set; } = 0.8f;

    [Export(Range = "1,100,0.5")] public float MinDistance { get; set; } = 10f;

    [Export(Range = "1,200,0.5")] public float MaxDistance { get; set; } = 40f;

    /// <summary>Height above the listener (m): the canopy.</summary>
    [Export(Range = "0,50,0.5")] public float MinHeight { get; set; } = 6f;

    [Export(Range = "0,50,0.5")] public float MaxHeight { get; set; } = 16f;

    /// <summary>The songs' level (dB, on top of the Ambience bus) before distance attenuation.</summary>
    [Export(Range = "-40,12,0.5")] public float VolumeDb { get; set; } = 0f;

    /// <summary>Random level spread per bird (± dB).</summary>
    [Export(Range = "0,12,0.5")] public float VolumeSpreadDb { get; set; } = 3f;

    /// <summary>How much quieter songbirds are in fully dense pines (dB).</summary>
    [Export(Range = "0,30,0.5")] public float PineQuietDb { get; set; } = 8f;

    [Export] public int Seed { get; set; } = 0xB12D5;

    /// <summary>Pine density (0–1) at a world position, e.g. from the terrain's scatter density map; null = none.</summary>
    public Func<Vector3, float>? PineDensity { get; set; }

    /// <summary>Birds triggered so far.</summary>
    public int Triggered { get; private set; }

    /// <summary>Triggers skipped because every pooled player was still singing.</summary>
    public int Skipped { get; private set; }

    public BirdSpecies? LastSpecies => _lastSpecies < 0 ? null : (BirdSpecies)_lastSpecies;

    /// <summary>Where the last bird sang from (world space).</summary>
    public Vector3 LastPosition { get; private set; }

    public IReadOnlyList<AudioPlayer3D> Pool => _pool;

    protected override void OnReady()
    {
        base.OnReady();
        _rng = new ForestRandom((ulong)Seed);
        ForestSoundBank.Shared.Birds(BirdSpecies.Warbler); // synthesise the songs now, not at the first trigger
        _pool = new AudioPlayer3D[Math.Max(1, PoolSize)];
        for (var i = 0; i < _pool.Length; i++)
        {
            _pool[i] = new AudioPlayer3D
            {
                Name = $"Bird{i}",
                Bus = ForestAudio.AmbienceBus,
                AttenuationModel = AttenuationModel.Inverse,
                UnitSize = 8f,
                MaxDistance = 90f,
                LowPassAtMaxDistance = 2500f, // the canopy swallows the highs of far birds
            };
            AddChild(_pool[i]);
        }

        _untilNext = NextInterval() * 0.5f; // the first bird comes sooner
    }

    protected override void OnProcess(in GameTime gameTime) => Advance(gameTime.DeltaTime, ForestAudio.ListenerPosition(this));

    /// <summary>Runs the schedule for <paramref name="deltaSeconds"/> around <paramref name="listener"/> (public for tests).</summary>
    public void Advance(float deltaSeconds, Vector3 listener)
    {
        _untilNext -= deltaSeconds;
        while (_untilNext <= 0f)
        {
            Trigger(listener);
            _untilNext += NextInterval();
        }
    }

    /// <summary>Sings one song or call now from a random canopy point around <paramref name="listener"/>.</summary>
    public void Trigger(Vector3 listener)
    {
        var player = FreePlayer();
        if (player is null)
        {
            Skipped++;
            return;
        }

        var angle = _rng.NextFloat() * MathF.Tau;
        var distance = _rng.Range(MinDistance, MathF.Max(MinDistance, MaxDistance));
        var position = listener + new Vector3(MathF.Cos(angle) * distance, _rng.Range(MinHeight, MathF.Max(MinHeight, MaxHeight)),
            MathF.Sin(angle) * distance);
        var pines = PineDensity is { } density ? Math.Clamp(density(position), 0f, 1f) : 0f;

        var species = PickSpecies(pines);
        var variants = ForestSoundBank.Shared.Birds(species);
        // Mostly songs, sometimes a call; never the same variant twice in a row for a species.
        var variant = _rng.NextFloat() < 0.7f ? _rng.Range(0, BirdSynth.SongVariants) : _rng.Range(BirdSynth.SongVariants, variants.Length);
        if (variant == _lastVariant[(int)species])
            variant = (variant + 1) % variants.Length;
        _lastVariant[(int)species] = variant;
        _lastSpecies = (int)species;

        player.Stream = variants[variant];
        player.GlobalPosition = position;
        LastPosition = position;
        player.VolumeDb = VolumeDb + _rng.Range(-VolumeSpreadDb, VolumeSpreadDb)
                          - (species == BirdSpecies.Woodpecker ? 0f : PineQuietDb * pines);
        player.Play();
        Triggered++;
    }

    private BirdSpecies PickSpecies(float pines)
    {
        // Songbirds weigh 1 each; the woodpecker 0.35, up to 2 in dense pines. The last species is left out.
        Span<float> weights = stackalloc float[BirdSynth.AllSpecies.Length];
        var total = 0f;
        for (var i = 0; i < weights.Length; i++)
        {
            weights[i] = i == _lastSpecies ? 0f : (BirdSpecies)i == BirdSpecies.Woodpecker ? 0.35f + 1.65f * pines : 1f;
            total += weights[i];
        }

        var pick = _rng.NextFloat() * total;
        for (var i = 0; i < weights.Length; i++)
        {
            pick -= weights[i];
            if (pick < 0f && weights[i] > 0f)
                return (BirdSpecies)i;
        }

        return _lastSpecies == 0 ? BirdSpecies.Chickadee : BirdSpecies.Warbler;
    }

    private AudioPlayer3D? FreePlayer()
    {
        foreach (var player in _pool)
        {
            if (!player.Playing)
                return player;
        }

        return null;
    }

    private float NextInterval() => MathF.Max(MinInterval, _rng.Exponential(MeanInterval));
}

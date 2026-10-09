using MainframeEngine;

namespace Forest;

/// <summary>
/// Every Forest sound as an in-memory <see cref="AudioStream"/> (<see cref="AudioStream.FromSamples"/>), synthesised
/// once by <see cref="ForestSynth"/> and <see cref="BirdSynth"/>. Each group is built on first use (≈ 0.1 s for the
/// loops); <see cref="Shared"/> is the process-wide bank the nodes use, so a scene reload costs nothing. About 17 MB of
/// samples in all, most of it the stereo wind and leaves.
/// </summary>
public sealed class ForestSoundBank
{
    /// <summary>The seed of <see cref="Shared"/>.</summary>
    public const ulong DefaultSeed = 0x466F72657374UL; // "Forest"

    /// <summary>Footstep variations per surface.</summary>
    public const int FootstepVariants = 6;

    /// <summary>Per-play pitch variation of a footstep (ZzFX's randomness; Godot's random pitch).</summary>
    public const float FootstepPitchRandomness = 0.08f;

    public const float BirdPitchRandomness = 0.04f;

    private static readonly Lazy<ForestSoundBank> s_shared = new(() => new ForestSoundBank(DefaultSeed));

    private readonly ulong _seed;
    private readonly Lazy<(AudioStream Roar, AudioStream Rustle)> _wind;
    private readonly Lazy<(AudioStream Brook, AudioStream Falls)> _water;
    private readonly Lazy<Dictionary<string, AudioStream[]>> _footsteps;
    private readonly Lazy<AudioStream[][]> _birds;

    public ForestSoundBank(ulong seed)
    {
        _seed = seed;
        _wind = new Lazy<(AudioStream, AudioStream)>(() => (
            Loop(ForestSynth.WindRoar(_seed + 1), 2, ForestSynth.WindRate, "Wind roar"),
            Loop(ForestSynth.LeafRustle(_seed + 2), 2, ForestSynth.Rate, "Leaf rustle")));
        _water = new Lazy<(AudioStream, AudioStream)>(() => (
            Loop(ForestSynth.Brook(_seed + 3), 1, ForestSynth.Rate, "Brook"),
            Loop(ForestSynth.Falls(_seed + 4), 1, ForestSynth.Rate, "Falls")));
        _footsteps = new Lazy<Dictionary<string, AudioStream[]>>(BuildFootsteps);
        _birds = new Lazy<AudioStream[][]>(BuildBirds);
    }

    /// <summary>The bank the Forest's audio nodes use.</summary>
    public static ForestSoundBank Shared => s_shared.Value;

    /// <summary>Wind in the trees: a stereo 24 s loop (the Ambience bed's low layer).</summary>
    public AudioStream WindRoar => _wind.Value.Roar;

    /// <summary>Leaves in the gusts: a stereo 17 s loop (the bed's high layer).</summary>
    public AudioStream LeafRustle => _wind.Value.Rustle;

    /// <summary>The babbling brook: a mono 14 s loop.</summary>
    public AudioStream Brook => _water.Value.Brook;

    /// <summary>Rushing falls: a mono 10 s loop.</summary>
    public AudioStream Falls => _water.Value.Falls;

    /// <summary>
    /// The <see cref="FootstepVariants"/> variations for <paramref name="surface"/>; unknown surfaces (and a terrain
    /// layer named <c>leaf_litter</c> or <c>path</c>) map to their nearest set, else <c>default</c>.
    /// </summary>
    public AudioStream[] Footsteps(string? surface)
    {
        var sets = _footsteps.Value;
        return sets.TryGetValue(surface ?? "default", out var set) ? set : sets[MapSurface(surface)];
    }

    /// <summary>The <see cref="BirdSynth.Variants"/> songs and calls of <paramref name="species"/>.</summary>
    public AudioStream[] Birds(BirdSpecies species) => _birds.Value[(int)species];

    /// <summary>Builds every group now (a loading screen, or before an allocation window).</summary>
    public void Preload()
    {
        _ = _wind.Value;
        _ = _water.Value;
        _ = _footsteps.Value;
        _ = _birds.Value;
    }

    /// <summary>The set an unknown surface name uses.</summary>
    public static string MapSurface(string? surface) => surface switch
    {
        "leaf_litter" or "litter" or "forest_floor" => "leaves",
        "path" or "soil" or "earth" => "dirt",
        "stone" or "cliff" or "boulder" => "rock",
        "pebbles" or "shingle" => "gravel",
        "planks" or "bridge" or "log" => "wood",
        "shallow" or "stream" => "water",
        _ => "default",
    };

    private Dictionary<string, AudioStream[]> BuildFootsteps()
    {
        var sets = new Dictionary<string, AudioStream[]>(StringComparer.Ordinal);
        for (var s = 0; s < ForestSynth.FootstepSurfaces.Length; s++)
        {
            var surface = ForestSynth.FootstepSurfaces[s];
            var set = new AudioStream[FootstepVariants];
            for (var v = 0; v < set.Length; v++)
            {
                var samples = ForestSynth.Footstep(surface, _seed * 31 + (ulong)(s * 97 + v));
                set[v] = AudioStream.FromSamples(samples, 1, ForestSynth.Rate, $"Footstep {surface} {v}");
                set[v].PitchRandomness = FootstepPitchRandomness;
            }

            sets[surface] = set;
        }

        return sets;
    }

    private AudioStream[][] BuildBirds()
    {
        var birds = new AudioStream[BirdSynth.AllSpecies.Length][];
        foreach (var species in BirdSynth.AllSpecies)
        {
            var set = new AudioStream[BirdSynth.Variants];
            for (var v = 0; v < set.Length; v++)
            {
                set[v] = AudioStream.FromSamples(BirdSynth.Build(species, v, _seed), 1, BirdSynth.Rate, $"{species} {v}");
                set[v].PitchRandomness = BirdPitchRandomness;
            }

            birds[(int)species] = set;
        }

        return birds;
    }

    private static AudioStream Loop(float[] samples, int channels, int rate, string name)
    {
        var stream = AudioStream.FromSamples(samples, channels, rate, name);
        stream.Loop = true;
        return stream;
    }
}

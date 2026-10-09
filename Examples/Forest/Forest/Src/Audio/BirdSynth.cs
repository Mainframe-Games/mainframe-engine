using MainframeEngine;

namespace Forest;

/// <summary>The Forest's bird voices: five made-up species, loosely after real ones.</summary>
public enum BirdSpecies
{
    /// <summary>A robin-like warble: 4–7 quick sliding whistles, 2.2–4.2 kHz.</summary>
    Warbler,

    /// <summary>A chickadee's "fee-bee": two or three clear falling whistles.</summary>
    Chickadee,

    /// <summary>A finch's trill: a fast run of downward chirps, then a flourish.</summary>
    Finch,

    /// <summary>A thrush's flute: three slow phrases with pitch jumps, ending in a soft trill.</summary>
    Thrush,

    /// <summary>A woodpecker drumming on a trunk (heard more on the pine slope).</summary>
    Woodpecker,
}

/// <summary>
/// Bird songs and calls built from ZzFX notes (<see cref="Zzfx.Generate"/>, 44.1 kHz mono): each species has a recipe
/// that varies its notes from a seed, and the notes are mixed into one buffer at their start times. Variants 0–3 are
/// songs, higher variants short calls (one or two notes).
/// </summary>
public static class BirdSynth
{
    public const int Rate = Zzfx.SampleRate;

    /// <summary>Variants per species: <see cref="SongVariants"/> songs, then calls.</summary>
    public const int Variants = 6;

    public const int SongVariants = 4;

    public static readonly BirdSpecies[] AllSpecies = Enum.GetValues<BirdSpecies>();

    /// <summary>Peak level of each species' buffer (the woodpecker's knocks are the loudest at the source).</summary>
    public static float Peak(BirdSpecies species) => species switch
    {
        BirdSpecies.Woodpecker => 0.55f,
        BirdSpecies.Thrush => 0.45f,
        _ => 0.5f,
    };

    /// <summary>One song (variant &lt; <see cref="SongVariants"/>) or call of <paramref name="species"/>; deterministic for the seed.</summary>
    public static float[] Build(BirdSpecies species, int variant, ulong seed)
    {
        var rng = new ForestRandom(seed ^ ((ulong)species << 32) ^ (ulong)variant * 0x9E37UL);
        var call = variant >= SongVariants;
        var notes = new List<(float Start, ZzfxParameters Note)>();
        switch (species)
        {
            case BirdSpecies.Warbler:
                Warbler(notes, ref rng, call);
                break;
            case BirdSpecies.Chickadee:
                Chickadee(notes, ref rng, call);
                break;
            case BirdSpecies.Finch:
                Finch(notes, ref rng, call);
                break;
            case BirdSpecies.Thrush:
                Thrush(notes, ref rng, call);
                break;
            default:
                Woodpecker(notes, ref rng, call);
                break;
        }

        return Mix(notes, Peak(species));
    }

    // ZzFX's slide is per-sample acceleration: a note sliding by ΔHz over T seconds needs slide = ΔHz / (500·T).
    private static float SlideFor(float deltaHz, float seconds) => deltaHz / (500f * MathF.Max(seconds, 0.001f));

    private static void Warbler(List<(float, ZzfxParameters)> notes, ref ForestRandom r, bool call)
    {
        var count = call ? r.Range(1, 3) : r.Range(4, 8);
        var t = 0f;
        var key = r.Range(0.9f, 1.1f);
        for (var i = 0; i < count; i++)
        {
            var sustain = r.Range(0.03f, 0.08f);
            var length = 0.008f + sustain + 0.03f;
            var hz = r.LogRange(2200f, 4200f) * key;
            var delta = r.Range(500f, 1400f) * (r.NextFloat() < 0.5f ? -1f : 1f);
            notes.Add((t, new ZzfxParameters
            {
                Volume = r.Range(0.7f, 1f),
                Frequency = hz,
                Attack = 0.008f,
                Sustain = sustain,
                Release = 0.03f,
                Shape = ZzfxShape.Sine,
                Slide = SlideFor(delta, length),
                PitchJump = r.NextFloat() < 0.3f ? r.Range(-600f, 600f) : 0f,
                PitchJumpTime = sustain * 0.5f,
            }));
            t += length + r.Range(0.035f, 0.1f);
        }
    }

    private static void Chickadee(List<(float, ZzfxParameters)> notes, ref ForestRandom r, bool call)
    {
        var key = r.Range(0.94f, 1.06f);
        var count = call ? 1 : r.NextFloat() < 0.35f ? 3 : 2;
        var t = 0f;
        for (var i = 0; i < count; i++)
        {
            var hz = (i == 0 ? 3950f : 3350f) * key;
            var sustain = i == 0 ? r.Range(0.16f, 0.22f) : r.Range(0.18f, 0.26f);
            notes.Add((t, new ZzfxParameters
            {
                Volume = i == 0 ? 1f : 0.85f,
                Frequency = hz,
                Attack = 0.025f,
                Sustain = sustain,
                Release = 0.08f,
                Shape = ZzfxShape.Sine,
                Slide = SlideFor(-r.Range(80f, 180f), sustain + 0.1f),
                SustainVolume = 0.9f,
            }));
            t += sustain + 0.105f + r.Range(0.03f, 0.07f);
        }
    }

    private static void Finch(List<(float, ZzfxParameters)> notes, ref ForestRandom r, bool call)
    {
        var key = r.Range(0.9f, 1.1f);
        if (call)
        {
            notes.Add((0f, new ZzfxParameters
            {
                Volume = 0.8f,
                Frequency = 4600f * key,
                Attack = 0.004f,
                Sustain = 0.02f,
                Release = 0.03f,
                Shape = ZzfxShape.Sine,
                Slide = SlideFor(-1400f, 0.055f),
            }));
            return;
        }

        // The trill: ZzFX's repeat resets the downward slide every chirp, its tremolo pulses the level at the same rate.
        var chirp = r.Range(0.03f, 0.045f);
        var trill = r.Range(0.5f, 0.9f);
        notes.Add((0f, new ZzfxParameters
        {
            Volume = 0.9f,
            Frequency = r.Range(4000f, 4800f) * key,
            Attack = 0.01f,
            Sustain = trill,
            Release = 0.05f,
            Shape = ZzfxShape.Sine,
            Slide = SlideFor(-r.Range(700f, 1100f), chirp),
            RepeatTime = chirp,
            Tremolo = 0.6f,
        }));
        // The flourish: one or two longer sliding notes.
        var t = trill + 0.07f;
        var flourish = r.Range(1, 3);
        for (var i = 0; i < flourish; i++)
        {
            notes.Add((t, new ZzfxParameters
            {
                Volume = 0.8f,
                Frequency = r.LogRange(2800f, 3800f) * key,
                Attack = 0.01f,
                Sustain = 0.06f,
                Release = 0.05f,
                Shape = ZzfxShape.Sine,
                Slide = SlideFor(r.Range(-900f, 600f), 0.12f),
            }));
            t += 0.16f;
        }
    }

    private static void Thrush(List<(float, ZzfxParameters)> notes, ref ForestRandom r, bool call)
    {
        var key = r.Range(0.92f, 1.08f);
        var phrases = call ? 1 : 3;
        var t = 0f;
        for (var i = 0; i < phrases; i++)
        {
            var sustain = r.Range(0.12f, 0.22f);
            notes.Add((t, new ZzfxParameters
            {
                Volume = r.Range(0.75f, 1f),
                Frequency = r.LogRange(1800f, 2800f) * key,
                Attack = 0.03f,
                Sustain = sustain,
                Release = 0.09f,
                Shape = ZzfxShape.Sine,
                PitchJump = r.Range(-500f, 700f),
                PitchJumpTime = sustain * 0.55f,
                Slide = SlideFor(r.Range(-150f, 150f), sustain),
                SustainVolume = 0.8f,
            }));
            t += sustain + 0.12f + r.Range(0.08f, 0.16f);
        }

        if (!call)
        {
            // A soft high trill to finish.
            var chirp = r.Range(0.022f, 0.03f);
            notes.Add((t, new ZzfxParameters
            {
                Volume = 0.55f,
                Frequency = r.Range(3600f, 4400f) * key,
                Attack = 0.01f,
                Sustain = r.Range(0.18f, 0.3f),
                Release = 0.05f,
                Shape = ZzfxShape.Sine,
                Slide = SlideFor(-r.Range(400f, 700f), chirp),
                RepeatTime = chirp,
                Tremolo = 0.7f,
            }));
        }
    }

    private static void Woodpecker(List<(float, ZzfxParameters)> notes, ref ForestRandom r, bool call)
    {
        if (call)
        {
            // A sharp "pik".
            notes.Add((0f, new ZzfxParameters
            {
                Volume = 0.7f,
                Frequency = r.Range(2400f, 2900f),
                Attack = 0.003f,
                Sustain = 0.015f,
                Release = 0.04f,
                Shape = ZzfxShape.Triangle,
                Slide = SlideFor(-500f, 0.058f),
            }));
            return;
        }

        // A drum roll: 14–22 knocks at ≈ 18 Hz, slowing and fading towards the end.
        var hits = r.Range(14, 23);
        var interval = 1f / r.Range(16f, 21f);
        var hz = r.Range(650f, 950f);
        var t = 0f;
        for (var i = 0; i < hits; i++)
        {
            var u = i / (float)(hits - 1);
            notes.Add((t, new ZzfxParameters
            {
                Volume = (1f - 0.55f * u) * r.Range(0.85f, 1f),
                Frequency = hz * r.Range(0.97f, 1.03f),
                Attack = 0f,
                Sustain = 0f,
                Release = 0.016f,
                Shape = ZzfxShape.Tan,
                Noise = 0.4f,
                ShapeCurve = 1f,
            }));
            t += interval * (1f + 0.35f * u * u);
        }
    }

    /// <summary>Mixes the notes at their start times, fades the ends and normalizes to <paramref name="peak"/>.</summary>
    private static float[] Mix(List<(float Start, ZzfxParameters Note)> notes, float peak)
    {
        var rendered = new float[notes.Count][];
        var length = 0;
        for (var i = 0; i < notes.Count; i++)
        {
            rendered[i] = Zzfx.Generate(notes[i].Note);
            length = Math.Max(length, (int)(notes[i].Start * Rate) + rendered[i].Length);
        }

        var mix = new float[length + Rate / 50];
        for (var i = 0; i < notes.Count; i++)
        {
            var start = (int)(notes[i].Start * Rate);
            var samples = rendered[i];
            for (var s = 0; s < samples.Length; s++)
                mix[start + s] += samples[s];
        }

        SignalTools.FadeEnds(mix, Rate / 1000);
        SignalTools.NormalizePeak(mix, peak);
        return mix;
    }
}

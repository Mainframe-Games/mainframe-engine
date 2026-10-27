using MainframeEngine;

namespace Neon;

/// <summary>Every sound Neon synthesises (<see cref="NeonSoundBank"/>), for <see cref="NeonAudio.PlayAt"/> and <see cref="NeonAudio.CreateLoop"/>.</summary>
public enum NeonSound
{
    // The rain and the city (loops unless noted).

    /// <summary>Rain in the open (stereo; the balcony's mix).</summary>
    RainOutdoor,

    /// <summary>Rain heard from inside: muffled, patter and drips on the glass (stereo).</summary>
    RainIndoor,

    /// <summary>The city's drone: the low hum and the traffic wash (stereo).</summary>
    CityDrone,

    /// <summary>The city through the walls (stereo).</summary>
    CityDroneIndoor,

    /// <summary>A distant siren (one-shot, 3 variants).</summary>
    Siren,

    /// <summary>A distant car horn (one-shot, 4 variants).</summary>
    Horn,

    /// <summary>A public-address chime from somewhere in the city (one-shot, 2 variants, no words).</summary>
    PaChime,

    /// <summary>A flying car going by (one-shot, 6 s; play it on a moving emitter with Doppler).</summary>
    FlyingCar,

    /// <summary>The elevated train's rumble and clickety-clack (mono loop for a moving emitter).</summary>
    TrainRumble,

    /// <summary>An airship's engines (mono loop for a moving emitter).</summary>
    AirshipDrone,

    // The apartment.

    /// <summary>The shower running (loop).</summary>
    Shower,

    /// <summary>A tap running into a sink (loop).</summary>
    Tap,

    /// <summary>The fridge's compressor hum (loop).</summary>
    FridgeHum,

    /// <summary>The computer room's fans and the rack's whine (loop).</summary>
    PcFans,

    /// <summary>One key of a mechanical keyboard (one-shot, 8 variants).</summary>
    KeyClick,

    /// <summary>A hinged door opening: latch, bolt, creak (one-shot).</summary>
    DoorOpen,

    /// <summary>A hinged door shutting: thud and latch (one-shot; play it when the door reaches its frame).</summary>
    DoorClose,

    /// <summary>The balcony's glass door sliding open (one-shot, its stop at 0.85 s).</summary>
    SliderOpen,

    /// <summary>The balcony's glass door sliding shut (one-shot, its seal and latch at 0.85 s).</summary>
    SliderClose,

    /// <summary>A light switch (one-shot).</summary>
    SwitchClick,

    /// <summary>The front door's lock panel: buzz and the "denied" two-tone (one-shot).</summary>
    LockedBuzz,

    /// <summary>A TV tuned to nothing (loop).</summary>
    TvStatic,

    /// <summary>The TV changing channel (one-shot).</summary>
    TvBlip,

    /// <summary>The TV ad's jingle (loop, 7.5 s).</summary>
    TvJingle,
}

/// <summary>
/// Every Neon sound as an in-memory <see cref="AudioStream"/> (<see cref="AudioStream.FromSamples"/>), synthesised
/// once by <see cref="NeonSynth"/> (and <see cref="NeonMusic.Jingle"/>) on first use and kept: <see cref="Shared"/> is the
/// process-wide bank the nodes use, so a scene reload costs nothing. Loops have <see cref="AudioStream.Loop"/> set;
/// one-shots with variations come as arrays (<see cref="Variants"/>), footsteps per surface (<see cref="Footsteps"/>).
/// About 45 MB of samples when everything is built; <see cref="Preload"/> builds it all (a loading screen).
/// </summary>
public sealed class NeonSoundBank
{
    /// <summary>The seed of <see cref="Shared"/>.</summary>
    public const ulong DefaultSeed = 0x4E656F6E4E36UL; // "NeonN6"

    /// <summary>Footstep variations per surface.</summary>
    public const int FootstepVariants = 6;

    /// <summary>Per-play pitch variation of footsteps, keys and the like.</summary>
    public const float FootstepPitchRandomness = 0.08f;

    public const float FoleyPitchRandomness = 0.04f;

    private static readonly NeonSound[] s_all = Enum.GetValues<NeonSound>();
    private static readonly Lazy<NeonSoundBank> s_shared = new(() => new NeonSoundBank(DefaultSeed));

    private readonly ulong _seed;
    private readonly Lazy<AudioStream[]>[] _sounds;
    private readonly Lazy<Dictionary<string, AudioStream[]>> _footsteps;

    public NeonSoundBank(ulong seed)
    {
        _seed = seed;
        _sounds = new Lazy<AudioStream[]>[s_all.Length];
        foreach (var sound in s_all)
            _sounds[(int)sound] = new Lazy<AudioStream[]>(() => Build(sound));
        _footsteps = new Lazy<Dictionary<string, AudioStream[]>>(BuildFootsteps);
    }

    /// <summary>The bank Neon's audio nodes use.</summary>
    public static NeonSoundBank Shared => s_shared.Value;

    /// <summary>Every sound, in enum order.</summary>
    public static IReadOnlyList<NeonSound> All => s_all;

    /// <summary>The first (or only) stream of <paramref name="sound"/>.</summary>
    public AudioStream this[NeonSound sound] => _sounds[(int)sound].Value[0];

    /// <summary>All variations of <paramref name="sound"/> (one for most).</summary>
    public AudioStream[] Variants(NeonSound sound) => _sounds[(int)sound].Value;

    /// <summary>Whether <paramref name="sound"/> is a loop (its streams have <see cref="AudioStream.Loop"/> set).</summary>
    public static bool IsLoop(NeonSound sound) => sound is NeonSound.RainOutdoor or NeonSound.RainIndoor or NeonSound.CityDrone or NeonSound.CityDroneIndoor
        or NeonSound.TrainRumble or NeonSound.AirshipDrone or NeonSound.Shower or NeonSound.Tap or NeonSound.FridgeHum or NeonSound.PcFans
        or NeonSound.TvStatic or NeonSound.TvJingle;

    /// <summary>Whether <paramref name="sound"/> comes from outside (muffled by the walls while the listener is in).</summary>
    public static bool IsOutdoor(NeonSound sound) => sound is NeonSound.Siren or NeonSound.Horn or NeonSound.PaChime or NeonSound.FlyingCar
        or NeonSound.TrainRumble or NeonSound.AirshipDrone;

    /// <summary>The bus a sound plays on unless the caller names one.</summary>
    public static string DefaultBus(NeonSound sound) => sound switch
    {
        NeonSound.RainOutdoor or NeonSound.RainIndoor => NeonAudio.RainBus,
        NeonSound.CityDrone or NeonSound.CityDroneIndoor or NeonSound.Siren or NeonSound.Horn or NeonSound.PaChime or NeonSound.FlyingCar
            or NeonSound.TrainRumble or NeonSound.AirshipDrone => NeonAudio.AmbienceBus,
        NeonSound.Shower => NeonAudio.BathroomBus,
        NeonSound.TvStatic or NeonSound.TvBlip or NeonSound.TvJingle => NeonAudio.TvBus,
        _ => NeonAudio.FoleyBus,
    };

    /// <summary>
    /// How a positional emitter of <paramref name="sound"/> falls off: full level within <c>UnitSize</c> metres, silent
    /// beyond <c>MaxDistance</c> (0: never). Small machines are only heard in their room; the train and airships across
    /// the city.
    /// </summary>
    public static (float UnitSize, float MaxDistance) Emitter(NeonSound sound) => sound switch
    {
        NeonSound.Shower => (1.5f, 14f),
        NeonSound.Tap => (0.8f, 9f),
        NeonSound.FridgeHum => (0.8f, 7f),
        NeonSound.PcFans => (1.2f, 10f),
        NeonSound.KeyClick or NeonSound.SwitchClick => (0.8f, 10f),
        NeonSound.TvStatic or NeonSound.TvBlip or NeonSound.TvJingle => (2f, 22f),
        NeonSound.Siren or NeonSound.Horn or NeonSound.PaChime => (30f, 0f),
        NeonSound.FlyingCar => (12f, 0f),
        NeonSound.TrainRumble => (35f, 0f),
        NeonSound.AirshipDrone => (60f, 0f),
        _ => (2f, 25f),
    };

    /// <summary>
    /// The <see cref="FootstepVariants"/> variations for <paramref name="surface"/>; unknown names map to their nearest
    /// set (<see cref="MapSurface"/>), else concrete.
    /// </summary>
    public AudioStream[] Footsteps(string? surface)
    {
        var sets = _footsteps.Value;
        return sets.TryGetValue(surface ?? "default", out var set) ? set : sets[MapSurface(surface)];
    }

    /// <summary>The set an unknown surface name uses.</summary>
    public static string MapSurface(string? surface) => surface switch
    {
        "grate" or "steel" or "stairs" or "mezzanine" => "metal",
        "carpet" or "mat" => "rug",
        "ceramic" or "porcelain" or "bathroom" => "tile",
        "planks" or "floorboards" or "parquet" => "wood",
        _ => "default",
    };

    /// <summary>Builds every sound now (a loading screen, or before an allocation window). The music is separate (<see cref="NeonMusic"/>).</summary>
    public void Preload()
    {
        foreach (var lazy in _sounds)
            _ = lazy.Value;
        _ = _footsteps.Value;
    }

    private AudioStream[] Build(NeonSound sound)
    {
        var s = _seed + (ulong)sound * 1009;
        return sound switch
        {
            NeonSound.RainOutdoor => [Loop(NeonSynth.RainOutdoor(s), 2, NeonSynth.Rate, sound)],
            NeonSound.RainIndoor => [Loop(NeonSynth.RainIndoor(s), 2, NeonSynth.Rate, sound)],
            NeonSound.CityDrone => [Loop(NeonSynth.CityDrone(s), 2, NeonSynth.LowRate, sound)],
            NeonSound.CityDroneIndoor => [Loop(NeonSynth.CityDrone(s, indoor: true), 2, NeonSynth.LowRate, sound)],
            NeonSound.TrainRumble => [Loop(NeonSynth.TrainRumble(s), 1, NeonSynth.LowRate, sound)],
            NeonSound.AirshipDrone => [Loop(NeonSynth.AirshipDrone(s), 1, NeonSynth.LowRate, sound)],
            NeonSound.Shower => [Loop(NeonSynth.Shower(s), 1, NeonSynth.Rate, sound)],
            NeonSound.Tap => [Loop(NeonSynth.Tap(s), 1, NeonSynth.Rate, sound)],
            NeonSound.FridgeHum => [Loop(NeonSynth.FridgeHum(s), 1, NeonSynth.LowRate, sound)],
            NeonSound.PcFans => [Loop(NeonSynth.PcFans(s), 1, NeonSynth.Rate, sound)],
            NeonSound.TvStatic => [Loop(NeonSynth.TvStatic(s), 1, NeonSynth.Rate, sound)],
            NeonSound.TvJingle => [Loop(NeonMusic.Jingle(s), 1, NeonMusic.Rate, sound)],
            NeonSound.Siren => Set(sound, NeonSynth.SirenVariants, v => NeonSynth.Siren(v, s + (ulong)v), 0.03f),
            NeonSound.Horn => Set(sound, NeonSynth.HornVariants, v => NeonSynth.Horn(v, s + (ulong)v), 0.05f),
            NeonSound.PaChime => Set(sound, NeonSynth.PaChimeVariants, v => NeonSynth.PaChime(v, s + (ulong)v), 0f),
            NeonSound.KeyClick => Set(sound, NeonSynth.KeyClickVariants, v => NeonSynth.KeyClick(v, s), 0.05f),
            NeonSound.FlyingCar => [OneShot(NeonSynth.FlyingCar(s), sound, 0.06f)],
            NeonSound.DoorOpen => [OneShot(NeonSynth.DoorOpen(s), sound, FoleyPitchRandomness)],
            NeonSound.DoorClose => [OneShot(NeonSynth.DoorClose(s), sound, FoleyPitchRandomness)],
            NeonSound.SliderOpen => [OneShot(NeonSynth.Slider(closing: false, s), sound, FoleyPitchRandomness)],
            NeonSound.SliderClose => [OneShot(NeonSynth.Slider(closing: true, s), sound, FoleyPitchRandomness)],
            NeonSound.SwitchClick => [OneShot(NeonSynth.SwitchClick(s), sound, FoleyPitchRandomness)],
            NeonSound.LockedBuzz => [OneShot(NeonSynth.LockedBuzz(s), sound, 0f)],
            NeonSound.TvBlip => [OneShot(NeonSynth.TvBlip(s), sound, FoleyPitchRandomness)],
            _ => throw new ArgumentOutOfRangeException(nameof(sound), sound, null),
        };
    }

    private static AudioStream[] Set(NeonSound sound, int count, Func<int, float[]> build, float pitchRandomness)
    {
        var set = new AudioStream[count];
        for (var v = 0; v < count; v++)
        {
            set[v] = AudioStream.FromSamples(build(v), 1, NeonSynth.Rate, $"{sound} {v}");
            set[v].PitchRandomness = pitchRandomness;
        }

        return set;
    }

    private static AudioStream OneShot(float[] samples, NeonSound sound, float pitchRandomness)
    {
        var stream = AudioStream.FromSamples(samples, 1, NeonSynth.Rate, sound.ToString());
        stream.PitchRandomness = pitchRandomness;
        return stream;
    }

    private static AudioStream Loop(float[] samples, int channels, int rate, NeonSound sound)
    {
        var stream = AudioStream.FromSamples(samples, channels, rate, sound.ToString());
        stream.Loop = true;
        return stream;
    }

    private Dictionary<string, AudioStream[]> BuildFootsteps()
    {
        var sets = new Dictionary<string, AudioStream[]>(StringComparer.Ordinal);
        for (var s = 0; s < NeonSynth.FootstepSurfaces.Length; s++)
        {
            var surface = NeonSynth.FootstepSurfaces[s];
            var set = new AudioStream[FootstepVariants];
            for (var v = 0; v < set.Length; v++)
            {
                set[v] = AudioStream.FromSamples(NeonSynth.Footstep(surface, _seed * 31 + (ulong)(s * 97 + v)), 1, NeonSynth.Rate, $"Footstep {surface} {v}");
                set[v].PitchRandomness = FootstepPitchRandomness;
            }

            sets[surface] = set;
        }

        return sets;
    }
}

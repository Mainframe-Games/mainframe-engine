using MainframeEngine;
using MainframeEngine.Audio;
using MainframeEngine.Audio.Music;

namespace Neon;

/// <summary>
/// The stereo's music: two short, original synthwave loops synthesised here (drums, bass, pads and a lead, each a
/// stem) and played through the engine's adaptive music (<see cref="AdaptiveMusicPlayer"/>, ADR 0196). Each song is a
/// manifest built in code (<see cref="CreateMusic"/>): one looping 8-bar section, four layers with a low-pass filter
/// each, and a <see cref="MuffleParameter"/> (0–1) whose curves close the filters and soften the lead and drums, so
/// the music sounds through walls when the player leaves the living room. The stems are rendered once into
/// <see cref="CacheFolder"/> as WAVs (the runtime streams stems from files; nothing is committed) and reused while
/// their name (song, seed and <see cref="StemVersion"/>) matches. Also the TV's ad jingle (<see cref="Jingle"/>).
/// </summary>
public static class NeonMusic
{
    public const int Rate = 48000;

    /// <summary>Bars in each song's loop.</summary>
    public const int Bars = 8;

    /// <summary>The music's parameter: 0 in the room with the stereo, 1 far away behind walls.</summary>
    public const string MuffleParameter = "muffle";

    /// <summary>Bumped whenever the recipes change, so old cached stems are not reused.</summary>
    public const int StemVersion = 1;

    /// <summary>The section every song loops.</summary>
    public const string Section = "main";

    /// <summary>The layers (stems), in mix order.</summary>
    public static readonly string[] Layers = ["drums", "bass", "pads", "lead"];

    /// <summary>The mix's peak after normalization (all four layers summed).</summary>
    public const float MixPeak = 0.6f;

    /// <summary>The TV jingle's loop, in seconds (16 beats at 128 BPM) and its RMS.</summary>
    public const float JingleSeconds = 7.5f;

    public const float JingleRms = 0.09f;

    private const float TailSeconds = 3f;

    /// <summary>The songs: tempo (BPM, a whole number of frames a beat at 48 kHz), the bars' chords and the lead's notes.</summary>
    public static IReadOnlyList<NeonSong> Songs { get; } =
    [
        new NeonSong("night-drive", "Night Drive", 100, NeonSongStyle.Drive,
            [new(57, true), new(53, false), new(60, false), new(55, false), new(57, true), new(53, false), new(60, false), new(55, false)],
            [
                new(0, 1.5f, 76), new(1.5f, 0.5f, 74), new(2, 1, 72), new(3, 1, 69),
                new(4, 1, 72), new(5, 0.5f, 69), new(5.5f, 0.5f, 72), new(6, 2, 74),
                new(8, 1, 76), new(9, 1, 79), new(10, 0.5f, 76), new(10.5f, 0.5f, 74), new(11, 1, 72),
                new(12, 3, 74), new(15, 1, 71),
                new(16, 1.5f, 76), new(17.5f, 0.5f, 74), new(18, 1, 72), new(19, 1, 76),
                new(20, 2, 81), new(22, 1, 79), new(23, 1, 77),
                new(24, 1, 76), new(25, 0.5f, 74), new(25.5f, 0.5f, 72), new(26, 1, 74), new(27, 1, 76),
                new(28, 2, 74), new(30, 1, 71),
            ]),
        new NeonSong("rain-signal", "Rain Signal", 90, NeonSongStyle.HalfTime,
            [new(50, true), new(58, false), new(55, true), new(57, false), new(50, true), new(58, false), new(55, true), new(57, false)],
            [
                new(16, 1.5f, 81), new(17.5f, 0.5f, 77), new(18, 2, 79),
                new(20, 1.5f, 77), new(21.5f, 0.5f, 74), new(22, 2, 77),
                new(24, 1, 79), new(25, 1, 82), new(26, 2, 81),
                new(28, 1, 76), new(29, 1, 85), new(30, 2, 81),
            ]),
    ];

    /// <summary>Where <see cref="CreateMusic"/> renders stems by default: a <c>neon-music</c> folder in the temp directory.</summary>
    public static string CacheFolder { get; set; } = Path.Combine(Path.GetTempPath(), "neon-music");

    /// <summary>Frames per beat of <paramref name="song"/>.</summary>
    public static int BeatFrames(NeonSong song) => Rate * 60 / song.Tempo;

    /// <summary>Frames in <paramref name="song"/>'s loop.</summary>
    public static int LoopFrames(NeonSong song) => BeatFrames(song) * 4 * Bars;

    /// <summary>
    /// The adaptive music of song <paramref name="index"/>: renders its stems into <paramref name="folder"/> (default
    /// <see cref="CacheFolder"/>) unless they are there already, and builds the manifest. <paramref name="inMemory"/>
    /// loads the section into memory instead of streaming it (tests: exact, no streaming thread).
    /// </summary>
    public static AdaptiveMusic CreateMusic(int index, ulong seed = NeonSoundBank.DefaultSeed, string? folder = null, bool inMemory = false)
    {
        var song = Songs[index];
        folder ??= CacheFolder;
        Directory.CreateDirectory(folder);
        var paths = new string[Layers.Length];
        var missing = false;
        for (var l = 0; l < Layers.Length; l++)
        {
            paths[l] = Path.Combine(folder, $"{song.Id}-{seed:x}-v{StemVersion}-{Layers[l]}.wav");
            missing |= !File.Exists(paths[l]);
        }

        if (missing)
        {
            var stems = RenderStems(index, seed);
            for (var l = 0; l < Layers.Length; l++)
            {
                var temp = paths[l] + ".tmp";
                WavWriter.Write(temp, stems[l], 2, Rate, WavSampleFormat.Pcm16);
                File.Move(temp, paths[l], overwrite: true); // whole files only: a crash mid-write never leaves a short stem
            }
        }

        var music = AdaptiveMusic.FromManifest(Manifest(index, paths), song.Name);
        if (inMemory)
            music.PreloadSections.Add(Section);
        return music;
    }

    /// <summary>The manifest of song <paramref name="index"/> with its stems at <paramref name="stemPaths"/> (in <see cref="Layers"/> order).</summary>
    public static MusicManifest Manifest(int index, IReadOnlyList<string> stemPaths)
    {
        var song = Songs[index];
        var manifest = new MusicManifest
        {
            Song = song.Name,
            SampleRate = Rate,
            Tempo = song.Tempo,
            TimeSignature = [4, 4],
            Start = Section,
            Parameters = [new MusicParameterInfo { Name = MuffleParameter, Kind = MusicParameterKind.Continuous, Min = 0, Max = 1, SeekSpeed = 1.5f }],
        };
        // Muffled: every layer's filter closes (the kick's thump and the bass carry through walls, the hats and lead do not).
        ReadOnlySpan<float> closed = [900f, 500f, 600f, 700f];
        ReadOnlySpan<float> quieter = [-3f, 0f, -2f, -6f];
        for (var l = 0; l < Layers.Length; l++)
        {
            manifest.Layers.Add(new MusicManifestLayer { Id = Layers[l], Name = Layers[l], Filter = MusicLayerFilter.LowPass });
            manifest.Curves.Add(new MusicCurveInfo
            {
                Layer = Layers[l],
                Target = MusicCurveTarget.Cutoff,
                Parameter = MuffleParameter,
                Points = [new(0, 18000), new(0.5f, closed[l] * 3f), new(1, closed[l])],
            });
            manifest.Curves.Add(new MusicCurveInfo { Layer = Layers[l], Target = MusicCurveTarget.Volume, Parameter = MuffleParameter, Points = [new(0, 0), new(1, quieter[l])] });
        }

        var section = new MusicManifestSection { Id = Section, Name = "Loop", Kind = MusicSectionKind.Section, Bars = Bars, LengthFrames = LoopFrames(song), Loop = true };
        for (var l = 0; l < Layers.Length; l++)
            section.Stems[Layers[l]] = new MusicStemSet { Body = new MusicStemRef { Path = stemPaths[l], Frames = LoopFrames(song) } };
        manifest.Sections.Add(section);
        return manifest;
    }

    /// <summary>
    /// Song <paramref name="index"/>'s four stems (interleaved stereo, <see cref="LoopFrames"/> each, seamless: each
    /// pass's tails are folded onto the start), scaled together so their sum peaks at <see cref="MixPeak"/>.
    /// </summary>
    public static float[][] RenderStems(int index, ulong seed)
    {
        var song = Songs[index];
        var rng = new NeonRandom(seed ^ (ulong)(index + 1) * 0x9E3779B97F4A7C15UL);
        var loop = LoopFrames(song);
        var frames = loop + (int)(TailSeconds * Rate);
        var stems = new float[Layers.Length][];
        stems[0] = Drums(song, frames, ref rng);
        stems[1] = Bass(song, frames);
        stems[2] = Pads(song, frames, ref rng);
        stems[3] = song.Style == NeonSongStyle.Drive ? Lead(song, frames) : Arp(song, frames);
        ReadOnlySpan<float> gains = [0.9f, 0.7f, 0.35f, 0.42f];
        for (var l = 0; l < stems.Length; l++)
        {
            SignalTools.Scale(stems[l], gains[l]);
            stems[l] = SignalTools.WrapTail(stems[l], 2, loop);
        }

        var mix = new float[stems[0].Length];
        foreach (var stem in stems)
            for (var i = 0; i < mix.Length; i++)
                mix[i] += stem[i];
        var peak = SignalTools.Peak(mix);
        foreach (var stem in stems)
            SignalTools.Scale(stem, MixPeak / peak);
        return stems;
    }

    /// <summary>The four stems summed (tests, the offline render).</summary>
    public static float[] Mix(float[][] stems)
    {
        var mix = new float[stems[0].Length];
        foreach (var stem in stems)
            for (var i = 0; i < mix.Length; i++)
                mix[i] += stem[i];
        return mix;
    }

    /// <summary>MIDI note → Hz.</summary>
    public static double Hz(double midi) => 440 * Math.Pow(2, (midi - 69) / 12);

    // ── Layers ────────────────────────────────────────────────────────────────────────────────────

    private static float[] Drums(NeonSong song, int frames, ref NeonRandom rng)
    {
        var b = new float[frames * 2];
        var beat = BeatFrames(song);
        for (var bar = 0; bar < Bars; bar++)
        {
            var at = bar * 4 * beat;
            if (song.Style == NeonSongStyle.Drive)
            {
                for (var k = 0; k < 4; k++)
                    Kick(b, at + k * beat, 1f);
                Snare(b, at + beat, 0.9f, ref rng);
                Snare(b, at + 3 * beat, 0.9f, ref rng);
                for (var h = 0; h < 8; h++)
                    Hat(b, at + h * beat / 2, open: bar % 2 == 1 && h == 7, h % 2 == 1 ? 0.5f : 0.3f, ref rng);
            }
            else
            {
                Kick(b, at, 1f);
                Kick(b, at + beat * 5 / 2, 0.8f);
                Snare(b, at + 2 * beat, 1f, ref rng);
                for (var h = 0; h < 16; h++)
                    Hat(b, at + h * beat / 4, open: bar % 4 == 3 && h == 14, h % 4 == 2 ? 0.4f : h % 2 == 0 ? 0.25f : 0.14f, ref rng);
            }
        }

        return b;
    }

    private static void Kick(float[] b, int at, float gain)
    {
        var frames = (int)(0.45f * Rate);
        var phase = 0.0;
        for (var i = 0; i < frames && (at + i) * 2 + 1 < b.Length; i++)
        {
            var t = i / (double)Rate;
            phase += 2 * Math.PI * (45 + 75 * Math.Exp(-t / 0.035)) / Rate;
            var env = (1 - Math.Exp(-t / 0.001)) * Math.Exp(-t / 0.28);
            var click = Math.Exp(-t / 0.002) * 0.25 * Math.Sin(2 * Math.PI * 1800 * t);
            var x = (float)((Math.Sin(phase) * env + click) * gain);
            b[(at + i) * 2] += x;
            b[(at + i) * 2 + 1] += x;
        }
    }

    private static void Snare(float[] b, int at, float gain, ref NeonRandom rng)
    {
        var frames = (int)(0.5f * Rate);
        var bandL = Biquad.BandPass(2000, 0.6, Rate);
        var bandR = Biquad.BandPass(2000, 0.6, Rate);
        var roomL = Biquad.LowPass(6000, 0.7, Rate);
        var roomR = Biquad.LowPass(6000, 0.7, Rate);
        for (var i = 0; i < frames && (at + i) * 2 + 1 < b.Length; i++)
        {
            var t = i / (float)Rate;
            var tone = MathF.Sin(2 * MathF.PI * 185 * t) * MathF.Exp(-t / 0.06f) * 0.5f;
            var body = MathF.Exp(-t / 0.12f) * 0.7f;
            var gate = t < 0.28f ? 1f : MathF.Exp(-(t - 0.28f) / 0.02f); // the 80s gated room
            var room = MathF.Exp(-t / 0.25f) * 0.3f * gate;
            var nl = rng.NextSigned();
            var nr = rng.NextSigned();
            b[(at + i) * 2] += (tone + bandL.Process(nl) * body + roomL.Process(nl) * room) * gain;
            b[(at + i) * 2 + 1] += (tone + bandR.Process(nr) * body + roomR.Process(nr) * room) * gain;
        }
    }

    private static void Hat(float[] b, int at, bool open, float gain, ref NeonRandom rng)
    {
        var decay = open ? 0.12f : 0.025f;
        var frames = (int)(decay * 6 * Rate);
        var hp = Biquad.HighPass(7000, 0.7, Rate);
        for (var i = 0; i < frames && (at + i) * 2 + 1 < b.Length; i++)
        {
            var t = i / (float)Rate;
            var x = hp.Process(rng.NextSigned()) * MathF.Exp(-t / decay) * gain;
            b[(at + i) * 2] += x * 0.8f;
            b[(at + i) * 2 + 1] += x;
        }
    }

    /// <summary>
    /// The bass, centred: a saw with a sub sine through a low-pass whose cutoff snaps open on each note. The drive
    /// pulses eighths between the root and its octave; the half-time song plays a syncopated figure.
    /// </summary>
    private static float[] Bass(NeonSong song, int frames)
    {
        var b = new float[frames * 2];
        var beat = BeatFrames(song);
        ReadOnlySpan<(float Beat, float Length, int Interval)> halfTime = [(0, 0.75f, 0), (0.75f, 0.25f, 0), (1.5f, 0.5f, 12), (2, 1, 0), (3.5f, 0.5f, 7)];
        for (var bar = 0; bar < Bars; bar++)
        {
            var root = Fold(song.Chords[bar].Root, 33);
            var at = bar * 4 * beat;
            if (song.Style == NeonSongStyle.Drive)
            {
                for (var e = 0; e < 8; e++)
                    BassNote(b, at + e * beat / 2, beat / 2 - beat / 16, Hz(root + (e % 2 == 1 ? 12 : 0)));
            }
            else
            {
                foreach (var (start, length, interval) in halfTime)
                    BassNote(b, at + (int)(start * beat), (int)(length * beat) - beat / 16, Hz(root + interval));
            }
        }

        return b;
    }

    private static void BassNote(float[] b, int at, int frames, double hz)
    {
        var saw = new BlepOscillator(0);
        var sub = new BlepOscillator(0);
        var filter = new Biquad();
        var release = Rate / 30;
        for (var i = 0; i < frames + release && (at + i) * 2 + 1 < b.Length; i++)
        {
            var t = i / (double)Rate;
            if ((i & 15) == 0)
                filter.SetLowPass(250 + 1300 * Math.Exp(-t / 0.08), 1.1, Rate);
            var env = Math.Min(1, t / 0.003) * (i < frames ? 1 : 1 - (double)(i - frames) / release);
            var x = (float)((filter.Process(saw.Saw(hz, Rate)) * 0.8 + sub.Sine(hz, Rate) * 0.5) * env);
            b[(at + i) * 2] += x;
            b[(at + i) * 2 + 1] += x;
        }
    }

    /// <summary>
    /// The pads: each bar's chord (root, third, fifth, octave) on pairs of detuned saws, slightly different per side for
    /// width, swelling in over a quarter second and released over 0.6 s, through a low-pass (darker in the half-time song).
    /// </summary>
    private static float[] Pads(NeonSong song, int frames, ref NeonRandom rng)
    {
        var b = new float[frames * 2];
        var bar = 4 * BeatFrames(song);
        for (var k = 0; k < Bars; k++)
        {
            var chord = song.Chords[k];
            var root = Fold(chord.Root, 52);
            ReadOnlySpan<int> intervals = [0, chord.Minor ? 3 : 4, 7, 12];
            foreach (var interval in intervals)
            {
                var hz = Hz(root + interval);
                for (var side = 0; side < 2; side++)
                {
                    var a = new BlepOscillator(rng.NextFloat());
                    var c = new BlepOscillator(rng.NextFloat());
                    var d1 = Math.Pow(2, (side == 0 ? -7 : 5) / 1200.0);
                    var d2 = Math.Pow(2, (side == 0 ? 4 : -6) / 1200.0);
                    var release = (int)(0.6f * Rate);
                    for (var i = 0; i < bar + release && (k * bar + i) * 2 + 1 < b.Length; i++)
                    {
                        var t = i / (float)Rate;
                        var env = MathF.Min(1f, t / 0.25f) * (i < bar ? 1f : MathF.Exp(-(i - bar) / (0.18f * Rate)));
                        b[(k * bar + i) * 2 + side] += (a.Saw(hz * d1, Rate) + c.Saw(hz * d2, Rate)) * 0.12f * env;
                    }
                }
            }
        }

        var cutoff = song.Style == NeonSongStyle.Drive ? 1600 : 1100;
        for (var side = 0; side < 2; side++)
        {
            var lp1 = Biquad.LowPass(cutoff, 0.8, Rate);
            var lp2 = Biquad.LowPass(cutoff * 1.5, 0.7, Rate);
            for (var i = side; i < b.Length; i += 2)
                b[i] = lp2.Process(lp1.Process(b[i]));
        }

        return b;
    }

    /// <summary>The drive's lead: a pulse/saw voice with delayed vibrato, a low-pass and a ping-pong delay.</summary>
    private static float[] Lead(NeonSong song, int frames)
    {
        var mono = new float[frames];
        var beat = BeatFrames(song);
        foreach (var note in song.Melody)
        {
            var at = (int)(note.Beat * beat);
            var length = (int)(note.Length * beat) - beat / 12;
            var hz = Hz(note.Midi);
            var pulse = new BlepOscillator(0);
            var saw = new BlepOscillator(0.3);
            var release = (int)(0.08f * Rate);
            for (var i = 0; i < length + release && at + i < mono.Length; i++)
            {
                var t = i / (double)Rate;
                var vibrato = 1 + 0.004 * Math.Min(1, Math.Max(0, (t - 0.12) / 0.2)) * Math.Sin(2 * Math.PI * 5.5 * t);
                var env = Math.Min(1, t / 0.01) * (i < length ? 1 : 1 - (double)(i - length) / release);
                mono[at + i] += (float)((pulse.Pulse(hz * vibrato, Rate, 0.4) * 0.5 + saw.Saw(hz * vibrato, Rate) * 0.5) * env * 0.5);
            }
        }

        var lp = Biquad.LowPass(3500, 0.7, Rate);
        for (var i = 0; i < mono.Length; i++)
            mono[i] = lp.Process(mono[i]);
        return PingPong(mono, beat * 3 / 4, 0.35f, 0.3f);
    }

    /// <summary>
    /// The half-time song's top line: a plucked saw arpeggio in sixteenths over every bar (root, third, fifth, octave,
    /// up and down) and, in the second half, a bell melody; both through the ping-pong delay.
    /// </summary>
    private static float[] Arp(NeonSong song, int frames)
    {
        var mono = new float[frames];
        var beat = BeatFrames(song);
        ReadOnlySpan<int> pattern = [0, 1, 2, 3, 2, 1, 2, 3];
        for (var bar = 0; bar < Bars; bar++)
        {
            var chord = song.Chords[bar];
            var root = Fold(chord.Root, 62);
            Span<int> tones = [root, root + (chord.Minor ? 3 : 4), root + 7, root + 12];
            for (var s = 0; s < 16; s++)
            {
                var at = (bar * 4 * beat) + s * beat / 4;
                var hz = Hz(tones[pattern[s % pattern.Length]]);
                var saw = new BlepOscillator(0);
                var filter = new Biquad();
                var length = (int)(0.3f * Rate);
                var accent = s % 4 == 0 ? 1f : 0.7f;
                for (var i = 0; i < length && at + i < mono.Length; i++)
                {
                    var t = i / (double)Rate;
                    if ((i & 15) == 0)
                        filter.SetLowPass(600 + 2600 * Math.Exp(-t / 0.05), 1.2, Rate);
                    mono[at + i] += (float)(filter.Process(saw.Saw(hz, Rate)) * Math.Min(1, t / 0.002) * Math.Exp(-t / 0.15) * 0.35 * accent);
                }
            }
        }

        foreach (var note in song.Melody)
        {
            var at = (int)(note.Beat * beat);
            var hz = Hz(note.Midi);
            var length = (int)((note.Length * beat) + 0.8f * Rate);
            for (var i = 0; i < length && at + i < mono.Length; i++)
            {
                var t = i / (double)Rate;
                var mod = Math.Sin(2 * Math.PI * hz * 3.5 * t) * 1.2 * Math.Exp(-t / 0.4); // FM: a glassy bell
                var env = Math.Min(1, t / 0.004) * Math.Exp(-t / 0.7);
                mono[at + i] += (float)(Math.Sin(2 * Math.PI * hz * t + mod) * env * 0.4);
            }
        }

        return PingPong(mono, beat * 3 / 4, 0.4f, 0.35f);
    }

    /// <summary>
    /// Mono in, stereo out: the dry signal centred plus echoes every <paramref name="delay"/> frames alternating right and
    /// left, each <paramref name="feedback"/> times the last and duller (a low-pass at 3 kHz per repeat).
    /// </summary>
    private static float[] PingPong(float[] mono, int delay, float feedback, float wet)
    {
        var b = new float[mono.Length * 2];
        for (var i = 0; i < mono.Length; i++)
        {
            b[i * 2] = mono[i];
            b[i * 2 + 1] = mono[i];
        }

        var echo = (float[])mono.Clone();
        var gain = wet;
        for (var k = 1; k <= 6; k++)
        {
            var lp = Biquad.LowPass(3000, 0.7, Rate);
            var next = new float[mono.Length];
            for (var i = 0; i < mono.Length; i++)
                next[i] = i >= delay ? lp.Process(echo[i - delay]) : 0f;
            var side = k % 2 == 1 ? 1 : 0;
            for (var i = 0; i < mono.Length; i++)
                b[i * 2 + side] += next[i] * gain;
            echo = next;
            gain *= feedback;
        }

        return b;
    }

    /// <summary>The MIDI note of <paramref name="note"/>'s pitch class in the octave starting at <paramref name="low"/>.</summary>
    private static int Fold(int note, int low) => low + (((note - low) % 12) + 12) % 12;

    // ── The TV's jingle ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A TV ad's jingle, mono, a seamless <see cref="JingleSeconds"/> loop (16 beats at 128 BPM): a bouncy major hook on an
    /// FM bell and a square lead (I–V–vi–IV in C), a plucked bass, claps on 2 and 4, and a sparkle at the end. Mono,
    /// because it plays from the TV's speaker (an <see cref="AudioPlayer3D"/>).
    /// </summary>
    public static float[] Jingle(ulong seed)
    {
        var rng = new NeonRandom(seed);
        var beat = Rate * 60 / 128 * 1.0;
        var loop = (int)(JingleSeconds * Rate);
        var b = new float[loop + Rate];
        ReadOnlySpan<int> roots = [48, 43, 45, 41];
        ReadOnlySpan<(float Beat, float Length, int Midi)> hook =
        [
            (0, 0.5f, 72), (0.5f, 0.5f, 76), (1, 0.5f, 79), (1.5f, 1f, 84), (3, 0.5f, 83),
            (4, 0.5f, 79), (4.5f, 0.5f, 83), (5, 1, 86), (6, 0.5f, 83), (6.5f, 0.5f, 79),
            (8, 0.5f, 76), (8.5f, 0.5f, 81), (9, 1, 84), (10, 0.5f, 81), (10.5f, 0.5f, 76),
            (12, 0.5f, 77), (12.5f, 0.5f, 81), (13, 0.5f, 84), (13.5f, 1.5f, 89),
        ];
        foreach (var (start, length, midi) in hook)
        {
            var at = (int)(start * beat);
            var hz = Hz(midi);
            var square = new BlepOscillator(0);
            var frames = (int)(length * beat);
            for (var i = 0; i < frames + Rate / 4 && at + i < b.Length; i++)
            {
                var t = i / (double)Rate;
                var mod = Math.Sin(2 * Math.PI * hz * 2 * t) * 1.5 * Math.Exp(-t / 0.15);
                var bell = Math.Sin(2 * Math.PI * hz * t + mod) * Math.Exp(-t / 0.35);
                var env = Math.Min(1, t / 0.004) * (i < frames ? 1 : Math.Exp(-(i - frames) / (0.03 * Rate)));
                b[at + i] += (float)((bell * 0.5 + square.Pulse(hz, Rate, 0.25) * 0.18 * env) * Math.Min(1, t / 0.003));
            }
        }

        for (var k = 0; k < 4; k++)
        {
            for (var e = 0; e < 4; e++)
            {
                var at = (int)((k * 4 + e) * beat);
                var hz = Hz(roots[k] - 12 + (e == 2 ? 7 : 0));
                var saw = new BlepOscillator(0);
                for (var i = 0; i < (int)(0.9 * beat) && at + i < b.Length; i++)
                {
                    var t = i / (double)Rate;
                    b[at + i] += (float)(saw.Saw(hz, Rate) * Math.Exp(-t / 0.18) * 0.25);
                }
            }

            foreach (var clapBeat in new[] { 1, 3 })
            {
                var at = (int)((k * 4 + clapBeat) * beat);
                var band = Biquad.BandPass(1400, 0.9, Rate);
                for (var i = 0; i < Rate / 6 && at + i < b.Length; i++)
                {
                    var t = i / (float)Rate;
                    // three quick bursts, then the tail: a hand clap
                    var flam = t < 0.022f ? MathF.Exp(-(t % 0.0075f) / 0.0018f) : MathF.Exp(-(t - 0.022f) / 0.05f);
                    b[at + i] += band.Process(rng.NextSigned()) * flam * 0.5f;
                }
            }
        }

        // The sparkle: a run of quick high bells over the last beat.
        for (var s = 0; s < 6; s++)
        {
            var at = (int)((15 + s / 6f) * beat);
            var hz = Hz(96 + s * 2);
            for (var i = 0; i < Rate / 3 && at + i < b.Length; i++)
            {
                var t = i / (double)Rate;
                b[at + i] += (float)(Math.Sin(2 * Math.PI * hz * t) * Math.Exp(-t / 0.12) * 0.12);
            }
        }

        var result = SignalTools.WrapTail(b, 1, loop);
        SignalTools.NormalizeRms(result, JingleRms, maxPeak: 0.5f);
        return result;
    }
}

/// <summary>How a <see cref="NeonSong"/>'s drums, bass and top line go.</summary>
public enum NeonSongStyle
{
    /// <summary>Four on the floor, octave-pulsing bass, a lead melody.</summary>
    Drive,

    /// <summary>Half-time drums, a syncopated bass, an arpeggio with a bell melody.</summary>
    HalfTime,
}

/// <summary>A bar's chord: its root (MIDI) and whether it is minor.</summary>
public readonly record struct NeonChord(int Root, bool Minor);

/// <summary>A lead note: start and length in beats from the loop's start, MIDI pitch.</summary>
public readonly record struct NeonNote(float Beat, float Length, int Midi);

/// <summary>One of the stereo's songs (<see cref="NeonMusic.Songs"/>).</summary>
public sealed record NeonSong(string Id, string Name, int Tempo, NeonSongStyle Style, NeonChord[] Chords, NeonNote[] Melody);

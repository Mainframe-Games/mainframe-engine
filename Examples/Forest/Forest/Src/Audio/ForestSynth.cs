namespace Forest;

/// <summary>
/// The Forest's procedural sounds (no recorded audio): the wind bed, the leaves, the brook, the falls and the
/// footsteps, synthesised from seeded noise and filters. Every function is deterministic for its seed. Loops are
/// seamless (<see cref="SignalTools.FoldLoop"/>); one-shots fade in and out. Levels are set here so the bus mix stays
/// calm: loops by RMS, one-shots by peak, all well below full scale.
/// </summary>
public static class ForestSynth
{
    /// <summary>The rate of everything except the wind roar (the mix rate, so voices do not resample).</summary>
    public const int Rate = 48000;

    /// <summary>The wind roar has nothing above ≈ 2 kHz, so it is kept at a lower rate (a third less memory).</summary>
    public const int WindRate = 32000;

    public const float WindSeconds = 24f;
    public const float RustleSeconds = 17f;
    public const float BrookSeconds = 14f;
    public const float FallsSeconds = 10f;
    private const float LoopFadeSeconds = 1f;

    /// <summary>RMS level of each loop (the bus and node volumes set the mix on top).</summary>
    public const float WindRms = 0.1f;

    public const float RustleRms = 0.06f;
    public const float BrookRms = 0.1f;
    public const float FallsRms = 0.16f;

    /// <summary>
    /// Wind through the trees, stereo at <see cref="WindRate"/>: decorrelated noise per channel through a band-pass
    /// whose centre rises with the gusts (180 → 700 Hz), plus a low rumble, under a slow gust envelope that repeats with
    /// the loop (the right channel lags 0.35 s, so gusts sweep across).
    /// </summary>
    public static float[] WindRoar(ulong seed)
    {
        var rng = new ForestRandom(seed);
        var gust = GustShape.Create(ref rng, WindSeconds);
        var loop = (int)(WindSeconds * WindRate);
        var fade = (int)(LoopFadeSeconds * WindRate);
        var raw = new float[(loop + fade) * 2];
        for (var c = 0; c < 2; c++)
        {
            var noise = new ForestRandom(rng.NextULong());
            var band = new Biquad();
            var soften = Biquad.LowPass(1400, 0.7, WindRate);
            var rumble = new OnePole(70f, WindRate);
            var lag = c * 0.35;
            for (var i = 0; i < loop + fade; i++)
            {
                if ((i & 15) == 0)
                    band.SetBandPass(180 + 520 * gust.At(i / (double)WindRate - lag), 0.9, WindRate);
                var white = noise.NextSigned();
                raw[i * 2 + c] = soften.Process(band.Process(white)) + rumble.Process(white) * 2.5f;
            }
        }

        var result = SignalTools.FoldLoop(raw, 2, loop, fade);
        for (var i = 0; i < loop; i++)
        {
            var t = i / (double)WindRate;
            result[i * 2] *= 0.22f + 0.78f * gust.At(t);
            result[i * 2 + 1] *= 0.22f + 0.78f * gust.At(t - 0.35);
        }

        SignalTools.NormalizeRms(result, WindRms, maxPeak: 0.5f);
        return result;
    }

    /// <summary>
    /// Leaves rustling, stereo at <see cref="Rate"/>: high band noise (2–8 kHz) with a fast random flutter, under its own
    /// gust envelope squared (the leaves only speak up in the gusts).
    /// </summary>
    public static float[] LeafRustle(ulong seed)
    {
        var rng = new ForestRandom(seed);
        var gust = GustShape.Create(ref rng, RustleSeconds);
        var loop = (int)(RustleSeconds * Rate);
        var fade = (int)(LoopFadeSeconds * Rate);
        var raw = new float[(loop + fade) * 2];
        for (var c = 0; c < 2; c++)
        {
            var noise = new ForestRandom(rng.NextULong());
            var high = Biquad.HighPass(2000, 0.7, Rate);
            var top = Biquad.LowPass(8000, 0.7, Rate);
            var flutter = new OnePole(9f, Rate);
            var flutterSmooth = new OnePole(25f, Rate);
            for (var i = 0; i < loop + fade; i++)
            {
                var f = MathF.Abs(flutter.Process(noise.NextSigned()));
                var amount = flutterSmooth.Process(MathF.Min(f * 9f, 1f));
                raw[i * 2 + c] = top.Process(high.Process(noise.NextSigned())) * (0.25f + 0.75f * amount);
            }
        }

        var result = SignalTools.FoldLoop(raw, 2, loop, fade);
        for (var i = 0; i < loop; i++)
        {
            var g = gust.At(i / (double)Rate);
            var level = 0.12f + 0.88f * g * g;
            result[i * 2] *= level;
            result[i * 2 + 1] *= level;
        }

        SignalTools.NormalizeRms(result, RustleRms, maxPeak: 0.5f);
        return result;
    }

    /// <summary>
    /// A babbling brook, mono at <see cref="Rate"/>: a bed of band-passed noise (800 Hz and 2.5 kHz) that undulates, and
    /// random bubbles (Minnaert resonances: decaying sines whose pitch rises, 350 Hz – 2.4 kHz, ≈ 38 per second) with
    /// occasional bursts of babble.
    /// </summary>
    public static float[] Brook(ulong seed) =>
        Water(seed, BrookSeconds, BrookRms, bedGain: 0.25f, hissGain: 0.12f, rumbleGain: 0.15f, bubblesPerSecond: 38f,
            babblePerSecond: 2.2f, minHz: 350f, maxHz: 2400f, bubbleGain: 1f);

    /// <summary>
    /// Falling water, mono at <see cref="Rate"/>: the brook's recipe with a strong low roar, a brighter bed and dense
    /// small bubbles; louder (<see cref="FallsRms"/>).
    /// </summary>
    public static float[] Falls(ulong seed) =>
        Water(seed, FallsSeconds, FallsRms, bedGain: 0.8f, hissGain: 0.6f, rumbleGain: 0.45f, bubblesPerSecond: 260f,
            babblePerSecond: 6f, minHz: 600f, maxHz: 3200f, bubbleGain: 0.35f);

    private static float[] Water(ulong seed, float seconds, float rms, float bedGain, float hissGain, float rumbleGain,
        float bubblesPerSecond, float babblePerSecond, float minHz, float maxHz, float bubbleGain)
    {
        var rng = new ForestRandom(seed);
        var loop = (int)(seconds * Rate);
        var fade = (int)(LoopFadeSeconds * Rate);
        var raw = new float[loop + fade];

        var bed = Biquad.BandPass(800, 0.5, Rate);
        var hiss = Biquad.BandPass(2500, 0.7, Rate);
        var rumble = new OnePole(110f, Rate);
        var rumbleLp = Biquad.LowPass(250, 0.7, Rate);
        var swell = new OnePole(0.7f, Rate);
        var noise = new ForestRandom(rng.NextULong());
        for (var i = 0; i < raw.Length; i++)
        {
            var white = noise.NextSigned();
            var undulate = 0.75f + 0.25f * Math.Clamp(swell.Process(noise.NextSigned()) * 25f, -1f, 1f);
            raw[i] = (bed.Process(white) * bedGain + hiss.Process(white) * hissGain) * undulate
                     + rumbleLp.Process(rumble.Process(white)) * rumbleGain * 6f;
        }

        // Single bubbles (Poisson), and bursts of 4–8 within 80 ms (babble).
        var time = rng.Exponential(1f / bubblesPerSecond);
        while (time < (loop + fade) / (float)Rate)
        {
            Bubble(raw, (int)(time * Rate), ref rng, minHz, maxHz, bubbleGain);
            time += rng.Exponential(1f / bubblesPerSecond);
        }

        time = rng.Exponential(1f / babblePerSecond);
        while (time < (loop + fade) / (float)Rate)
        {
            var count = rng.Range(4, 9);
            for (var b = 0; b < count; b++)
                Bubble(raw, (int)((time + rng.Range(0f, 0.08f)) * Rate), ref rng, minHz, maxHz, bubbleGain);
            time += rng.Exponential(1f / babblePerSecond);
        }

        var result = SignalTools.FoldLoop(raw, 1, loop, fade);
        SignalTools.NormalizeRms(result, rms, maxPeak: 0.7f);
        return result;
    }

    /// <summary>One bubble: a sine at a log-random pitch rising by 1.3–2.4× over its life, 1 ms attack, exponential decay.</summary>
    private static void Bubble(float[] buffer, int start, ref ForestRandom rng, float minHz, float maxHz, float gain)
    {
        var hz = rng.LogRange(minHz, maxHz);
        // Bigger (lower) bubbles ring longer.
        var life = rng.Range(0.006f, 0.022f) * MathF.Sqrt(1000f / hz);
        var rise = rng.Range(1.3f, 2.4f);
        var amp = MathF.Pow(rng.Range(0.05f, 1f), 1.5f) * 0.35f * gain;
        var frames = (int)(life * 3f * Rate);
        var phase = 0.0;
        for (var i = 0; i < frames && start + i < buffer.Length; i++)
        {
            var t = i / (float)Rate;
            var f = hz * (1f + (rise - 1f) * MathF.Min(t / life, 1.5f));
            phase += 2 * Math.PI * f / Rate;
            var env = (1f - MathF.Exp(-t / 0.001f)) * MathF.Exp(-t / (life * 0.45f));
            buffer[start + i] += (float)Math.Sin(phase) * env * amp;
        }
    }

    // ── Footsteps ───────────────────────────────────────────────────────────────────────────────

    /// <summary>The footstep surfaces with their own sounds (anything else plays <c>default</c>).</summary>
    public static readonly string[] FootstepSurfaces = ["grass", "leaves", "moss", "rock", "dirt", "gravel", "mud", "water", "wood", "default"];

    /// <summary>The peak level of each surface's steps (moss is soft, wood and water the loudest).</summary>
    public static float FootstepPeak(string surface) => surface switch
    {
        "moss" => 0.26f,
        "grass" => 0.32f,
        "leaves" => 0.38f,
        "gravel" or "rock" => 0.42f,
        "water" or "wood" => 0.46f,
        _ => 0.38f,
    };

    /// <summary>
    /// One footstep on <paramref name="surface"/>, mono at <see cref="Rate"/>: a heel and a softer toe impact 50–90 ms
    /// apart, each built from the surface's ingredients (a low thump, filtered noise, grains, resonant modes, a sweep),
    /// normalized to <see cref="FootstepPeak"/>.
    /// </summary>
    public static float[] Footstep(string surface, ulong seed)
    {
        var rng = new ForestRandom(seed);
        var length = surface is "water" or "mud" ? 0.5f : 0.34f;
        var buffer = new float[(int)(length * Rate)];
        var toe = (int)(rng.Range(0.05f, 0.09f) * Rate);
        Impact(buffer, 0, 1f, surface, ref rng);
        Impact(buffer, toe, rng.Range(0.5f, 0.75f), surface, ref rng);
        SignalTools.FadeEnds(buffer, Rate / 500);
        SignalTools.NormalizePeak(buffer, FootstepPeak(surface) * rng.Range(0.92f, 1f));
        return buffer;
    }

    private static void Impact(float[] b, int at, float gain, string surface, ref ForestRandom r)
    {
        var p = r.Range(0.9f, 1.12f); // per-impact pitch spread
        switch (surface)
        {
            case "grass":
                Thump(b, at, 90 * p, 0.03f, 0.25f * gain);
                NoiseBurst(b, at, 0.18f, 0.02f, 0.07f, FilterKind.BandPass, 3500 * p, 0.6f, 0.6f * gain, ref r);
                Grains(b, at, 0.12f, 25, 5000 * p, 1f, 0.15f * gain, ref r);
                break;
            case "leaves":
                Thump(b, at, 80 * p, 0.03f, 0.2f * gain);
                NoiseBurst(b, at, 0.15f, 0.012f, 0.05f, FilterKind.BandPass, 2500 * p, 0.6f, 0.3f * gain, ref r);
                Grains(b, at, 0.2f, 60, 3000 * p, 0.8f, 0.5f * gain, ref r);
                break;
            case "moss":
                Thump(b, at, 70 * p, 0.05f, 0.6f * gain);
                NoiseBurst(b, at, 0.12f, 0.01f, 0.04f, FilterKind.LowPass, 700 * p, 0.7f, 0.6f * gain, ref r);
                break;
            case "gravel":
                Thump(b, at, 90 * p, 0.03f, 0.35f * gain);
                Grains(b, at, 0.16f, 90, 4000 * p, 0.7f, 0.6f * gain, ref r);
                Grains(b, at, 0.1f, 30, 1500 * p, 0.8f, 0.4f * gain, ref r);
                break;
            case "rock":
                Thump(b, at, 120 * p, 0.02f, 0.4f * gain);
                NoiseBurst(b, at, 0.004f, 0.0005f, 0.0015f, FilterKind.HighPass, 2000, 0.7f, 0.8f * gain, ref r);
                Modes(b, at, [1900 * p, 2700 * p, 3900 * p], [0.02f, 0.014f, 0.01f], 0.25f * gain, ref r);
                NoiseBurst(b, at, 0.06f, 0.005f, 0.02f, FilterKind.BandPass, 3000 * p, 0.7f, 0.2f * gain, ref r);
                break;
            case "wood":
                Thump(b, at, 110 * p, 0.06f, 0.6f * gain);
                NoiseBurst(b, at, 0.005f, 0.0005f, 0.002f, FilterKind.HighPass, 1500, 0.7f, 0.4f * gain, ref r);
                Modes(b, at, [190 * p, 430 * p, 720 * p, 1150 * p], [0.08f, 0.05f, 0.035f, 0.02f], 0.4f * gain, ref r);
                break;
            case "mud":
                Thump(b, at, 60 * p, 0.07f, 0.5f * gain);
                Sweep(b, at, 0.18f, 1500 * p, 300 * p, 0.015f, 0.06f, 0.5f * gain, ref r);
                Chirp(b, at + (int)(r.Range(0.1f, 0.14f) * Rate), 200 * p, 700 * p, 0.012f, 0.3f * gain);
                break;
            case "water":
                NoiseBurst(b, at, 0.12f, 0.008f, 0.04f, FilterKind.LowPass, 500 * p, 0.7f, 0.4f * gain, ref r);
                Sweep(b, at, 0.25f, 4000 * p, 900 * p, 0.01f, 0.08f, 0.8f * gain, ref r);
                var drops = r.Range(6, 13);
                for (var i = 0; i < drops; i++)
                    Bubble(b, at + (int)(r.Range(0.05f, 0.35f) * Rate), ref r, 600f, 2500f, 0.45f * gain);
                break;
            default: // dirt, and anything unknown
                Thump(b, at, 85 * p, 0.04f, 0.7f * gain);
                NoiseBurst(b, at, 0.1f, 0.006f, 0.03f, FilterKind.LowPass, 1500 * p, 0.7f, 0.5f * gain, ref r);
                Grains(b, at, 0.08f, 10, 2500 * p, 0.8f, 0.15f * gain, ref r);
                break;
        }
    }

    private enum FilterKind { LowPass, HighPass, BandPass }

    /// <summary>A low sine thump with a slight downward pitch and an exponential decay.</summary>
    private static void Thump(float[] b, int at, float hz, float decay, float gain)
    {
        var frames = (int)(decay * 5 * Rate);
        var phase = 0.0;
        for (var i = 0; i < frames && at + i < b.Length; i++)
        {
            var t = i / (float)Rate;
            phase += 2 * Math.PI * hz * (1f - 0.3f * MathF.Min(t / decay, 1f)) / Rate;
            var env = (1f - MathF.Exp(-t / 0.002f)) * MathF.Exp(-t / decay);
            b[at + i] += (float)Math.Sin(phase) * env * gain;
        }
    }

    /// <summary>Filtered white noise under an attack/decay envelope, <paramref name="duration"/> long.</summary>
    private static void NoiseBurst(float[] b, int at, float duration, float attack, float decay, FilterKind kind, float hz, float q,
        float gain, ref ForestRandom r)
    {
        var filter = kind switch
        {
            FilterKind.LowPass => Biquad.LowPass(hz, q, Rate),
            FilterKind.HighPass => Biquad.HighPass(hz, q, Rate),
            _ => Biquad.BandPass(hz, q, Rate),
        };
        var frames = (int)(duration * Rate);
        for (var i = 0; i < frames && at + i < b.Length; i++)
        {
            var t = i / (float)Rate;
            var env = t < attack ? t / attack : MathF.Exp(-(t - attack) / decay);
            b[at + i] += filter.Process(r.NextSigned()) * env * gain;
        }
    }

    /// <summary>
    /// <paramref name="count"/> tiny clicks (0.3–1.5 ms of noise) spread over <paramref name="duration"/>, crowded
    /// towards the start, through a band-pass: crunch.
    /// </summary>
    private static void Grains(float[] b, int at, float duration, int count, float hz, float q, float gain, ref ForestRandom r)
    {
        Span<float> grain = stackalloc float[(int)(0.0015f * Rate) + 1];
        for (var g = 0; g < count; g++)
        {
            var start = at + (int)(duration * MathF.Pow(r.NextFloat(), 1.8f) * Rate);
            var frames = (int)(r.Range(0.0003f, 0.0015f) * Rate);
            var amp = MathF.Pow(r.NextFloat(), 2f) * gain * 3f;
            for (var i = 0; i < frames; i++)
                grain[i] = r.NextSigned() * amp * (1f - (float)i / frames);
            var filter = Biquad.BandPass(hz * r.Range(0.8f, 1.25f), q, Rate);
            // Run the filter on past the grain so it rings out.
            var ring = frames + Rate / 250;
            for (var i = 0; i < ring && start + i < b.Length; i++)
                b[start + i] += filter.Process(i < frames ? grain[i] : 0f);
        }
    }

    /// <summary>Damped sines at <paramref name="hz"/> (resonant modes of a plank or a stone), random phases.</summary>
    private static void Modes(float[] b, int at, ReadOnlySpan<float> hz, ReadOnlySpan<float> decays, float gain, ref ForestRandom r)
    {
        for (var m = 0; m < hz.Length; m++)
        {
            var phase = r.NextFloat() * MathF.PI * 2;
            var amp = gain * r.Range(0.6f, 1f) / (m + 1);
            var frames = (int)(decays[m] * 5 * Rate);
            for (var i = 0; i < frames && at + i < b.Length; i++)
            {
                var t = i / (float)Rate;
                b[at + i] += MathF.Sin(phase + 2 * MathF.PI * hz[m] * t) * MathF.Exp(-t / decays[m]) * amp;
            }
        }
    }

    /// <summary>Noise through a band-pass sweeping from <paramref name="fromHz"/> to <paramref name="toHz"/> (a splash, a squelch).</summary>
    private static void Sweep(float[] b, int at, float duration, float fromHz, float toHz, float attack, float decay, float gain,
        ref ForestRandom r)
    {
        var filter = new Biquad();
        var frames = (int)(duration * Rate);
        for (var i = 0; i < frames && at + i < b.Length; i++)
        {
            var t = i / (float)Rate;
            if ((i & 15) == 0)
                filter.SetBandPass(fromHz * MathF.Pow(toHz / fromHz, t / duration), 0.9, Rate);
            var env = t < attack ? t / attack : MathF.Exp(-(t - attack) / decay);
            b[at + i] += filter.Process(r.NextSigned()) * env * gain;
        }
    }

    /// <summary>A short sine chirp (a suction pop).</summary>
    private static void Chirp(float[] b, int at, float fromHz, float toHz, float duration, float gain)
    {
        var frames = (int)(duration * Rate);
        var phase = 0.0;
        for (var i = 0; i < frames && at + i < b.Length; i++)
        {
            var u = (float)i / frames;
            phase += 2 * Math.PI * (fromHz + (toHz - fromHz) * u) / Rate;
            b[at + i] += (float)Math.Sin(phase) * MathF.Sin(MathF.PI * u) * gain;
        }
    }

    /// <summary>
    /// A slow gust envelope in [0, 1] that repeats every loop: a sum of sines at whole multiples of the loop frequency
    /// (1, 2, 3, 5 and 7 cycles per loop) with random phases and weights, shaped so gusts are rarer than lulls.
    /// </summary>
    private readonly struct GustShape
    {
        private readonly double _period;
        private readonly double[] _weights;
        private readonly double[] _phases;
        private static readonly int[] Harmonics = [1, 2, 3, 5, 7];

        private GustShape(double period, double[] weights, double[] phases)
        {
            _period = period;
            _weights = weights;
            _phases = phases;
        }

        public static GustShape Create(ref ForestRandom rng, double period)
        {
            var weights = new double[Harmonics.Length];
            var phases = new double[Harmonics.Length];
            var total = 0.0;
            for (var k = 0; k < weights.Length; k++)
            {
                weights[k] = rng.Range(0.4f, 1f) / Math.Sqrt(Harmonics[k]);
                phases[k] = rng.NextFloat() * Math.PI * 2;
                total += weights[k];
            }

            for (var k = 0; k < weights.Length; k++)
                weights[k] /= total;
            return new GustShape(period, weights, phases);
        }

        public float At(double seconds)
        {
            var sum = 0.0;
            for (var k = 0; k < _weights.Length; k++)
                sum += _weights[k] * Math.Sin(2 * Math.PI * Harmonics[k] * seconds / _period + _phases[k]);
            var g = Math.Clamp(0.5 + 0.8 * sum, 0, 1); // the weights sum to 1, but a full peak is rare: stretch a little
            return (float)(g * g * (3 - 2 * g)); // smoothstep: lingering lulls and gusts
        }
    }
}

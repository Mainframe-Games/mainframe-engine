namespace Neon;

// The building blocks the recipes share (the Forest's, at Neon's rates): impacts, filtered noise bursts, grains,
// resonant modes, bubbles, tones, echoes and a slow envelope that repeats with its loop.
public static partial class NeonSynth
{
    private enum FilterKind { LowPass, HighPass, BandPass }

    /// <summary>A low sine thump with a slight downward pitch and an exponential decay.</summary>
    private static void Thump(float[] b, int at, float hz, float decay, float gain, int rate = Rate)
    {
        var frames = (int)(decay * 5 * rate);
        var phase = 0.0;
        for (var i = 0; i < frames && at + i < b.Length; i++)
        {
            if (at + i < 0)
                continue;
            var t = i / (float)rate;
            phase += 2 * Math.PI * hz * (1f - 0.3f * MathF.Min(t / decay, 1f)) / rate;
            var env = (1f - MathF.Exp(-t / 0.002f)) * MathF.Exp(-t / decay);
            b[at + i] += (float)Math.Sin(phase) * env * gain;
        }
    }

    /// <summary>Filtered white noise under an attack/decay envelope, <paramref name="duration"/> long.</summary>
    private static void NoiseBurst(float[] b, int at, float duration, float attack, float decay, FilterKind kind, float hz, float q,
        float gain, ref NeonRandom r, int rate = Rate)
    {
        var filter = kind switch
        {
            FilterKind.LowPass => Biquad.LowPass(hz, q, rate),
            FilterKind.HighPass => Biquad.HighPass(hz, q, rate),
            _ => Biquad.BandPass(hz, q, rate),
        };
        var frames = (int)(duration * rate);
        for (var i = 0; i < frames && at + i < b.Length; i++)
        {
            var t = i / (float)rate;
            var env = t < attack ? t / attack : MathF.Exp(-(t - attack) / decay);
            var x = filter.Process(r.NextSigned()) * env * gain;
            if (at + i >= 0)
                b[at + i] += x;
        }
    }

    /// <summary>
    /// <paramref name="count"/> tiny clicks (0.3–1.5 ms of noise) spread over <paramref name="duration"/>, crowded
    /// towards the start, through a band-pass: crunch, rattle, patter.
    /// </summary>
    private static void Grains(float[] b, int at, float duration, int count, float hz, float q, float gain, ref NeonRandom r, float crowd = 1.8f)
    {
        Span<float> grain = stackalloc float[(int)(0.0015f * Rate) + 1];
        for (var g = 0; g < count; g++)
        {
            var start = at + (int)(duration * MathF.Pow(r.NextFloat(), crowd) * Rate);
            var frames = (int)(r.Range(0.0003f, 0.0015f) * Rate);
            var amp = MathF.Pow(r.NextFloat(), 2f) * gain * 3f;
            for (var i = 0; i < frames; i++)
                grain[i] = r.NextSigned() * amp * (1f - (float)i / frames);
            var filter = Biquad.BandPass(hz * r.Range(0.8f, 1.25f), q, Rate);
            var ring = frames + Rate / 250; // let the filter ring out past the grain
            for (var i = 0; i < ring && start + i < b.Length; i++)
                b[start + i] += filter.Process(i < frames ? grain[i] : 0f);
        }
    }

    /// <summary>Damped sines at <paramref name="hz"/> (resonant modes of a plank, a grate, a latch), random phases.</summary>
    private static void Modes(float[] b, int at, ReadOnlySpan<float> hz, ReadOnlySpan<float> decays, float gain, ref NeonRandom r)
    {
        for (var m = 0; m < hz.Length; m++)
        {
            var phase = r.NextFloat() * MathF.PI * 2;
            var amp = gain * r.Range(0.6f, 1f) / (m + 1);
            var frames = (int)(decays[m] * 5 * Rate);
            for (var i = 0; i < frames && at + i < b.Length; i++)
            {
                var t = i / (float)Rate;
                var attack = 1f - MathF.Exp(-t / 0.0004f);
                b[at + i] += MathF.Sin(phase + 2 * MathF.PI * hz[m] * t) * MathF.Exp(-t / decays[m]) * amp * attack;
            }
        }
    }

    /// <summary>Noise through a band-pass sweeping from <paramref name="fromHz"/> to <paramref name="toHz"/> (a whoosh, a splash).</summary>
    private static void Sweep(float[] b, int at, float duration, float fromHz, float toHz, float attack, float decay, float gain,
        ref NeonRandom r, double q = 0.9)
    {
        var filter = new Biquad();
        var frames = (int)(duration * Rate);
        for (var i = 0; i < frames && at + i < b.Length; i++)
        {
            var t = i / (float)Rate;
            if ((i & 15) == 0)
                filter.SetBandPass(fromHz * MathF.Pow(toHz / fromHz, t / duration), q, Rate);
            var env = t < attack ? t / attack : MathF.Exp(-(t - attack) / decay);
            b[at + i] += filter.Process(r.NextSigned()) * env * gain;
        }
    }

    /// <summary>One water bubble or drip: a sine at a log-random pitch rising 1.3–2.4× over its life, 1 ms attack, decaying.</summary>
    private static void Bubble(float[] buffer, int start, ref NeonRandom rng, float minHz, float maxHz, float gain, int rate = Rate)
    {
        var hz = rng.LogRange(minHz, maxHz);
        var life = rng.Range(0.006f, 0.022f) * MathF.Sqrt(1000f / hz); // bigger (lower) bubbles ring longer
        var rise = rng.Range(1.3f, 2.4f);
        var amp = MathF.Pow(rng.Range(0.05f, 1f), 1.5f) * 0.35f * gain;
        var frames = (int)(life * 3f * rate);
        var phase = 0.0;
        for (var i = 0; i < frames && start + i < buffer.Length; i++)
        {
            var t = i / (float)rate;
            var f = hz * (1f + (rise - 1f) * MathF.Min(t / life, 1.5f));
            phase += 2 * Math.PI * f / rate;
            var env = (1f - MathF.Exp(-t / 0.001f)) * MathF.Exp(-t / (life * 0.45f));
            buffer[start + i] += (float)Math.Sin(phase) * env * amp;
        }
    }

    /// <summary>A tone (a band-limited pulse or saw blended with a sine) under a linear attack and release.</summary>
    private static void Tone(float[] b, int at, float duration, float hz, float attack, float release, float gain, float pulseMix,
        double width = 0.5)
    {
        var osc = new BlepOscillator(0);
        var sine = new BlepOscillator(0);
        var frames = (int)(duration * Rate);
        for (var i = 0; i < frames && at + i < b.Length; i++)
        {
            var t = i / (float)Rate;
            var env = MathF.Min(1f, t / MathF.Max(attack, 1e-4f)) * MathF.Min(1f, (duration - t) / MathF.Max(release, 1e-4f));
            var x = osc.Pulse(hz, Rate, width) * pulseMix + sine.Sine(hz, Rate) * (1f - pulseMix);
            b[at + i] += x * MathF.Max(env, 0f) * gain;
        }
    }

    /// <summary>A bell-like chime: inharmonic sine partials (1, 2.76, 5.4, 8.9) decaying at different rates.</summary>
    private static void Chime(float[] b, int at, float hz, float decay, float gain)
    {
        ReadOnlySpan<float> ratios = [1f, 2.76f, 5.4f, 8.9f];
        ReadOnlySpan<float> levels = [1f, 0.45f, 0.22f, 0.1f];
        var frames = (int)(decay * 6 * Rate);
        for (var p = 0; p < ratios.Length; p++)
        {
            var f = hz * ratios[p];
            var d = decay / (1f + p * 0.9f);
            for (var i = 0; i < frames && at + i < b.Length; i++)
            {
                var t = i / (float)Rate;
                var env = (1f - MathF.Exp(-t / 0.002f)) * MathF.Exp(-t / d);
                b[at + i] += MathF.Sin(2 * MathF.PI * f * t) * env * levels[p] * gain;
            }
        }
    }

    /// <summary>Adds delayed, low-passed copies (a street's reflections, a stairwell): each tap is (seconds, gain).</summary>
    private static void Echoes(float[] b, ReadOnlySpan<(float Seconds, float Gain)> taps, float lowPassHz)
    {
        var dry = (float[])b.Clone();
        foreach (var (seconds, gain) in taps)
        {
            var delay = (int)(seconds * Rate);
            var lp = Biquad.LowPass(lowPassHz, 0.7, Rate);
            for (var i = delay; i < b.Length; i++)
                b[i] += lp.Process(dry[i - delay]) * gain;
        }
    }

    /// <summary>Low-passes a whole buffer (in place) with <paramref name="stages"/> cascaded biquads (distance, walls).</summary>
    private static void LowPass(float[] b, double hz, int stages = 1, int rate = Rate, int channels = 1)
    {
        for (var c = 0; c < channels; c++)
        {
            for (var s = 0; s < stages; s++)
            {
                var lp = Biquad.LowPass(hz, 0.707, rate);
                for (var i = c; i < b.Length; i += channels)
                    b[i] = lp.Process(b[i]);
            }
        }
    }

    /// <summary>High-passes a whole buffer (in place).</summary>
    private static void HighPass(float[] b, double hz, int rate = Rate, int channels = 1)
    {
        for (var c = 0; c < channels; c++)
        {
            var hp = Biquad.HighPass(hz, 0.707, rate);
            for (var i = c; i < b.Length; i += channels)
                b[i] = hp.Process(b[i]);
        }
    }

    /// <summary>
    /// A slow envelope in [0, 1] that repeats every loop: a sum of sines at whole multiples of the loop frequency
    /// (1, 2, 3, 5 and 7 cycles per loop) with random phases and weights, smoothstepped (swells of rain, traffic waves).
    /// </summary>
    private readonly struct LoopShape
    {
        private static readonly int[] Harmonics = [1, 2, 3, 5, 7];
        private readonly double _period;
        private readonly double[] _weights;
        private readonly double[] _phases;

        private LoopShape(double period, double[] weights, double[] phases)
        {
            _period = period;
            _weights = weights;
            _phases = phases;
        }

        public static LoopShape Create(ref NeonRandom rng, double period)
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
            return new LoopShape(period, weights, phases);
        }

        public float At(double seconds)
        {
            var sum = 0.0;
            for (var k = 0; k < _weights.Length; k++)
                sum += _weights[k] * Math.Sin(2 * Math.PI * Harmonics[k] * seconds / _period + _phases[k]);
            var g = Math.Clamp(0.5 + 0.8 * sum, 0, 1);
            return (float)(g * g * (3 - 2 * g));
        }
    }
}

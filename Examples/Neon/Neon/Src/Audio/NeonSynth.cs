namespace Neon;

/// <summary>
/// Neon's procedural sounds (no recorded audio, as in the Forest): the rain and the city outside, the apartment's
/// machines and water, its foley and footsteps, synthesised from seeded noise, filters and oscillators. Every function
/// is deterministic for its seed. Loops are seamless (<see cref="SignalTools.FoldLoop"/>, and tones at whole cycles per
/// loop); one-shots fade in and out. Levels are set here so the bus mix stays calm: loops by RMS, one-shots by peak, all
/// well below full scale. The music is <see cref="NeonMusic"/>.
/// </summary>
public static partial class NeonSynth
{
    /// <summary>The rate of the bright sounds (the mix rate, so voices do not resample).</summary>
    public const int Rate = 48000;

    /// <summary>The dull sounds (the city drone, the train, the airship, the fridge) have nothing above ≈ 8 kHz: half the memory.</summary>
    public const int LowRate = 24000;

    private const float LoopFadeSeconds = 1f;

    public const float RainOutdoorSeconds = 14f;
    public const float RainIndoorSeconds = 12f;
    public const float CitySeconds = 20f;
    public const float CityIndoorSeconds = 18f;
    public const float TrainSeconds = 12f;
    public const float AirshipSeconds = 10f;
    public const float ShowerSeconds = 10f;
    public const float TapSeconds = 8f;
    public const float FridgeSeconds = 12f;
    public const float PcFansSeconds = 10f;
    public const float TvStaticSeconds = 6f;

    /// <summary>RMS level of each loop (the bus and node volumes set the mix on top).</summary>
    public const float RainOutdoorRms = 0.12f;

    public const float RainIndoorRms = 0.045f;
    public const float CityRms = 0.1f;
    public const float CityIndoorRms = 0.045f;
    public const float TrainRms = 0.12f;
    public const float AirshipRms = 0.1f;
    public const float ShowerRms = 0.12f;
    public const float TapRms = 0.08f;
    public const float FridgeRms = 0.05f;
    public const float PcFansRms = 0.06f;
    public const float TvStaticRms = 0.08f;

    // ── Rain ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Rain in the open (the balcony), stereo: decorrelated noise per channel in three bands (a bright hiss, a mid body,
    /// a low splash), a spray of tiny drop impacts (≈ 300 a second per channel, 2–8 kHz) and a few metallic tinks on the
    /// railing, under a slow swell that repeats with the loop.
    /// </summary>
    public static float[] RainOutdoor(ulong seed)
    {
        var rng = new NeonRandom(seed);
        var swell = LoopShape.Create(ref rng, RainOutdoorSeconds);
        var loop = (int)(RainOutdoorSeconds * Rate);
        var fade = (int)(LoopFadeSeconds * Rate);
        var raw = new float[(loop + fade) * 2];
        var channel = new float[loop + fade];
        for (var c = 0; c < 2; c++)
        {
            Array.Clear(channel);
            var noise = new NeonRandom(rng.NextULong());
            var hissHp = Biquad.HighPass(500, 0.7, Rate);
            var hissLp = Biquad.LowPass(8500, 0.7, Rate);
            var body = Biquad.BandPass(1200, 0.5, Rate);
            var splash = Biquad.LowPass(250, 0.7, Rate);
            for (var i = 0; i < channel.Length; i++)
            {
                var white = noise.NextSigned();
                channel[i] = hissLp.Process(hissHp.Process(white)) * 0.5f + body.Process(white) * 0.45f + splash.Process(white) * 1.6f;
            }

            var seconds = channel.Length / (float)Rate;
            Grains(channel, 0, seconds, (int)(150 * seconds), 2500f, 1.2f, 0.06f, ref noise, crowd: 1f);
            Grains(channel, 0, seconds, (int)(110 * seconds), 4500f, 1.2f, 0.05f, ref noise, crowd: 1f);
            Grains(channel, 0, seconds, (int)(60 * seconds), 7500f, 1.2f, 0.04f, ref noise, crowd: 1f);
            // Fat drops on the railing and the balcony's steel: a quiet tink now and then.
            var time = noise.Exponential(0.6f);
            while (time < seconds)
            {
                var p = noise.Range(0.85f, 1.2f);
                Modes(channel, (int)(time * Rate), [2350 * p, 3720 * p, 5480 * p], [0.012f, 0.008f, 0.005f], 0.05f * noise.Range(0.3f, 1f), ref noise);
                time += noise.Exponential(0.6f);
            }

            for (var i = 0; i < channel.Length; i++)
                raw[i * 2 + c] = channel[i];
        }

        var result = SignalTools.FoldLoop(raw, 2, loop, fade);
        for (var i = 0; i < loop; i++)
        {
            var g = 0.72f + 0.28f * swell.At(i / (double)Rate);
            result[i * 2] *= g;
            result[i * 2 + 1] *= g;
        }

        SignalTools.NormalizeRms(result, RainOutdoorRms, maxPeak: 0.6f);
        return result;
    }

    /// <summary>
    /// The rain from inside, stereo: the downpour through the glass (a low-passed roar, nothing above ≈ 700 Hz), patter
    /// on the panes (≈ 18 glassy ticks a second per channel), slow drips (rising bubbles) and a faint trickle running
    /// down the window.
    /// </summary>
    public static float[] RainIndoor(ulong seed)
    {
        var rng = new NeonRandom(seed);
        var swell = LoopShape.Create(ref rng, RainIndoorSeconds);
        var loop = (int)(RainIndoorSeconds * Rate);
        var fade = (int)(LoopFadeSeconds * Rate);
        var raw = new float[(loop + fade) * 2];
        var channel = new float[loop + fade];
        for (var c = 0; c < 2; c++)
        {
            Array.Clear(channel);
            var noise = new NeonRandom(rng.NextULong());
            var lp1 = Biquad.LowPass(650, 0.7, Rate);
            var lp2 = Biquad.LowPass(650, 0.7, Rate);
            var hp = Biquad.HighPass(70, 0.7, Rate);
            var trickle = Biquad.BandPass(950, 1.5, Rate);
            var flutter = new OnePole(6f, Rate);
            for (var i = 0; i < channel.Length; i++)
            {
                var white = noise.NextSigned();
                var wobble = 0.5f + 0.5f * Math.Clamp(flutter.Process(noise.NextSigned()) * 30f, -1f, 1f);
                channel[i] = hp.Process(lp2.Process(lp1.Process(white))) * 1.4f + trickle.Process(white) * 0.03f * wobble;
            }

            var seconds = channel.Length / (float)Rate;
            // Patter: drops hitting the glass, a tick with a short glassy ring.
            var time = noise.Exponential(1f / 18f);
            while (time < seconds)
            {
                var at = (int)(time * Rate);
                var gain = MathF.Pow(noise.Range(0.05f, 1f), 1.6f);
                var p = noise.Range(0.8f, 1.25f);
                NoiseBurst(channel, at, 0.004f, 0.0002f, 0.0012f, FilterKind.BandPass, 3400f * p, 0.9f, 0.22f * gain, ref noise);
                Modes(channel, at, [2650 * p, 3950 * p], [0.006f, 0.004f], 0.05f * gain, ref noise);
                time += noise.Exponential(1f / 18f);
            }

            // Drips (from the frame, the sill): a rising "plip" every second or two.
            time = noise.Exponential(1.6f);
            while (time < seconds)
            {
                Bubble(channel, (int)(time * Rate), ref noise, 700f, 1900f, 1.3f);
                time += noise.Exponential(1.6f);
            }

            for (var i = 0; i < channel.Length; i++)
                raw[i * 2 + c] = channel[i];
        }

        var result = SignalTools.FoldLoop(raw, 2, loop, fade);
        for (var i = 0; i < loop; i++)
        {
            var g = 0.75f + 0.25f * swell.At(i / (double)Rate);
            result[i * 2] *= g;
            result[i * 2 + 1] *= g;
        }

        SignalTools.NormalizeRms(result, RainIndoorRms, maxPeak: 0.6f);
        return result;
    }

    // ── The city ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The city twenty storeys down, stereo at <see cref="LowRate"/>: a low rumble, a mains-like hum (55 Hz and its
    /// harmonics, beating slowly), a traffic wash of band-passed noise in slow waves, and single distant cars rising and
    /// falling across the stereo field. <paramref name="indoor"/>: the same city through the walls and glass (two
    /// low-pass stages at 320 Hz, the wash mostly gone, quieter).
    /// </summary>
    public static float[] CityDrone(ulong seed, bool indoor = false)
    {
        var seconds = indoor ? CityIndoorSeconds : CitySeconds;
        var rng = new NeonRandom(seed);
        var waves = LoopShape.Create(ref rng, seconds);
        var loop = (int)(seconds * LowRate);
        var fade = (int)(LoopFadeSeconds * LowRate);
        var raw = new float[(loop + fade) * 2];
        var channel = new float[loop + fade];
        var total = channel.Length / (float)LowRate;
        for (var c = 0; c < 2; c++)
        {
            Array.Clear(channel);
            var noise = new NeonRandom(rng.NextULong());
            var rumble1 = Biquad.LowPass(90, 0.7, LowRate);
            var rumble2 = Biquad.LowPass(90, 0.7, LowRate);
            var subsonic = Biquad.HighPass(28, 0.7, LowRate); // nothing below hearing: it only costs headroom
            var washBand = Biquad.BandPass(520, 0.4, LowRate);
            var washTop = Biquad.LowPass(1600, 0.7, LowRate);
            for (var i = 0; i < channel.Length; i++)
            {
                var white = noise.NextSigned();
                var wash = washTop.Process(washBand.Process(white)) * (0.35f + 0.65f * waves.At(i / (double)LowRate - c * 1.3));
                channel[i] = subsonic.Process(rumble2.Process(rumble1.Process(white))) * 5f + wash * 0.6f;
            }

            // Distant cars: a band of noise swelling and fading over 2–4 s, panned.
            var time = noise.Exponential(1.2f);
            while (time < total)
            {
                var length = noise.Range(2f, 4f);
                var hz = noise.Range(280f, 900f);
                var pan = noise.NextFloat();
                var gain = noise.Range(0.1f, 0.35f) * (c == 0 ? MathF.Sqrt(1f - pan) : MathF.Sqrt(pan));
                var band = Biquad.BandPass(hz, 1.2, LowRate);
                var start = (int)(time * LowRate);
                var frames = (int)(length * LowRate);
                for (var i = 0; i < frames && start + i < channel.Length; i++)
                {
                    var u = (float)i / frames;
                    var bell = MathF.Sin(MathF.PI * u);
                    channel[start + i] += band.Process(noise.NextSigned()) * bell * bell * gain;
                }

                time += noise.Exponential(1.2f);
            }

            for (var i = 0; i < channel.Length; i++)
                raw[i * 2 + c] = channel[i];
        }

        var result = SignalTools.FoldLoop(raw, 2, loop, fade);
        // The hum: whole cycles per loop, so it needs no crossfade (added after the fold).
        var f1 = SignalTools.LoopHz(55, seconds);
        var f1b = SignalTools.LoopHz(55.25, seconds);
        for (var i = 0; i < loop; i++)
        {
            var t = i / (double)LowRate;
            var hum = Math.Sin(2 * Math.PI * f1 * t) * 0.5 + Math.Sin(2 * Math.PI * f1b * t) * 0.35 + Math.Sin(2 * Math.PI * 2 * f1 * t + 1.1) * 0.3 +
                      Math.Sin(2 * Math.PI * 3 * f1 * t + 2.3) * 0.12 + Math.Sin(2 * Math.PI * 4 * f1 * t + 0.4) * 0.08;
            result[i * 2] += (float)hum * 0.05f;
            result[i * 2 + 1] += (float)hum * 0.05f;
        }

        if (indoor)
            LowPassLoop(result, 320, stages: 2, rate: LowRate, channels: 2);
        SignalTools.NormalizeRms(result, indoor ? CityIndoorRms : CityRms, maxPeak: 0.6f);
        return result;
    }

    /// <summary>
    /// Low-passes a finished loop in place, seamlessly: each filter is first run over the loop's last half second, so it
    /// enters the start in the state the end leaves it in (a filter started from rest would put a step at the seam).
    /// </summary>
    private static void LowPassLoop(float[] loop, double hz, int stages, int rate, int channels)
    {
        var frames = loop.Length / channels;
        var warm = Math.Min(frames, rate / 2);
        for (var c = 0; c < channels; c++)
        {
            for (var s = 0; s < stages; s++)
            {
                var lp = Biquad.LowPass(hz, 0.707, rate);
                for (var i = frames - warm; i < frames; i++)
                    lp.Process(loop[i * channels + c]);
                for (var i = 0; i < frames; i++)
                    loop[i * channels + c] = lp.Process(loop[i * channels + c]);
            }
        }
    }

    /// <summary>
    /// An elevated train going by, mono at <see cref="LowRate"/> (a loop: the emitter moves along the track): a low
    /// rumble, the motors' hum, the wheels' clickety-clack over the rail joints (a car a second, two bogies of two
    /// axles) and a faint flange squeal.
    /// </summary>
    public static float[] TrainRumble(ulong seed)
    {
        var rng = new NeonRandom(seed);
        var squealShape = LoopShape.Create(ref rng, TrainSeconds);
        var loop = (int)(TrainSeconds * LowRate);
        var fade = (int)(LoopFadeSeconds * LowRate);
        var raw = new float[loop + fade];
        var noise = new NeonRandom(rng.NextULong());
        var lp1 = Biquad.LowPass(120, 0.7, LowRate);
        var lp2 = Biquad.LowPass(120, 0.7, LowRate);
        var subsonic = Biquad.HighPass(28, 0.7, LowRate);
        var mid = Biquad.BandPass(320, 0.6, LowRate);
        var squeal = Biquad.BandPass(2400, 9, LowRate);
        for (var i = 0; i < raw.Length; i++)
        {
            var white = noise.NextSigned();
            var s = squealShape.At(i / (double)LowRate);
            raw[i] = subsonic.Process(lp2.Process(lp1.Process(white))) * 6f + mid.Process(white) * 0.2f + squeal.Process(white) * 0.08f * s * s;
        }

        // Clickety-clack: the loop is a whole number of cars (1 s each), so the rhythm runs on across the seam.
        for (var car = 0; car * 1f < TrainSeconds + LoopFadeSeconds; car++)
        {
            ReadOnlySpan<float> axles = [0f, 0.12f, 0.62f, 0.74f];
            foreach (var axle in axles)
            {
                var at = (int)((car + axle) * LowRate);
                var g = noise.Range(0.8f, 1f);
                Thump(raw, at, noise.Range(60f, 75f), 0.035f, 1f * g, LowRate);
                NoiseBurst(raw, at, 0.04f, 0.0005f, 0.012f, FilterKind.BandPass, noise.Range(700f, 1000f), 1.2f, 2.2f * g, ref noise, LowRate);
                NoiseBurst(raw, at, 0.03f, 0.0003f, 0.006f, FilterKind.BandPass, noise.Range(1800f, 2600f), 4f, 1.6f * g, ref noise, LowRate);
            }
        }

        var result = SignalTools.FoldLoop(raw, 1, loop, fade);
        var hz = SignalTools.LoopHz(95, TrainSeconds);
        for (var i = 0; i < loop; i++)
        {
            var t = i / (double)LowRate;
            result[i] += (float)(Math.Sin(2 * Math.PI * hz * t) * 0.6 + Math.Sin(2 * Math.PI * 2 * hz * t) * 0.3 + Math.Sin(2 * Math.PI * 3 * hz * t) * 0.15) * 0.04f;
        }

        SignalTools.NormalizeRms(result, TrainRms, maxPeak: 0.7f);
        return result;
    }

    /// <summary>
    /// An airship's engines, mono at <see cref="LowRate"/>: a deep throb (32 Hz and its harmonics, pulsing at the
    /// propellers' 5.5 Hz blade rate), two engines beating against each other (76 and 76.3 Hz) and the prop wash.
    /// </summary>
    public static float[] AirshipDrone(ulong seed)
    {
        var rng = new NeonRandom(seed);
        var gust = LoopShape.Create(ref rng, AirshipSeconds);
        var loop = (int)(AirshipSeconds * LowRate);
        var fade = (int)(LoopFadeSeconds * LowRate);
        var raw = new float[loop + fade];
        var noise = new NeonRandom(rng.NextULong());
        var wash = Biquad.LowPass(350, 0.7, LowRate);
        for (var i = 0; i < raw.Length; i++)
            raw[i] = wash.Process(noise.NextSigned()) * (0.6f + 0.4f * gust.At(i / (double)LowRate));

        var result = SignalTools.FoldLoop(raw, 1, loop, fade);
        var f0 = SignalTools.LoopHz(32, AirshipSeconds);
        var blade = SignalTools.LoopHz(5.5, AirshipSeconds);
        var e1 = SignalTools.LoopHz(76, AirshipSeconds);
        var e2 = SignalTools.LoopHz(76.3, AirshipSeconds);
        for (var i = 0; i < loop; i++)
        {
            var t = i / (double)LowRate;
            var throb = 0.6 + 0.4 * Math.Sin(2 * Math.PI * blade * t);
            var body = Math.Sin(2 * Math.PI * f0 * t) + 0.6 * Math.Sin(2 * Math.PI * 2 * f0 * t + 0.7) + 0.4 * Math.Sin(2 * Math.PI * 3 * f0 * t + 1.9) +
                       0.25 * Math.Sin(2 * Math.PI * 4 * f0 * t + 2.6);
            var engines = Math.Sin(2 * Math.PI * e1 * t) + Math.Sin(2 * Math.PI * e2 * t + 0.5);
            result[i] += (float)(body * throb * 0.12 + engines * 0.05);
        }

        SignalTools.NormalizeRms(result, AirshipRms, maxPeak: 0.7f);
        return result;
    }

    // ── The apartment ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The shower running, mono: a bright spray hiss (1.5–11 kHz) fluttering as the water breaks, a dense patter of
    /// drops on the tiles (≈ 1 400 grains a second), the splash's body and a few bubbles at the drain.
    /// </summary>
    public static float[] Shower(ulong seed)
    {
        var rng = new NeonRandom(seed);
        var loop = (int)(ShowerSeconds * Rate);
        var fade = (int)(LoopFadeSeconds * Rate);
        var raw = new float[loop + fade];
        var noise = new NeonRandom(rng.NextULong());
        var hp = Biquad.HighPass(1500, 0.7, Rate);
        var lp = Biquad.LowPass(11000, 0.7, Rate);
        var body = Biquad.BandPass(600, 0.5, Rate);
        var flutter = new OnePole(30f, Rate);
        var swell = new OnePole(0.8f, Rate);
        for (var i = 0; i < raw.Length; i++)
        {
            var white = noise.NextSigned();
            var f = 0.8f + 0.2f * Math.Clamp(flutter.Process(noise.NextSigned()) * 12f, -1f, 1f);
            var s = 0.85f + 0.15f * Math.Clamp(swell.Process(noise.NextSigned()) * 25f, -1f, 1f);
            raw[i] = lp.Process(hp.Process(white)) * 0.38f * f + body.Process(white) * 0.5f * s;
        }

        var seconds = raw.Length / (float)Rate;
        Grains(raw, 0, seconds, (int)(800 * seconds), 3200f, 1f, 0.05f, ref noise, crowd: 1f);
        Grains(raw, 0, seconds, (int)(600 * seconds), 6500f, 1f, 0.04f, ref noise, crowd: 1f);
        var time = noise.Exponential(0.12f);
        while (time < seconds)
        {
            Bubble(raw, (int)(time * Rate), ref noise, 300f, 900f, 0.5f);
            time += noise.Exponential(0.12f);
        }

        var result = SignalTools.FoldLoop(raw, 1, loop, fade);
        SignalTools.NormalizeRms(result, ShowerRms, maxPeak: 0.7f);
        return result;
    }

    /// <summary>
    /// A tap running into a sink, mono: a narrow stream (band-passed noise at 1.4 and 3.2 kHz, swelling a little) over a
    /// dense gurgle of bubbles (≈ 60 a second, 500 Hz – 2 kHz) with bursts of babble.
    /// </summary>
    public static float[] Tap(ulong seed)
    {
        var rng = new NeonRandom(seed);
        var loop = (int)(TapSeconds * Rate);
        var fade = (int)(LoopFadeSeconds * Rate);
        var raw = new float[loop + fade];
        var noise = new NeonRandom(rng.NextULong());
        var stream = Biquad.BandPass(1400, 0.7, Rate);
        var hiss = Biquad.BandPass(3200, 0.8, Rate);
        var swell = new OnePole(1.2f, Rate);
        for (var i = 0; i < raw.Length; i++)
        {
            var white = noise.NextSigned();
            var s = 0.8f + 0.2f * Math.Clamp(swell.Process(noise.NextSigned()) * 25f, -1f, 1f);
            raw[i] = (stream.Process(white) * 0.5f + hiss.Process(white) * 0.2f) * s;
        }

        var seconds = raw.Length / (float)Rate;
        var time = noise.Exponential(1f / 60f);
        while (time < seconds)
        {
            Bubble(raw, (int)(time * Rate), ref noise, 500f, 2000f, 1f);
            time += noise.Exponential(1f / 60f);
        }

        time = noise.Exponential(0.5f);
        while (time < seconds)
        {
            var count = noise.Range(4, 9);
            for (var b = 0; b < count; b++)
                Bubble(raw, (int)((time + noise.Range(0f, 0.08f)) * Rate), ref noise, 600f, 2200f, 1f);
            time += noise.Exponential(0.5f);
        }

        var result = SignalTools.FoldLoop(raw, 1, loop, fade);
        SignalTools.NormalizeRms(result, TapRms, maxPeak: 0.7f);
        return result;
    }

    /// <summary>
    /// The fridge, mono at <see cref="LowRate"/>: the compressor's hum (50 Hz and harmonics, softly saturated, with a
    /// slight rattle) and the condenser fan's low whoosh.
    /// </summary>
    public static float[] FridgeHum(ulong seed)
    {
        var rng = new NeonRandom(seed);
        var loop = (int)(FridgeSeconds * LowRate);
        var fade = (int)(LoopFadeSeconds * LowRate);
        var raw = new float[loop + fade];
        var noise = new NeonRandom(rng.NextULong());
        var lp = Biquad.LowPass(700, 0.7, LowRate);
        var hp = Biquad.HighPass(150, 0.7, LowRate);
        for (var i = 0; i < raw.Length; i++)
            raw[i] = hp.Process(lp.Process(noise.NextSigned())) * 0.25f;

        var result = SignalTools.FoldLoop(raw, 1, loop, fade);
        var f = SignalTools.LoopHz(50, FridgeSeconds);
        var rattle = SignalTools.LoopHz(7.25, FridgeSeconds);
        for (var i = 0; i < loop; i++)
        {
            var t = i / (double)LowRate;
            var hum = 0.5 * Math.Sin(2 * Math.PI * f * t) + 0.8 * Math.Sin(2 * Math.PI * 2 * f * t + 0.3) * (1 + 0.12 * Math.Sin(2 * Math.PI * rattle * t)) +
                      0.3 * Math.Sin(2 * Math.PI * 3 * f * t + 1.2) + 0.15 * Math.Sin(2 * Math.PI * 4 * f * t + 2.1) + 0.05 * Math.Sin(2 * Math.PI * 6 * f * t);
            result[i] += (float)Math.Tanh(hum * 1.6) * 0.35f;
        }

        SignalTools.NormalizeRms(result, FridgeRms, maxPeak: 0.5f);
        return result;
    }

    /// <summary>
    /// The computer room's machines, mono: fan noise (80 Hz – 3 kHz), two fans' blade tones beating at 2.4 Hz, a
    /// server rack's faint coil whine (7.4 and 11.25 kHz) and now and then a drive's seek ticks.
    /// </summary>
    public static float[] PcFans(ulong seed)
    {
        var rng = new NeonRandom(seed);
        var loop = (int)(PcFansSeconds * Rate);
        var fade = (int)(LoopFadeSeconds * Rate);
        var raw = new float[loop + fade];
        var noise = new NeonRandom(rng.NextULong());
        var lp = Biquad.LowPass(3000, 0.7, Rate);
        var hp = Biquad.HighPass(120, 0.7, Rate);
        for (var i = 0; i < raw.Length; i++)
            raw[i] = hp.Process(lp.Process(noise.NextSigned()));

        var seconds = raw.Length / (float)Rate;
        var time = noise.Exponential(0.7f);
        while (time < seconds)
        {
            var ticks = noise.Range(2, 6);
            for (var k = 0; k < ticks; k++)
                NoiseBurst(raw, (int)((time + k * noise.Range(0.012f, 0.03f)) * Rate), 0.004f, 0.0002f, 0.001f, FilterKind.HighPass, 2500f, 0.7f, 0.25f, ref noise);
            time += noise.Exponential(0.7f);
        }

        var result = SignalTools.FoldLoop(raw, 1, loop, fade);
        var blade1 = SignalTools.LoopHz(220, PcFansSeconds);
        var blade2 = SignalTools.LoopHz(222.4, PcFansSeconds);
        var whine1 = SignalTools.LoopHz(7400, PcFansSeconds);
        var whine2 = SignalTools.LoopHz(11250, PcFansSeconds);
        for (var i = 0; i < loop; i++)
        {
            var t = i / (double)Rate;
            var tones = 0.022 * (Math.Sin(2 * Math.PI * blade1 * t) + Math.Sin(2 * Math.PI * blade2 * t + 1)) + 0.01 * Math.Sin(2 * Math.PI * 2 * blade1 * t) +
                        0.012 * Math.Sin(2 * Math.PI * whine1 * t) + 0.006 * Math.Sin(2 * Math.PI * whine2 * t);
            result[i] = result[i] * 0.3f + (float)tones;
        }

        SignalTools.NormalizeRms(result, PcFansRms, maxPeak: 0.5f);
        return result;
    }

    /// <summary>A TV tuned to nothing, mono: hiss (300 Hz – 7 kHz) fluttering, over a 60 Hz buzz.</summary>
    public static float[] TvStatic(ulong seed)
    {
        var rng = new NeonRandom(seed);
        var loop = (int)(TvStaticSeconds * Rate);
        var fade = (int)(LoopFadeSeconds * Rate);
        var raw = new float[loop + fade];
        var noise = new NeonRandom(rng.NextULong());
        var hp = Biquad.HighPass(300, 0.7, Rate);
        var lp = Biquad.LowPass(7000, 0.7, Rate);
        var flutter = new OnePole(40f, Rate);
        for (var i = 0; i < raw.Length; i++)
        {
            var f = 0.7f + 0.3f * Math.Clamp(flutter.Process(noise.NextSigned()) * 10f, -1f, 1f);
            raw[i] = lp.Process(hp.Process(noise.NextSigned())) * f;
        }

        var result = SignalTools.FoldLoop(raw, 1, loop, fade);
        var buzz = SignalTools.LoopHz(60, TvStaticSeconds);
        var osc = new BlepOscillator(0);
        var soften = Biquad.LowPass(1800, 0.7, Rate);
        for (var i = 0; i < loop; i++) // one pass to settle the filter, so the buzz runs on across the seam
            soften.Process(osc.Pulse(buzz, Rate, 0.2));
        for (var i = 0; i < loop; i++)
            result[i] += soften.Process(osc.Pulse(buzz, Rate, 0.2)) * 0.04f;

        SignalTools.NormalizeRms(result, TvStaticRms, maxPeak: 0.6f);
        return result;
    }
}

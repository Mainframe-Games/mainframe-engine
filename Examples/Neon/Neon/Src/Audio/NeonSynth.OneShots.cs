namespace Neon;

// One-shots (mono at Rate, each normalized to its peak): the city's distant events, the apartment's foley, footsteps.
public static partial class NeonSynth
{
    /// <summary>Variations of the one-shots that have more than one.</summary>
    public const int SirenVariants = 3;

    public const int HornVariants = 4;
    public const int PaChimeVariants = 2;
    public const int KeyClickVariants = 8;

    /// <summary>Peak levels of the one-shots.</summary>
    public const float SirenPeak = 0.32f;

    public const float HornPeak = 0.28f;
    public const float PaChimePeak = 0.3f;
    public const float FlyingCarPeak = 0.5f;
    public const float KeyClickPeak = 0.3f;
    public const float DoorOpenPeak = 0.42f;
    public const float DoorClosePeak = 0.5f;
    public const float SliderPeak = 0.42f;
    public const float SwitchClickPeak = 0.35f;
    public const float LockedBuzzPeak = 0.4f;
    public const float TvBlipPeak = 0.3f;

    /// <summary>The flying car's pass is this long (the fly-by emitter crosses in the same time).</summary>
    public const float FlyingCarSeconds = 6f;

    // ── The city's events ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A distant siren, ≈ 7 s: variant 0 a slow wail (700–1350 Hz), 1 a fast yelp, 2 a hi-lo two-tone (880/660 Hz).
    /// A buzzy tone dulled by distance (two low-pass stages at 2.4 kHz), drifting down a little as it goes by, fading in
    /// and out, with the street's echoes (190, 430 and 770 ms).
    /// </summary>
    public static float[] Siren(int variant, ulong seed)
    {
        var rng = new NeonRandom(seed);
        const float seconds = 7f;
        var b = new float[(int)(seconds * Rate)];
        var osc = new BlepOscillator(rng.NextFloat());
        var sine = new BlepOscillator(rng.NextFloat());
        var pitch = rng.Range(0.94f, 1.06f);
        var period = variant switch { 0 => rng.Range(2.8f, 3.6f), 1 => rng.Range(0.28f, 0.36f), _ => rng.Range(1.1f, 1.3f) };
        var hz = 0f;
        for (var i = 0; i < b.Length; i++)
        {
            var t = i / (float)Rate;
            var phase = t / period % 1f;
            var target = variant switch
            {
                0 => 700f + 650f * (0.5f - 0.5f * MathF.Cos(2 * MathF.PI * phase)),
                1 => 750f + 750f * (phase < 0.5f ? phase * 2 : 2 - phase * 2),
                _ => phase < 0.5f ? 880f : 660f,
            };
            hz = i == 0 ? target : hz + (target - hz) * 0.004f; // glide between the two tones (≈ 5 ms)
            var drift = 1f + 0.035f * (0.5f - t / seconds); // going by: a slow Doppler fall
            var f = hz * pitch * drift;
            var env = MathF.Min(1f, t / 1.8f) * MathF.Min(1f, (seconds - t) / 2.2f);
            b[i] = (osc.Pulse(f, Rate) * 0.5f + sine.Sine(f, Rate) * 0.6f) * env * env;
        }

        LowPass(b, 2400, stages: 2);
        Echoes(b, [(0.19f, 0.35f), (0.43f, 0.22f), (0.77f, 0.12f)], 1500f);
        SignalTools.FadeEnds(b, Rate / 50);
        SignalTools.NormalizePeak(b, SirenPeak);
        return b;
    }

    /// <summary>
    /// A distant car horn, 1.6 s: variant 0 one honk, 1 a double honk, 2 a long lean on it, 3 a truck's low triple.
    /// Two pulse tones a major third apart through a nasal band-pass, dulled by distance, with echoes.
    /// </summary>
    public static float[] Horn(int variant, ulong seed)
    {
        var rng = new NeonRandom(seed);
        var b = new float[(int)(1.6f * Rate)];
        var low = variant == 3 ? rng.Range(260f, 300f) : rng.Range(380f, 460f);
        ReadOnlySpan<(float Start, float Length)> honks = variant switch
        {
            0 => [(0.02f, rng.Range(0.35f, 0.5f))],
            1 => [(0.02f, 0.16f), (0.3f, 0.34f)],
            2 => [(0.02f, rng.Range(0.8f, 1.0f))],
            _ => [(0.02f, 0.14f), (0.24f, 0.14f), (0.46f, 0.3f)],
        };
        foreach (var (start, length) in honks)
        {
            Tone(b, (int)(start * Rate), length, low, 0.015f, 0.04f, 0.5f, 0.9f, 0.4);
            Tone(b, (int)(start * Rate), length, low * 1.26f, 0.015f, 0.04f, 0.4f, 0.9f, 0.4);
        }

        var nasal = Biquad.BandPass(900, 0.7, Rate);
        for (var i = 0; i < b.Length; i++)
            b[i] = b[i] * 0.4f + nasal.Process(b[i]);
        LowPass(b, 2200, stages: 2);
        Echoes(b, [(0.21f, 0.3f), (0.47f, 0.16f)], 1400f);
        SignalTools.FadeEnds(b, Rate / 100);
        SignalTools.NormalizePeak(b, HornPeak);
        return b;
    }

    /// <summary>
    /// A public-address chime from a station or a tower, 4 s (no words): variant 0 four notes up (C–E–G–C), 1 three down
    /// (G–E–C). Bell partials through a horn speaker's band (400 Hz – 3.5 kHz) with the city's long echoes.
    /// </summary>
    public static float[] PaChime(int variant, ulong seed)
    {
        var rng = new NeonRandom(seed);
        var b = new float[4 * Rate];
        ReadOnlySpan<float> up = [523.25f, 659.25f, 783.99f, 1046.5f];
        ReadOnlySpan<float> down = [783.99f, 659.25f, 523.25f];
        var notes = variant == 0 ? up : down;
        var step = rng.Range(0.26f, 0.32f);
        for (var n = 0; n < notes.Length; n++)
            Chime(b, (int)((0.05f + n * step) * Rate), notes[n], n == notes.Length - 1 ? 0.9f : 0.5f, 0.4f);
        HighPass(b, 400);
        LowPass(b, 3500, stages: 2);
        Echoes(b, [(0.25f, 0.35f), (0.55f, 0.2f), (0.9f, 0.12f)], 2000f);
        SignalTools.FadeEnds(b, Rate / 50);
        SignalTools.NormalizePeak(b, PaChimePeak);
        return b;
    }

    /// <summary>
    /// A flying car's engines for a pass-by, <see cref="FlyingCarSeconds"/> (play it on a moving
    /// <see cref="MainframeEngine.AudioPlayer3D"/> with Doppler: the distance makes the swell, the Doppler the pitch fall).
    /// A turbine whine (1.18 and 2.36 kHz, wavering), a low roar and an airy hiss, swelling gently to the middle.
    /// </summary>
    public static float[] FlyingCar(ulong seed)
    {
        var rng = new NeonRandom(seed);
        var b = new float[(int)(FlyingCarSeconds * Rate)];
        var whine = new BlepOscillator(rng.NextFloat());
        var whine2 = new BlepOscillator(rng.NextFloat());
        var waver = new OnePole(1.5f, Rate);
        var roarLp = Biquad.LowPass(900, 0.7, Rate);
        var roarHp = Biquad.HighPass(120, 0.7, Rate);
        var hiss = Biquad.BandPass(3500, 0.8, Rate);
        var noise = new NeonRandom(rng.NextULong());
        var hz = rng.Range(1100f, 1260f);
        for (var i = 0; i < b.Length; i++)
        {
            var t = i / (float)Rate;
            var u = t / FlyingCarSeconds;
            var w = 1f + Math.Clamp(waver.Process(noise.NextSigned()) * 40f, -1f, 1f) * 0.01f;
            var white = noise.NextSigned();
            var env = MathF.Min(1f, t / 0.8f) * MathF.Min(1f, (FlyingCarSeconds - t) / 0.8f) * (0.6f + 0.4f * MathF.Sin(MathF.PI * u));
            b[i] = (whine.Sine(hz * w, Rate) * 0.14f + whine2.Sine(2 * hz * w, Rate) * 0.08f + roarLp.Process(roarHp.Process(white)) * 0.9f +
                    hiss.Process(white) * 0.35f) * env;
        }

        SignalTools.FadeEnds(b, Rate / 50);
        SignalTools.NormalizePeak(b, FlyingCarPeak);
        return b;
    }

    // ── Apartment foley ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One mechanical key, ≈ 90 ms: the switch's click, the keycap bottoming out 5–9 ms later (a plastic thock) and the
    /// quieter release.
    /// </summary>
    public static float[] KeyClick(int variant, ulong seed)
    {
        var rng = new NeonRandom(seed + (ulong)variant * 7919);
        var b = new float[(int)(0.1f * Rate)];
        var p = rng.Range(0.85f, 1.2f);
        NoiseBurst(b, 0, 0.002f, 0.0001f, 0.0005f, FilterKind.HighPass, 3500f, 0.7f, 0.5f, ref rng);
        Modes(b, 0, [1900 * p, 3100 * p, 4700 * p], [0.004f, 0.003f, 0.002f], 0.25f, ref rng);
        var bottom = (int)(rng.Range(0.005f, 0.009f) * Rate);
        Thump(b, bottom, 380 * p, 0.006f, 0.45f);
        NoiseBurst(b, bottom, 0.006f, 0.0002f, 0.0015f, FilterKind.BandPass, 1200 * p, 0.8f, 0.35f, ref rng);
        var release = (int)(rng.Range(0.045f, 0.07f) * Rate);
        NoiseBurst(b, release, 0.002f, 0.0001f, 0.0005f, FilterKind.HighPass, 3000f, 0.7f, 0.2f, ref rng);
        Modes(b, release, [2200 * p, 3600 * p], [0.003f, 0.002f], 0.1f, ref rng);
        SignalTools.FadeEnds(b, Rate / 2000);
        SignalTools.NormalizePeak(b, KeyClickPeak * rng.Range(0.85f, 1f));
        return b;
    }

    /// <summary>
    /// A door opening, 1 s: the handle and latch (a metal click), the bolt drawing back, a short hinge creak (stick-slip
    /// pulses through the door's resonances) and the air moving.
    /// </summary>
    public static float[] DoorOpen(ulong seed)
    {
        var rng = new NeonRandom(seed);
        var b = new float[Rate];
        Modes(b, 0, [1650, 2800, 4300], [0.02f, 0.012f, 0.008f], 0.5f, ref rng);
        NoiseBurst(b, 0, 0.003f, 0.0001f, 0.0008f, FilterKind.HighPass, 2500f, 0.7f, 0.6f, ref rng);
        var bolt = (int)(0.06f * Rate);
        NoiseBurst(b, bolt, 0.01f, 0.0003f, 0.003f, FilterKind.BandPass, 1800f, 0.9f, 0.4f, ref rng);
        Thump(b, bolt, 160f, 0.015f, 0.3f);
        // The creak: a rising train of tiny clicks through three narrow resonances.
        var creak = new float[b.Length];
        var start = 0.15f;
        var length = rng.Range(0.45f, 0.6f);
        var t = start;
        while (t < start + length)
        {
            var u = (t - start) / length;
            creak[(int)(t * Rate)] += MathF.Sin(MathF.PI * u) * rng.Range(0.6f, 1f);
            t += 1f / (35f + 35f * u + rng.Range(-4f, 4f));
        }

        var r1 = Biquad.BandPass(520, 8, Rate);
        var r2 = Biquad.BandPass(1100, 8, Rate);
        var r3 = Biquad.BandPass(1750, 8, Rate);
        for (var i = 0; i < b.Length; i++)
            b[i] += (r1.Process(creak[i]) * 1f + r2.Process(creak[i]) * 0.7f + r3.Process(creak[i]) * 0.4f) * 0.9f;
        Sweep(b, (int)(0.1f * Rate), 0.6f, 400f, 900f, 0.15f, 0.25f, 0.1f, ref rng);
        SignalTools.FadeEnds(b, Rate / 500);
        SignalTools.NormalizePeak(b, DoorOpenPeak);
        return b;
    }

    /// <summary>A door shutting, 0.8 s: a puff of air, the leaf's thud into the frame (wood modes), the latch catching, a rattle.</summary>
    public static float[] DoorClose(ulong seed)
    {
        var rng = new NeonRandom(seed);
        var b = new float[(int)(0.8f * Rate)];
        Sweep(b, 0, 0.12f, 700f, 300f, 0.06f, 0.03f, 0.08f, ref rng);
        var hit = (int)(0.02f * Rate);
        Thump(b, hit, 85f, 0.05f, 0.8f);
        Modes(b, hit, [170, 380, 650, 1050], [0.07f, 0.05f, 0.03f, 0.02f], 0.45f, ref rng);
        NoiseBurst(b, hit, 0.006f, 0.0003f, 0.002f, FilterKind.LowPass, 2500f, 0.7f, 0.4f, ref rng);
        var latch = hit + (int)(0.035f * Rate);
        Modes(b, latch, [2100, 3300], [0.01f, 0.007f], 0.3f, ref rng);
        NoiseBurst(b, latch, 0.003f, 0.0001f, 0.0008f, FilterKind.HighPass, 3000f, 0.7f, 0.35f, ref rng);
        Grains(b, latch, 0.08f, 8, 1500f, 1f, 0.15f, ref rng);
        SignalTools.FadeEnds(b, Rate / 500);
        SignalTools.NormalizePeak(b, DoorClosePeak);
        return b;
    }

    /// <summary>
    /// The balcony's glass door sliding, 1.1 s: rollers rumbling along the track (with ticks over its joints) and the
    /// air, then at ≈ 0.85 s (the slide's end) the rubber stop (<paramref name="closing"/> false) or the seal's thump and
    /// the latch (true), the glass rattling in its frame.
    /// </summary>
    public static float[] Slider(bool closing, ulong seed)
    {
        var rng = new NeonRandom(seed + (closing ? 1UL : 0UL));
        var b = new float[(int)(1.1f * Rate)];
        const float slide = 0.85f;
        var band = Biquad.BandPass(260, 0.8, Rate);
        var top = Biquad.LowPass(600, 0.7, Rate);
        for (var i = 0; i < (int)(slide * Rate); i++)
        {
            var u = i / (slide * Rate);
            var env = MathF.Min(1f, u / 0.15f) * (1f - 0.5f * u); // eased: quick start, slowing
            b[i] += top.Process(band.Process(rng.NextSigned())) * env * 0.6f;
        }

        Grains(b, 0, slide, 25, 1200f, 1f, 0.12f, ref rng, crowd: 1f);
        Sweep(b, 0, slide, 300f, 700f, 0.2f, 0.4f, 0.08f, ref rng);
        var end = (int)(slide * Rate);
        if (closing)
        {
            Thump(b, end, 80f, 0.05f, 0.7f);
            NoiseBurst(b, end, 0.04f, 0.0005f, 0.012f, FilterKind.LowPass, 900f, 0.7f, 0.3f, ref rng);
            Modes(b, end, [1850, 2950], [0.04f, 0.025f], 0.15f, ref rng);
            var latch = end + (int)(0.05f * Rate);
            Modes(b, latch, [2400, 3700], [0.008f, 0.006f], 0.2f, ref rng);
            NoiseBurst(b, latch, 0.003f, 0.0001f, 0.0008f, FilterKind.HighPass, 3000f, 0.7f, 0.25f, ref rng);
        }
        else
        {
            Thump(b, end, 65f, 0.04f, 0.4f);
            Modes(b, end, [1850, 2950], [0.03f, 0.02f], 0.08f, ref rng);
        }

        SignalTools.FadeEnds(b, Rate / 500);
        SignalTools.NormalizePeak(b, SliderPeak);
        return b;
    }

    /// <summary>A rocker light switch, 0.15 s: a sharp plastic click with its contact's echo 8 ms later.</summary>
    public static float[] SwitchClick(ulong seed)
    {
        var rng = new NeonRandom(seed);
        var b = new float[(int)(0.15f * Rate)];
        for (var k = 0; k < 2; k++)
        {
            var at = k * (int)(0.008f * Rate);
            var g = k == 0 ? 1f : 0.4f;
            NoiseBurst(b, at, 0.0015f, 0.0001f, 0.0004f, FilterKind.HighPass, 3000f, 0.7f, 0.5f * g, ref rng);
            Modes(b, at, [3300, 5100, 7200], [0.006f, 0.004f, 0.003f], 0.4f * g, ref rng);
            Thump(b, at, 900f, 0.004f, 0.25f * g);
        }

        SignalTools.FadeEnds(b, Rate / 2000);
        SignalTools.NormalizePeak(b, SwitchClickPeak);
        return b;
    }

    /// <summary>
    /// The front door's lock panel refusing, 1.1 s: the solenoid's clack, a harsh 118 Hz buzz (0.38 s) and the
    /// "denied" two-tone, high then low (B5, E5).
    /// </summary>
    public static float[] LockedBuzz(ulong seed)
    {
        var rng = new NeonRandom(seed);
        var b = new float[(int)(1.1f * Rate)];
        Thump(b, 0, 140f, 0.01f, 0.4f);
        NoiseBurst(b, 0, 0.006f, 0.0002f, 0.0015f, FilterKind.BandPass, 2000f, 0.9f, 0.35f, ref rng);
        var buzz = new BlepOscillator(0);
        var band = Biquad.BandPass(900, 0.8, Rate);
        var frames = (int)(0.38f * Rate);
        for (var i = 0; i < frames; i++)
        {
            var t = i / (float)Rate;
            var env = MathF.Min(1f, t / 0.005f) * MathF.Min(1f, (0.38f - t) / 0.02f);
            var x = buzz.Pulse(118, Rate, 0.3) * 0.8f;
            b[i + Rate / 100] += MathF.Tanh((band.Process(x) * 2.5f + x * 0.3f) * 1.5f) * env * 0.6f;
        }

        Tone(b, (int)(0.48f * Rate), 0.14f, 987.77f, 0.005f, 0.02f, 0.45f, 0.5f);
        Tone(b, (int)(0.68f * Rate), 0.22f, 659.25f, 0.005f, 0.03f, 0.45f, 0.5f);
        SignalTools.FadeEnds(b, Rate / 500);
        SignalTools.NormalizePeak(b, LockedBuzzPeak);
        return b;
    }

    /// <summary>The TV changing channel, 0.3 s: the relay's thunk, a 1.25 kHz blip and a burst of static.</summary>
    public static float[] TvBlip(ulong seed)
    {
        var rng = new NeonRandom(seed);
        var b = new float[(int)(0.3f * Rate)];
        Thump(b, 0, 200f, 0.008f, 0.3f);
        Tone(b, (int)(0.005f * Rate), 0.06f, 1250f, 0.002f, 0.01f, 0.5f, 0.3f);
        NoiseBurst(b, (int)(0.07f * Rate), 0.18f, 0.005f, 0.06f, FilterKind.HighPass, 400f, 0.7f, 0.5f, ref rng);
        SignalTools.FadeEnds(b, Rate / 500);
        SignalTools.NormalizePeak(b, TvBlipPeak);
        return b;
    }

    // ── Footsteps ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>The footstep surfaces with their own sounds (anything else plays <c>default</c>: concrete).</summary>
    public static readonly string[] FootstepSurfaces = ["concrete", "metal", "tile", "wood", "rug", "default"];

    /// <summary>The peak level of each surface's steps (the rug is soft, the steel grate rings loudest).</summary>
    public static float FootstepPeak(string surface) => surface switch
    {
        "rug" => 0.22f,
        "wood" => 0.4f,
        "tile" => 0.38f,
        "metal" => 0.44f,
        _ => 0.36f,
    };

    /// <summary>
    /// One footstep on <paramref name="surface"/>, 0.34 s mono (0.45 s on the ringing grate): a heel and a softer toe
    /// impact 50–90 ms apart, each built from the surface's ingredients, normalized to <see cref="FootstepPeak"/>.
    /// </summary>
    public static float[] Footstep(string surface, ulong seed)
    {
        var rng = new NeonRandom(seed);
        var length = surface == "metal" ? 0.45f : 0.34f;
        var buffer = new float[(int)(length * Rate)];
        var toe = (int)(rng.Range(0.05f, 0.09f) * Rate);
        Impact(buffer, 0, 1f, surface, ref rng);
        Impact(buffer, toe, rng.Range(0.5f, 0.75f), surface, ref rng);
        SignalTools.FadeEnds(buffer, Rate / 500);
        SignalTools.NormalizePeak(buffer, FootstepPeak(surface) * rng.Range(0.92f, 1f));
        return buffer;
    }

    private static void Impact(float[] b, int at, float gain, string surface, ref NeonRandom r)
    {
        var p = r.Range(0.9f, 1.12f); // per-impact pitch spread
        switch (surface)
        {
            case "metal": // the mezzanine's steel grate and the stairs: a clank that rings, the grate's bars rattling
                Thump(b, at, 110 * p, 0.02f, 0.4f * gain);
                NoiseBurst(b, at, 0.004f, 0.0003f, 0.0012f, FilterKind.HighPass, 2500f, 0.7f, 0.5f * gain, ref r);
                Modes(b, at, [420 * p, 870 * p, 1330 * p, 2100 * p, 3150 * p], [0.12f, 0.09f, 0.07f, 0.05f, 0.04f], 0.45f * gain, ref r);
                Grains(b, at, 0.06f, 14, 2600 * p, 2f, 0.2f * gain, ref r);
                break;
            case "tile": // hard and bright: a sharp heel click with a short glassy ring
                Thump(b, at, 130 * p, 0.02f, 0.35f * gain);
                NoiseBurst(b, at, 0.006f, 0.0002f, 0.0015f, FilterKind.BandPass, 4000 * p, 0.8f, 0.9f * gain, ref r);
                Modes(b, at, [2400 * p, 3600 * p], [0.012f, 0.008f], 0.2f * gain, ref r);
                NoiseBurst(b, at, 0.05f, 0.003f, 0.012f, FilterKind.BandPass, 2800 * p, 0.7f, 0.12f * gain, ref r);
                break;
            case "wood": // floorboards: a hollow knock (the Forest's planks)
                Thump(b, at, 110 * p, 0.06f, 0.6f * gain);
                NoiseBurst(b, at, 0.005f, 0.0005f, 0.002f, FilterKind.HighPass, 1500, 0.7f, 0.4f * gain, ref r);
                Modes(b, at, [190 * p, 430 * p, 720 * p, 1150 * p], [0.08f, 0.05f, 0.035f, 0.02f], 0.4f * gain, ref r);
                break;
            case "rug": // a padded thump and the fibres brushing
                Thump(b, at, 70 * p, 0.05f, 0.6f * gain);
                NoiseBurst(b, at, 0.12f, 0.01f, 0.04f, FilterKind.LowPass, 600 * p, 0.7f, 0.5f * gain, ref r);
                NoiseBurst(b, at, 0.1f, 0.012f, 0.03f, FilterKind.BandPass, 1800 * p, 0.6f, 0.08f * gain, ref r);
                break;
            default: // concrete, and anything unknown: a boot's slap and a little grit
                Thump(b, at, 100 * p, 0.03f, 0.28f * gain);
                NoiseBurst(b, at, 0.006f, 0.0003f, 0.002f, FilterKind.BandPass, 3000 * p, 0.8f, 0.6f * gain, ref r);
                NoiseBurst(b, at, 0.08f, 0.004f, 0.02f, FilterKind.LowPass, 2200 * p, 0.7f, 0.5f * gain, ref r);
                Grains(b, at, 0.07f, 12, 3000 * p, 0.8f, 0.12f * gain, ref r);
                break;
        }
    }
}

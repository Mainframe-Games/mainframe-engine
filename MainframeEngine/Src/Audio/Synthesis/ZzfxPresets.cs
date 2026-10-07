namespace MainframeEngine;

/// <summary>
/// Random sound recipes in the spirit of the ZzFX designer's buttons (sfxr-style): each call returns a new variation.
/// Values are rounded to three significant digits so lines stay short, and every sound stays well under
/// <see cref="Zzfx.MaxSeconds"/>.
/// </summary>
public static class ZzfxPresets
{
    /// <summary>A bright coin / pickup chime: a short tone with an upward pitch jump.</summary>
    public static ZzfxParameters Pickup(Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        return new ZzfxParameters
        {
            Shape = Pick(random, ZzfxShape.Sine, ZzfxShape.Triangle, ZzfxShape.Square),
            Frequency = Range(random, 600, 1800),
            Attack = Range(random, 0, 0.02),
            Sustain = Range(random, 0.02, 0.15),
            Release = Range(random, 0.05, 0.3),
            PitchJump = Range(random, 50, 600),
            PitchJumpTime = Range(random, 0.03, 0.12),
        };
    }

    /// <summary>Alias of <see cref="Pickup"/>.</summary>
    public static ZzfxParameters Coin(Random random) => Pickup(random);

    /// <summary>A falling zap.</summary>
    public static ZzfxParameters Laser(Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        return new ZzfxParameters
        {
            Shape = Pick(random, ZzfxShape.Saw, ZzfxShape.Square, ZzfxShape.Triangle),
            Frequency = Range(random, 300, 1500),
            Attack = Range(random, 0, 0.03),
            Sustain = Range(random, 0.02, 0.1),
            Release = Range(random, 0.1, 0.3),
            ShapeCurve = Range(random, 0.5, 2),
            Slide = -Range(random, 2, 10),
            DeltaSlide = Range(random, 0, 5),
        };
    }

    /// <summary>A noisy boom with a long release.</summary>
    public static ZzfxParameters Explosion(Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        return new ZzfxParameters
        {
            Shape = ZzfxShape.Noise,
            Frequency = Range(random, 50, 400),
            Attack = Range(random, 0, 0.02),
            Sustain = Range(random, 0, 0.3),
            Release = Range(random, 0.4, 1.2),
            ShapeCurve = Range(random, 1, 3),
            Slide = -Range(random, 0, 1),
            Noise = Range(random, 0, 1),
            BitCrush = Range(random, 0, 0.8),
            SustainVolume = Range(random, 0.3, 1),
            Decay = Range(random, 0, 0.2),
        };
    }

    /// <summary>A short impact / hurt sound.</summary>
    public static ZzfxParameters Hit(Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        return new ZzfxParameters
        {
            Shape = Pick(random, ZzfxShape.Noise, ZzfxShape.Saw, ZzfxShape.Square),
            Frequency = Range(random, 100, 600),
            Sustain = Range(random, 0, 0.05),
            Release = Range(random, 0.05, 0.2),
            Slide = -Range(random, 0, 3),
            Noise = Range(random, 0, 1),
            BitCrush = Range(random, 0, 0.3),
        };
    }

    /// <summary>A rising jump.</summary>
    public static ZzfxParameters Jump(Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        return new ZzfxParameters
        {
            Shape = Pick(random, ZzfxShape.Square, ZzfxShape.Sine, ZzfxShape.Triangle),
            Frequency = Range(random, 150, 500),
            Sustain = Range(random, 0.05, 0.15),
            Release = Range(random, 0.1, 0.25),
            ShapeCurve = Range(random, 0.5, 1.5),
            Slide = Range(random, 1, 6),
        };
    }

    /// <summary>A tiny UI blip.</summary>
    public static ZzfxParameters Blip(Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        return new ZzfxParameters
        {
            Shape = Pick(random, ZzfxShape.Sine, ZzfxShape.Square, ZzfxShape.Triangle),
            Frequency = Range(random, 400, 1500),
            Sustain = Range(random, 0, 0.03),
            Release = Range(random, 0.02, 0.08),
        };
    }

    /// <summary>A rising, repeating power-up arpeggio.</summary>
    public static ZzfxParameters PowerUp(Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        return new ZzfxParameters
        {
            Shape = Pick(random, ZzfxShape.Square, ZzfxShape.Triangle, ZzfxShape.Sine),
            Frequency = Range(random, 200, 800),
            Attack = Range(random, 0, 0.05),
            Sustain = Range(random, 0.2, 0.4),
            Release = Range(random, 0.2, 0.5),
            Slide = Range(random, 0.5, 4),
            RepeatTime = Range(random, 0.05, 0.12),
            Tremolo = Range(random, 0, 0.3),
        };
    }

    /// <summary>Any sound: every parameter random within musical ranges (under 2.5 s).</summary>
    public static ZzfxParameters Randomize(Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        return new ZzfxParameters
        {
            Shape = (ZzfxShape)random.Next(6),
            Frequency = Range(random, 50, 2000),
            Attack = Chance(random, 0.5) ? Range(random, 0, 0.2) : 0,
            Sustain = Range(random, 0, 0.5),
            Release = Range(random, 0.05, 0.8),
            ShapeCurve = Range(random, 0.2, 3),
            Slide = Chance(random, 0.5) ? Range(random, -5, 5) : 0,
            DeltaSlide = Chance(random, 0.2) ? Range(random, -2, 2) : 0,
            PitchJump = Chance(random, 0.3) ? Range(random, -500, 500) : 0,
            PitchJumpTime = Chance(random, 0.3) ? Range(random, 0, 0.3) : 0,
            RepeatTime = Chance(random, 0.2) ? Range(random, 0.03, 0.3) : 0,
            Noise = Chance(random, 0.3) ? Range(random, 0, 1) : 0,
            Modulation = Chance(random, 0.2) ? Range(random, -50, 50) : 0,
            BitCrush = Chance(random, 0.2) ? Range(random, 0, 0.5) : 0,
            Delay = Chance(random, 0.2) ? Range(random, 0, 0.3) : 0,
            SustainVolume = Range(random, 0.3, 1),
            Decay = Chance(random, 0.4) ? Range(random, 0, 0.3) : 0,
            Tremolo = Chance(random, 0.2) ? Range(random, 0, 0.5) : 0,
            Filter = Chance(random, 0.2) ? Range(random, -3000, 3000) : 0,
        };
    }

    /// <summary>
    /// A variation of <paramref name="parameters"/>: every continuous value that differs from ZzFX's default moves by up
    /// to ±10 %; the shape, the volume and the randomness stay. Returns the input when the result would be too long.
    /// </summary>
    public static ZzfxParameters Mutate(in ZzfxParameters parameters, Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        var result = parameters;
        for (var i = 0; i < ZzfxParameters.Count; i++)
        {
            if (i is 0 or 1 or 6) // volume, randomness, shape
                continue;
            var value = parameters[i];
            if (value == ZzfxParameters.DefaultAt(i) || !float.IsFinite(value))
                continue;
            result = result.With(i, Tidy(value * (1 + 0.1 * (2 * random.NextDouble() - 1))));
        }

        return Zzfx.Duration(result) <= Zzfx.MaxSeconds ? result : parameters;
    }

    private static float Range(Random random, double min, double max) => Tidy(min + random.NextDouble() * (max - min));

    private static bool Chance(Random random, double probability) => random.NextDouble() < probability;

    private static ZzfxShape Pick(Random random, params ReadOnlySpan<ZzfxShape> shapes) => shapes[random.Next(shapes.Length)];

    // Three significant digits (the designer's lines stay short).
    private static float Tidy(double value)
    {
        if (value == 0 || !double.IsFinite(value))
            return 0;
        var digits = Math.Clamp(3 - (int)Math.Ceiling(Math.Log10(Math.Abs(value))), 0, 6);
        return (float)Math.Round(value, digits);
    }
}

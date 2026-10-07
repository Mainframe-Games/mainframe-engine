using System.Globalization;
using System.Text;

namespace MainframeEngine;

/// <summary>ZzFX's wave shapes (its <c>shape</c> parameter, 0–5).</summary>
public enum ZzfxShape
{
    Sine,
    Triangle,
    Saw,

    /// <summary><c>tan(t)</c> clamped to ±1.</summary>
    Tan,

    /// <summary>ZzFX's "noise": <c>sin(t³)</c>.</summary>
    Noise,

    /// <summary>A square wave; <see cref="ZzfxParameters.ShapeCurve"/> is its duty (1 = 50 %).</summary>
    Square,
}

/// <summary>
/// The 21 values of a ZzFX sound, in ZzFX order and with ZzFX's defaults, and the <c>zzfx(...[…])</c> line format the
/// ZzFX web designer copies (<see cref="TryParse"/>, <see cref="ToLine"/>). <see cref="Zzfx.Generate"/> synthesises them.
/// </summary>
public record struct ZzfxParameters()
{
    /// <summary>Number of values in a ZzFX line.</summary>
    public const int Count = 21;

    private static readonly float[] DefaultValues = [1f, 0.05f, 220f, 0f, 0f, 0.1f, 0f, 1f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f];

    /// <summary>ZzFX's default sound (<c>zzfx()</c>).</summary>
    public static ZzfxParameters Default => new();

    public float Volume { get; init; } = 1f;

    /// <summary>ZzFX's <c>randomness</c>: per-play pitch variation (<see cref="AudioStream.PitchRandomness"/>), not synthesised.</summary>
    public float Randomness { get; init; } = 0.05f;

    /// <summary>Start frequency in Hz.</summary>
    public float Frequency { get; init; } = 220f;

    public float Attack { get; init; }

    public float Sustain { get; init; }

    public float Release { get; init; } = 0.1f;

    public ZzfxShape Shape { get; init; }

    public float ShapeCurve { get; init; } = 1f;

    public float Slide { get; init; }

    public float DeltaSlide { get; init; }

    public float PitchJump { get; init; }

    public float PitchJumpTime { get; init; }

    public float RepeatTime { get; init; }

    public float Noise { get; init; }

    public float Modulation { get; init; }

    public float BitCrush { get; init; }

    public float Delay { get; init; }

    public float SustainVolume { get; init; } = 1f;

    public float Decay { get; init; }

    public float Tremolo { get; init; }

    /// <summary>Hz; positive = low-pass, negative = high-pass, 0 = off.</summary>
    public float Filter { get; init; }

    /// <summary>ZzFX's default for the value at <paramref name="index"/> (0–20, ZzFX order).</summary>
    public static float DefaultAt(int index) => DefaultValues[index];

    /// <summary>The value at <paramref name="index"/> in ZzFX order (the shape as its number).</summary>
    public readonly float this[int index] => index switch
    {
        0 => Volume,
        1 => Randomness,
        2 => Frequency,
        3 => Attack,
        4 => Sustain,
        5 => Release,
        6 => (float)Shape,
        7 => ShapeCurve,
        8 => Slide,
        9 => DeltaSlide,
        10 => PitchJump,
        11 => PitchJumpTime,
        12 => RepeatTime,
        13 => Noise,
        14 => Modulation,
        15 => BitCrush,
        16 => Delay,
        17 => SustainVolume,
        18 => Decay,
        19 => Tremolo,
        20 => Filter,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    /// <summary>A copy with the value at <paramref name="index"/> (ZzFX order) replaced.</summary>
    public readonly ZzfxParameters With(int index, float value) => index switch
    {
        0 => this with { Volume = value },
        1 => this with { Randomness = value },
        2 => this with { Frequency = value },
        3 => this with { Attack = value },
        4 => this with { Sustain = value },
        5 => this with { Release = value },
        6 => this with { Shape = ShapeFromNumber(value) },
        7 => this with { ShapeCurve = value },
        8 => this with { Slide = value },
        9 => this with { DeltaSlide = value },
        10 => this with { PitchJump = value },
        11 => this with { PitchJumpTime = value },
        12 => this with { RepeatTime = value },
        13 => this with { Noise = value },
        14 => this with { Modulation = value },
        15 => this with { BitCrush = value },
        16 => this with { Delay = value },
        17 => this with { SustainVolume = value },
        18 => this with { Decay = value },
        19 => this with { Tremolo = value },
        20 => this with { Filter = value },
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    /// <summary>
    /// Parses a ZzFX line: <c>zzfx(...[…])</c>, <c>zzfx(…)</c>, <c>[…]</c> or a bare comma-separated list. Empty slots
    /// and missing trailing values take ZzFX's defaults; numbers are invariant culture (<c>.3</c> and <c>0.3</c>).
    /// </summary>
    public static bool TryParse(string? text, out ZzfxParameters parameters, out string? error)
    {
        parameters = Default;
        error = null;
        var body = (text ?? "").AsSpan().Trim();
        if (body.IsEmpty)
        {
            error = "The ZzFX line is empty.";
            return false;
        }

        if (body.EndsWith(';'))
            body = body[..^1].TrimEnd();
        if (body.StartsWith("zzfx", StringComparison.OrdinalIgnoreCase))
        {
            body = body[4..].TrimStart();
            if (!body.StartsWith('(') || !body.EndsWith(')'))
            {
                error = "Expected zzfx(…) with parentheses.";
                return false;
            }

            body = body[1..^1].Trim();
            if (body.StartsWith("..."))
                body = body[3..].TrimStart();
        }

        if (body.StartsWith('[') || body.EndsWith(']'))
        {
            if (!body.StartsWith('[') || !body.EndsWith(']'))
            {
                error = "Unbalanced brackets in the ZzFX line.";
                return false;
            }

            body = body[1..^1].Trim();
        }

        if (body.IsEmpty)
            return true; // zzfx() / []: the default sound

        var result = Default;
        var index = 0;
        foreach (var range in body.Split(','))
        {
            if (index >= Count)
            {
                error = $"Too many values: a ZzFX line has at most {Count}.";
                return false;
            }

            var slot = body[range].Trim();
            if (!slot.IsEmpty)
            {
                if (!double.TryParse(slot, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
                    !double.IsFinite(value) || !float.IsFinite((float)value))
                {
                    error = $"Value {index + 1} ('{slot}') is not a finite number.";
                    return false;
                }

                result = result.With(index, (float)value);
            }

            index++;
        }

        parameters = result;
        return true;
    }

    /// <summary>
    /// The shortest equivalent line, like the ZzFX designer's: defaults become empty slots, trailing defaults are
    /// dropped, numbers have no leading zero (<c>zzfx(...[,,925,.04,.3,.6,1,.3,,6.27,-184,.09,.17])</c>).
    /// </summary>
    public readonly string ToLine()
    {
        var last = -1;
        for (var i = 0; i < Count; i++)
        {
            if (!IsDefaultAt(i))
                last = i;
        }

        var builder = new StringBuilder("zzfx(...[");
        for (var i = 0; i <= last; i++)
        {
            if (i > 0)
                builder.Append(',');
            if (!IsDefaultAt(i))
                builder.Append(FormatNumber(this[i]));
        }

        return builder.Append("])").ToString();
    }

    /// <inheritdoc cref="ToLine"/>
    public override readonly string ToString() => ToLine();

    private readonly bool IsDefaultAt(int index) => this[index] == DefaultValues[index];

    internal static string FormatNumber(float value)
    {
        var text = value.ToString(CultureInfo.InvariantCulture);
        if (text.StartsWith("0.", StringComparison.Ordinal))
            return text[1..];
        if (text.StartsWith("-0.", StringComparison.Ordinal))
            return "-" + text[2..];
        return text;
    }

    // ZzFX's shape test chain (shape ? shape>1 ? shape>2 ? …): 0 → sine, ≤ 1 → triangle, …, > 4 → square.
    internal static ZzfxShape ShapeFromNumber(float value) => value switch
    {
        _ when value == 0 || float.IsNaN(value) => ZzfxShape.Sine,
        <= 1 => ZzfxShape.Triangle,
        <= 2 => ZzfxShape.Saw,
        <= 3 => ZzfxShape.Tan,
        <= 4 => ZzfxShape.Noise,
        _ => ZzfxShape.Square,
    };
}

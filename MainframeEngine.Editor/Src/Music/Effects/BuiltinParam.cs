using System.Globalization;

namespace MainframeEngine.Editor.Music;

/// <summary>How a parameter's knob maps to its value.</summary>
public enum ParamCurve
{
    Linear,

    /// <summary>Exponential (frequencies, times, ratios): equal knob travel per octave. Needs <see cref="BuiltinParam.Min"/> &gt; 0.</summary>
    Log,

    /// <summary>0 or 1 (a button).</summary>
    Toggle,

    /// <summary>An index into <see cref="BuiltinParam.Choices"/> (a menu).</summary>
    Choice,

    /// <summary>Whole numbers on a linear knob (octave, unison voices, polyphony); not a modulation target.</summary>
    Stepped,
}

/// <summary>
/// One parameter of a built-in effect. <see cref="Id"/> is the <c>.msong</c> key and, like the order of
/// <see cref="Choices"/>, a file contract: ids are never renamed and choices are only appended. The parameter's index in
/// <see cref="BuiltinEffectInfo.Params"/> is its index in the effect's value array.
/// </summary>
/// <param name="Id">camelCase key in <see cref="PluginInsert.Params"/>.</param>
/// <param name="Name">Knob label.</param>
/// <param name="Unit">
/// "Hz", "dB", "ms", "s" (seconds, shown in ms below 1 s), "%", "pct" (0–1 shown as %), ":1", "pan" (−100…100), "pan1"
/// (−1…1), "st" and "ct" (signed semitones and cents), "×", "bits", "°" or "" (formatting).
/// </param>
/// <param name="Group">Knob group in the plugin window ("Low Cut", "Band 2", …), or null.</param>
public sealed record BuiltinParam(string Id, string Name, string Unit, float Min, float Max, float Default,
    ParamCurve Curve = ParamCurve.Linear, string[]? Choices = null, string? Group = null)
{
    public static BuiltinParam Toggle(string id, string name, bool on, string? group = null) =>
        new(id, name, "", 0, 1, on ? 1 : 0, ParamCurve.Toggle, null, group);

    public static BuiltinParam Choice(string id, string name, string[] choices, int index = 0, string? group = null) =>
        new(id, name, "", 0, choices.Length - 1, index, ParamCurve.Choice, choices, group);

    /// <summary>Clamps to [<see cref="Min"/>, <see cref="Max"/>]; toggles and choices round to whole numbers; NaN reads the default.</summary>
    public float Clamp(float value)
    {
        if (!float.IsFinite(value))
            return Default;
        value = Math.Clamp(value, Min, Max);
        return Curve is ParamCurve.Toggle or ParamCurve.Choice or ParamCurve.Stepped ? MathF.Round(value) : value;
    }

    /// <summary>0..1 knob position of <paramref name="value"/>.</summary>
    public float ToNormalized(float value)
    {
        value = Clamp(value);
        if (Max <= Min)
            return 0;
        return Curve == ParamCurve.Log && Min > 0
            ? MathF.Log(value / Min) / MathF.Log(Max / Min)
            : (value - Min) / (Max - Min);
    }

    /// <summary>The value at knob position <paramref name="normalized"/> (0..1).</summary>
    public float FromNormalized(float normalized)
    {
        normalized = Math.Clamp(float.IsFinite(normalized) ? normalized : 0, 0, 1);
        var value = Curve == ParamCurve.Log && Min > 0
            ? Min * MathF.Pow(Max / Min, normalized)
            : Min + (Max - Min) * normalized;
        return Clamp(value);
    }

    /// <summary>The value as the knob shows it ("2.40 kHz", "-6.0 dB", "4.0 : 1", "1/8 dotted", "On").</summary>
    public string Format(float value)
    {
        value = Clamp(value);
        var c = CultureInfo.InvariantCulture;
        switch (Curve)
        {
            case ParamCurve.Toggle:
                return value >= 0.5f ? "On" : "Off";
            case ParamCurve.Choice:
                return Choices is { Length: > 0 } choices ? choices[Math.Clamp((int)value, 0, choices.Length - 1)] : value.ToString("0", c);
        }

        return Unit switch
        {
            "Hz" => value >= 1000 ? (value / 1000).ToString(value >= 10000 ? "0.0" : "0.00", c) + " kHz" : value.ToString(value < 20 ? "0.0" : "0", c) + " Hz",
            "dB" => value.ToString("+0.0;-0.0;0.0", c) + " dB",
            "ms" => value.ToString(value < 10 ? "0.00" : value < 100 ? "0.0" : "0", c) + " ms",
            "%" => value.ToString("0", c) + " %",
            "pct" => (value * 100).ToString("0", c) + " %",
            "s" => value < 1 ? (value * 1000).ToString(value < 0.01f ? "0.0" : "0", c) + " ms" : value.ToString("0.00", c) + " s",
            "st" => value.ToString(Curve == ParamCurve.Stepped ? "+0;-0;0" : "+0.00;-0.00;0", c) + " st",
            "ct" => value.ToString("+0;-0;0", c) + " ct",
            "pan1" => MathF.Abs(value) < 0.005f ? "C" : (MathF.Abs(value) * 100).ToString("0", c) + (value < 0 ? " L" : " R"),
            "°" => value.ToString("0", c) + "°",
            ":1" => value.ToString(value < 10 ? "0.0" : "0", c) + " : 1",
            "pan" => MathF.Abs(value) < 0.5f ? "C" : MathF.Abs(value).ToString("0", c) + (value < 0 ? " L" : " R"),
            "×" => value.ToString(value < 10 ? "0.0" : "0", c) + "×",
            "" => value.ToString(Curve == ParamCurve.Stepped ? "0" : "0.00", c),
            _ => value.ToString(value < 10 ? "0.0" : "0", c) + " " + Unit,
        };
    }
}

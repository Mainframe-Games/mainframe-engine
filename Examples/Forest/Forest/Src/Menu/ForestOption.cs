using System.Globalization;

namespace Forest;

/// <summary>How an option is shown: a slider, an Off/On switch or a row of choices.</summary>
public enum ForestOptionKind
{
    Slider,
    Toggle,
    Choice,
}

/// <summary>Where an option's value lives and where its default comes from.</summary>
public enum ForestOptionStore
{
    /// <summary>
    /// The scene or the renderer (the sun, the environment, the post profile, the render scale): the default is the
    /// value the scene and project start with (<see cref="ForestWorld.Defaults"/>), and only a changed value is saved
    /// (<see cref="ForestSettings.Graphics"/>).
    /// </summary>
    Scene,

    /// <summary>A <see cref="ForestSettings"/> field (FOV, look, audio): the default is a fresh <see cref="ForestSettings"/>'s.</summary>
    Settings,
}

/// <summary>
/// One setting of the pause menu (ADR 0180): how to read it from and write it to the running game
/// (<see cref="ForestWorld"/>), its range and how to show it. Every value is a <see cref="float"/>: switches are 0 or
/// 1, choices the index into <see cref="Choices"/>. The menu's rows, data bindings and persistence are all generated
/// from <see cref="ForestOptions.All"/>.
/// </summary>
public sealed class ForestOption
{
    /// <summary>The data-binding name and the key in <see cref="ForestSettings.Graphics"/> (lower case, underscores).</summary>
    public required string Key { get; init; }

    public required string Label { get; init; }

    /// <summary>The menu page (<see cref="PauseMenu.Pages"/>).</summary>
    public required string Page { get; init; }

    /// <summary>The heading the row sits under.</summary>
    public required string Group { get; init; }

    public required ForestOptionKind Kind { get; init; }

    public ForestOptionStore Store { get; init; } = ForestOptionStore.Scene;

    public float Min { get; init; }

    public float Max { get; init; } = 1f;

    /// <summary>Slider step (0: continuous).</summary>
    public float Step { get; init; }

    /// <summary>The choices' labels (<see cref="ForestOptionKind.Choice"/>).</summary>
    public string[] Choices { get; init; } = [];

    /// <summary>A tooltip.</summary>
    public string? Hint { get; init; }

    /// <summary>A data expression that greys the row out (e.g. <c>!ssao</c>: the intensity means nothing without SSAO).</summary>
    public string? DimWhen { get; init; }

    /// <summary>The value's readout next to a slider.</summary>
    public Func<float, string> Format { get; init; } = Number;

    /// <summary>Reads the live value.</summary>
    public required Func<ForestWorld, float> Get { get; init; }

    /// <summary>Applies a value (already clamped) to the game.</summary>
    public required Action<ForestWorld, float> Set { get; init; }

    /// <summary><paramref name="value"/> made valid: switches 0/1, choices a whole index, sliders in range and on a step.</summary>
    public float Clamp(float value)
    {
        if (!float.IsFinite(value))
            value = Min;
        switch (Kind)
        {
            case ForestOptionKind.Toggle:
                return value >= 0.5f ? 1f : 0f;
            case ForestOptionKind.Choice:
                return Math.Clamp(MathF.Round(value), 0f, Math.Max(Choices.Length - 1, 0));
            default:
                value = Math.Clamp(value, Min, Max);
                if (Step > 0f)
                    value = Math.Clamp(Min + MathF.Round((value - Min) / Step) * Step, Min, Max);
                return MathF.Round(value, 5);
        }
    }

    /// <summary>The readout for <paramref name="value"/> (sliders: <see cref="Format"/>; choices: the label; switches: On/Off).</summary>
    public string Describe(float value) => Kind switch
    {
        ForestOptionKind.Toggle => value >= 0.5f ? "On" : "Off",
        ForestOptionKind.Choice => Choices.Length > 0 ? Choices[(int)Clamp(value)] : "",
        _ => Format(value),
    };

    public override string ToString() => Key;

    // ── Readouts ─────────────────────────────────────────────────────────────────────────────────────────────

    public static string Number(float v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    public static string Whole(float v) => v.ToString("0", CultureInfo.InvariantCulture);

    public static string Percent(float v) => (v * 100f).ToString("0", CultureInfo.InvariantCulture) + " %";

    public static string Degrees(float v) => v.ToString("0", CultureInfo.InvariantCulture) + "°";

    public static string Metres(float v) => v.ToString("0", CultureInfo.InvariantCulture) + " m";

    public static string Ev(float v) => (MathF.Abs(v) < 0.05f ? "0.0" : v.ToString("+0.0;-0.0", CultureInfo.InvariantCulture)) + " EV";
}

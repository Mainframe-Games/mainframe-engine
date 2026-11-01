using System.Globalization;

namespace Neon;

/// <summary>How an option is shown: a slider, an Off/On switch, a row of choices or, for long lists, a stepper.</summary>
public enum NeonOptionKind
{
    Slider,
    Toggle,
    Choice,
    Stepper,
}

/// <summary>Where an option's value lives and so how it is saved and reset.</summary>
public enum NeonOptionStore
{
    /// <summary>
    /// The scene or the renderer: the default is the value the game starts with (<see cref="NeonWorld.Defaults"/>) and only
    /// a changed value is saved, in the settings file's <see cref="NeonWorld.Section"/>.
    /// </summary>
    Scene,

    /// <summary>An engine <see cref="MainframeEngine.UserSettings"/> value (window, volumes): it saves and resets itself.</summary>
    User,

    /// <summary>Worked out from other options (the quality preset): never saved.</summary>
    Derived,
}

/// <summary>
/// One setting of the pause menu: how to read it from and write it to the running game (<see cref="NeonWorld"/>), its range
/// and how to show it. Every value is a <see cref="float"/>: switches are 0 or 1, choices the index into
/// <see cref="Choices"/>. The menu's rows, data bindings and persistence are all generated from <see cref="NeonOptions.All"/>.
/// </summary>
public sealed class NeonOption
{
    /// <summary>The data-binding name and the key in the settings file (lower case, underscores).</summary>
    public required string Key { get; init; }

    public required string Label { get; init; }

    /// <summary>The menu page (<see cref="NeonMenu.Pages"/>).</summary>
    public required string Page { get; init; }

    /// <summary>The heading the row sits under.</summary>
    public required string Group { get; init; }

    public required NeonOptionKind Kind { get; init; }

    public NeonOptionStore Store { get; init; } = NeonOptionStore.Scene;

    public float Min { get; init; }

    public float Max { get; init; } = 1f;

    /// <summary>Slider step (0: continuous).</summary>
    public float Step { get; init; }

    /// <summary>The choices' labels (<see cref="NeonOptionKind.Choice"/>, <see cref="NeonOptionKind.Stepper"/>).</summary>
    public string[] Choices { get; init; } = [];

    /// <summary>The choices when they depend on the machine (the window sizes); overrides <see cref="Choices"/>.</summary>
    public Func<NeonWorld, string[]>? LiveChoices { get; init; }

    /// <summary>A tooltip.</summary>
    public string? Hint { get; init; }

    /// <summary>A data expression that greys the row out (e.g. <c>!ssao</c>: the strength means nothing without occlusion).</summary>
    public string? DimWhen { get; init; }

    /// <summary>The value's readout next to a slider.</summary>
    public Func<float, string> Format { get; init; } = Number;

    /// <summary>Reads the live value.</summary>
    public required Func<NeonWorld, float> Get { get; init; }

    /// <summary>Applies a value (already clamped) to the game.</summary>
    public required Action<NeonWorld, float> Set { get; init; }

    /// <summary>The choices shown in <paramref name="world"/>.</summary>
    public string[] ChoicesIn(NeonWorld world) => LiveChoices?.Invoke(world) ?? Choices;

    /// <summary><paramref name="value"/> made valid in <paramref name="world"/>.</summary>
    public float Clamp(NeonWorld world, float value)
    {
        if (!float.IsFinite(value))
            value = Min;
        switch (Kind)
        {
            case NeonOptionKind.Toggle:
                return value >= 0.5f ? 1f : 0f;
            case NeonOptionKind.Choice:
            case NeonOptionKind.Stepper:
                return Math.Clamp(MathF.Round(value), 0f, Math.Max(ChoicesIn(world).Length - 1, 0));
            default:
                value = Math.Clamp(value, Min, Max);
                if (Step > 0f)
                    value = Math.Clamp(Min + MathF.Round((value - Min) / Step) * Step, Min, Max);
                return MathF.Round(value, 5);
        }
    }

    /// <summary>The readout for <paramref name="value"/> (sliders: <see cref="Format"/>; choices: the label; switches: On/Off).</summary>
    public string Describe(NeonWorld world, float value)
    {
        switch (Kind)
        {
            case NeonOptionKind.Toggle:
                return value >= 0.5f ? "On" : "Off";
            case NeonOptionKind.Choice:
            case NeonOptionKind.Stepper:
                var choices = ChoicesIn(world);
                return choices.Length > 0 ? choices[(int)Clamp(world, value)] : "";
            default:
                return Format(value);
        }
    }

    public override string ToString() => Key;

    // ── Readouts ─────────────────────────────────────────────────────────────────────────────────────────────

    public static string Number(float v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    public static string Whole(float v) => v.ToString("0", CultureInfo.InvariantCulture);

    public static string Percent(float v) => (v * 100f).ToString("0", CultureInfo.InvariantCulture) + " %";

    public static string Degrees(float v) => v.ToString("0", CultureInfo.InvariantCulture) + "°";

    public static string Metres(float v) => v.ToString("0", CultureInfo.InvariantCulture) + " m";

    public static string Ev(float v) => (MathF.Abs(v) < 0.05f ? "0.0" : v.ToString("+0.0;-0.0", CultureInfo.InvariantCulture)) + " EV";
}

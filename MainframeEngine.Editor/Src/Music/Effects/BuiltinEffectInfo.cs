namespace MainframeEngine.Editor.Music;

/// <summary>The Browser's and the add-effect menu's groups of built-in effects.</summary>
public enum BuiltinCategory
{
    EqFilter,
    Dynamics,
    DelayReverb,
    Modulation,
    Distortion,
    Utility,
}

/// <summary>The custom graphic a plugin window draws above the knobs.</summary>
public enum BuiltinGraph
{
    None,

    /// <summary>The EQ curve with draggable band handles.</summary>
    Eq,

    /// <summary>A gain-reduction meter (<see cref="BuiltinEffect.TakeGainReductionDb"/>).</summary>
    GainReduction,

    /// <summary>The delay time in note values and milliseconds at the song's tempo.</summary>
    DelayReadout,
}

/// <summary>A named set of parameter values (missing ones read their defaults).</summary>
public sealed record BuiltinPreset(string Name, IReadOnlyDictionary<string, float> Values);

/// <summary>
/// A built-in effect type: its <see cref="Id"/> (the <c>.msong</c> <c>builtin</c> value, never renamed), how it is shown,
/// its parameters (index = DSP index) and a factory for one instance at a sample rate.
/// </summary>
public sealed record BuiltinEffectInfo(string Id, string Name, BuiltinCategory Category, string Icon, string Tooltip,
    BuiltinParam[] Params, Func<int, BuiltinEffect> Create, BuiltinGraph Graph = BuiltinGraph.None, BuiltinPreset[]? Presets = null)
{
    public BuiltinParam? Find(string id)
    {
        foreach (var p in Params)
        {
            if (string.Equals(p.Id, id, StringComparison.Ordinal))
                return p;
        }

        return null;
    }

    public int IndexOf(string id) => Array.FindIndex(Params, p => string.Equals(p.Id, id, StringComparison.Ordinal));

    /// <summary>Every parameter at its default (a new insert's <see cref="PluginInsert.Params"/>).</summary>
    public Dictionary<string, float> Defaults()
    {
        var values = new Dictionary<string, float>(Params.Length, StringComparer.Ordinal);
        foreach (var p in Params)
            values[p.Id] = p.Default;
        return values;
    }

    /// <summary>The value of <paramref name="id"/> in <paramref name="values"/> (clamped), or its default.</summary>
    public float Value(IReadOnlyDictionary<string, float>? values, string id)
    {
        var p = Find(id) ?? throw new ArgumentException($"'{Name}' has no parameter '{id}'.", nameof(id));
        return values is not null && values.TryGetValue(id, out var v) ? p.Clamp(v) : p.Default;
    }
}

/// <summary>
/// The built-in effects, by category. Each effect class holds its own <c>Info</c> (static, so it exists before the
/// registry reads it); each category lists its effects in its own partial file.
/// </summary>
public static partial class BuiltinEffects
{
    private static BuiltinEffectInfo[]? _all;

    /// <summary>Every built-in effect in Browser order (by category, then as each category lists them).</summary>
    public static IReadOnlyList<BuiltinEffectInfo> All => _all ??=
    [
        .. EqFilterEffects(), .. DynamicsEffects(), .. DelayReverbEffects(), .. ModulationEffects(), .. DistortionEffects(),
        .. UtilityEffects(),
    ];

    public static BuiltinEffectInfo? Find(string id)
    {
        foreach (var info in All)
        {
            if (string.Equals(info.Id, id, StringComparison.Ordinal))
                return info;
        }

        return null;
    }

    public static IEnumerable<BuiltinEffectInfo> InCategory(BuiltinCategory category) => All.Where(i => i.Category == category);

    public static string CategoryName(BuiltinCategory category) => category switch
    {
        BuiltinCategory.EqFilter => "EQ & Filter",
        BuiltinCategory.Dynamics => "Dynamics",
        BuiltinCategory.DelayReverb => "Delay & Reverb",
        BuiltinCategory.Modulation => "Modulation",
        BuiltinCategory.Distortion => "Distortion",
        BuiltinCategory.Utility => "Utility",
        _ => category.ToString(),
    };
}

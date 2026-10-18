using System.Text.Json;
using System.Text.Json.Serialization;

namespace MainframeEngine.Editor.Music;

/// <summary>
/// The wavetable synth's patch, stored in its track's <see cref="InstrumentDescriptor.Patch"/>: parameter values (as
/// built-in effects store theirs: missing ids read defaults, out-of-range values clamp, unknown ids are kept), each
/// oscillator's wavetable, drawn LFO shapes, macro names, the mod matrix and the FX rack. Unknown fields are kept.
/// </summary>
public sealed class SynthPatch
{
    /// <summary>The patch format this editor writes (a newer one plays what is understood).</summary>
    public const int CurrentVersion = 1;

    /// <summary>The most mod routes a patch plays (more are kept in the file, not played).</summary>
    public const int MaxRoutes = 32;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>Parameter values by <see cref="SynthParams"/> id.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, float>? Params { get; set; }

    /// <summary>Each oscillator's table (<c>a</c>, <c>b</c>): a factory table or a project <c>.wav</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, SynthTableRef>? Tables { get; set; }

    /// <summary>LFO 1–4's drawn shapes (null: that LFO uses a built-in shape).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<SynthLfoShape?>? LfoShapes { get; set; }

    /// <summary>Macro 1–4's names.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Macros { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<SynthModRoute>? Matrix { get; set; }

    /// <summary>The synth's own effect chain (built-in effects only), after the voices and before the master volume.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<PluginInsert>? Fx { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>The Init patch: oscillator A on a saw, through filter 1 (an open 24 dB low-pass), no routes, no effects.</summary>
    public static SynthPatch Init() => new()
    {
        Params = new Dictionary<string, float>(StringComparer.Ordinal),
        Tables = new Dictionary<string, SynthTableRef>(StringComparer.Ordinal)
        {
            ["a"] = new() { Source = FactoryWavetables.DefaultSource },
            ["b"] = new() { Source = FactoryWavetables.DefaultSource },
        },
    };

    /// <summary>A deep copy (undo keeps whole copies of the parts it replaces).</summary>
    public SynthPatch Clone() => new()
    {
        Version = Version,
        Params = Params is null ? null : new Dictionary<string, float>(Params, StringComparer.Ordinal),
        Tables = Tables?.ToDictionary(static p => p.Key, static p => p.Value.Clone(), StringComparer.Ordinal),
        LfoShapes = LfoShapes?.Select(static s => s?.Clone()).ToList(),
        Macros = Macros is null ? null : [.. Macros],
        Matrix = Matrix?.Select(static r => r.Clone()).ToList(),
        Fx = Fx?.Select(static i => i.Clone()).ToList(),
        Extra = Extra,
    };
}

/// <summary>An oscillator's wavetable: <c>factory:&lt;set&gt;/&lt;name&gt;</c> or a project <c>.wav</c> (asset UID or path).</summary>
public sealed class SynthTableRef
{
    public string Source { get; set; } = FactoryWavetables.DefaultSource;

    /// <summary>A <c>.wav</c> without a <c>clm </c> chunk: its frame size (0: guess).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int FrameSize { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    public SynthTableRef Clone() => new() { Source = Source, FrameSize = FrameSize, Extra = Extra };
}

/// <summary>A drawn LFO shape: points <c>[x, y, curve]</c> (x, y in 0–1; curve −1…1 bends the segment ending at the point).</summary>
public sealed class SynthLfoShape
{
    public List<float[]> Points { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    public SynthLfoShape Clone() => new() { Points = [.. Points.Select(static p => (float[])p.Clone())], Extra = Extra };
}

/// <summary>
/// One mod-matrix route: <see cref="Source"/> (<c>env1</c>…, <c>lfo1</c>…, <c>velocity</c>, <c>note</c>, <c>random</c>,
/// <c>macro1</c>…, <c>modwheel</c>, <c>aftertouch</c>, <c>pitchbend</c>) moves <see cref="Target"/> (a continuous parameter
/// id) by <see cref="Amount"/> of its normalized range, optionally scaled by an <see cref="Aux"/> source.
/// </summary>
public sealed class SynthModRoute
{
    public string Source { get; set; } = "";

    public string Target { get; set; } = "";

    /// <summary>−1…1 of the target's normalized (knob) range.</summary>
    public float Amount { get; set; }

    /// <summary>Maps the source's 0…1 to −1…1.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Bipolar { get; set; }

    /// <summary>A second source that scales the route (0…1), or null.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Aux { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    public SynthModRoute Clone() => new() { Source = Source, Target = Target, Amount = Amount, Bipolar = Bipolar, Aux = Aux, Extra = Extra };
}

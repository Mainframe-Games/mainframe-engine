using System.Globalization;

namespace MainframeEngine.Audio.Music;

/// <summary>A compiled section: everything by index and in frames.</summary>
public sealed class MusicPlanSection
{
    internal MusicPlanSection(string id, string name, MusicSectionKind kind, int bars, long lengthFrames)
    {
        Id = id;
        Name = name;
        Kind = kind;
        Bars = bars;
        LengthFrames = lengthFrames;
    }

    public string Id { get; }

    public string Name { get; }

    public MusicSectionKind Kind { get; }

    public int Bars { get; }

    public long LengthFrames { get; }

    /// <summary>What follows the section's end: its own index (it loops), another section, or −1 (the music ends).</summary>
    public int Then { get; internal set; } = -1;

    public bool Loops => Then >= 0 && Then == Index;

    public int Index { get; internal set; }

    /// <summary>Stingers: where they land.</summary>
    public MusicQuantize Quantize { get; internal set; } = MusicQuantize.Beat;

    /// <summary>Stingers: beats that play before the quantum.</summary>
    public float PickupBeats { get; internal set; }

    /// <summary>Marker frames from the section's start, sorted, with their names and exit flags.</summary>
    public long[] MarkerFrames { get; internal set; } = [];

    public string[] MarkerNames { get; internal set; } = [];

    public bool[] MarkerExit { get; internal set; } = [];

    /// <summary>The exit markers' frames, sorted (for <see cref="MusicQuantize.ExitMarker"/>).</summary>
    public long[] ExitFrames { get; internal set; } = [];
}

/// <summary>A compiled comparison: parameter index, operator and value (labels resolved to their index).</summary>
public readonly record struct MusicPlanComparison(int Parameter, MusicCompare Op, float Value);

/// <summary>A compiled transition rule.</summary>
public sealed class MusicRule
{
    internal MusicRule(int index, int to) => (Index, To) = (index, to);

    /// <summary>Its position in the transition list (lower wins).</summary>
    public int Index { get; }

    public int To { get; }

    /// <summary>Source sections (ignored when <see cref="AnySource"/>).</summary>
    public int[] From { get; internal set; } = [];

    public bool AnySource { get; internal set; }

    /// <summary>False when the rule has no condition: it never fires by itself.</summary>
    public bool Automatic { get; internal set; }

    public MusicPlanComparison[] All { get; internal set; } = [];

    public MusicPlanComparison[] Any { get; internal set; } = [];

    public MusicQuantize Quantize { get; internal set; }

    public int Every { get; internal set; } = 1;

    public MusicEntry Entry { get; internal set; }

    /// <summary>Null: the default (0 at a section end or exit marker, else ½ beat).</summary>
    public float? FadeOutBeats { get; internal set; }

    public float FadeInBeats { get; internal set; }

    /// <summary>A transition section played once in between, or −1.</summary>
    public int Segment { get; internal set; } = -1;

    /// <summary>A stinger fired at the boundary, or −1.</summary>
    public int Stinger { get; internal set; } = -1;

    /// <summary>The rule applies when the music is in <paramref name="section"/>.</summary>
    public bool AppliesFrom(int section) => AnySource || Array.IndexOf(From, section) >= 0;

    /// <summary>The condition holds for the parameters' current values.</summary>
    public bool Matches(MusicParameterSet parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (!Automatic)
            return false;
        foreach (var c in All)
        {
            if (!Holds(c, parameters))
                return false;
        }

        if (Any.Length == 0)
            return true;
        foreach (var c in Any)
        {
            if (Holds(c, parameters))
                return true;
        }

        return false;
    }

    private static bool Holds(in MusicPlanComparison c, MusicParameterSet parameters)
    {
        const float Epsilon = 1e-6f;
        var v = parameters.GetValue(c.Parameter);
        return c.Op switch
        {
            MusicCompare.Equal => MathF.Abs(v - c.Value) <= Epsilon,
            MusicCompare.NotEqual => MathF.Abs(v - c.Value) > Epsilon,
            MusicCompare.Less => v < c.Value - Epsilon,
            MusicCompare.LessOrEqual => v <= c.Value + Epsilon,
            MusicCompare.Greater => v > c.Value + Epsilon,
            _ => v >= c.Value - Epsilon,
        };
    }
}

/// <summary>
/// A <see cref="MusicManifest"/> compiled for playback: sections, layers, curves and rules by index, the grid, and the
/// problems found (unknown ids, bad values). Immutable and shared by every instance playing it; per-instance state
/// lives in <see cref="MusicDirector"/>. Compiling never throws for bad data: what cannot be used is left out and reported.
/// </summary>
public sealed class MusicPlan
{
    /// <summary>The most layers a piece of music may have.</summary>
    public const int MaxLayers = 8;

    /// <summary>The <c>from</c> entry that matches every section.</summary>
    public const string AnySection = "*";

    private readonly Dictionary<string, int> _sections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _layers = new(StringComparer.Ordinal);
    private readonly List<string> _problems = [];

    private MusicPlan(MusicManifest manifest)
    {
        var problems = _problems;
        var rate = manifest.SampleRate > 0 ? manifest.SampleRate : 48000;
        var tempo = manifest.Tempo > 0 && double.IsFinite(manifest.Tempo) ? manifest.Tempo : 120;
        var signature = manifest.TimeSignature is { Length: 2 } ts && ts[0] > 0 && ts[1] > 0 ? ts : [4, 4];
        Grid = new MusicGrid(rate, tempo, signature[0], signature[1]);

        Parameters = [.. manifest.Parameters.Where(p => p is not null)];
        var parameterSet = new MusicParameterSet(Parameters);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in Parameters)
        {
            if (string.IsNullOrEmpty(p.Name))
                problems.Add("A parameter has no name; it is ignored.");
            else if (!seen.Add(p.Name))
                problems.Add($"Parameter '{p.Name}' is defined twice; the first is used.");
        }

        Layers = CompileLayers(manifest, problems);
        Sections = CompileSections(manifest, Grid, problems);
        StartSection = CompileStart(manifest, problems);
        Curves = CompileCurves(manifest, parameterSet, problems);
        Rules = CompileRules(manifest, parameterSet, problems);
    }

    public MusicGrid Grid { get; }

    public IReadOnlyList<MusicParameterInfo> Parameters { get; }

    public MusicManifestLayer[] Layers { get; }

    public MusicPlanSection[] Sections { get; }

    public MusicCurve[] Curves { get; }

    public MusicRule[] Rules { get; }

    /// <summary>The section <c>Play</c> starts at, or −1 when there is no playable section.</summary>
    public int StartSection { get; }

    /// <summary>What could not be used, for logs and the editor's problem list.</summary>
    public IReadOnlyList<string> Problems => _problems;

    public static MusicPlan Compile(MusicManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return new MusicPlan(manifest);
    }

    /// <summary>A section's index by id, or −1.</summary>
    public int FindSection(string id) => id is not null && _sections.TryGetValue(id, out var i) ? i : -1;

    /// <summary>A layer's index by id, or −1.</summary>
    public int FindLayer(string id) => id is not null && _layers.TryGetValue(id, out var i) ? i : -1;

    /// <summary>A new parameter set at the defaults (one per playing instance).</summary>
    public MusicParameterSet CreateParameters() => new(Parameters);

    /// <summary>
    /// The first rule (in priority order) that applies from <paramref name="section"/>, leads somewhere else and whose
    /// condition holds, or null.
    /// </summary>
    public MusicRule? FirstMatchingRule(int section, MusicParameterSet parameters)
    {
        foreach (var rule in Rules)
        {
            if (rule.Automatic && (rule.To != section || rule.Segment >= 0) && rule.AppliesFrom(section) && rule.Matches(parameters))
                return rule;
        }

        return null;
    }

    /// <summary>
    /// Evaluates every layer's gain (dB; curves on one layer add, i.e. their gains multiply) and filter cutoff (Hz; a
    /// low-pass takes the lowest of its curves, a high-pass the highest) for the parameters' current values.
    /// </summary>
    public void EvaluateLayers(MusicParameterSet parameters, Span<float> gainDb, Span<float> cutoffHz)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        for (var i = 0; i < Layers.Length && i < gainDb.Length; i++)
        {
            gainDb[i] = 0;
            if (i < cutoffHz.Length)
                cutoffHz[i] = Layers[i].Filter == MusicLayerFilter.HighPass ? MusicCurve.MinCutoffHz : MusicCurve.MaxCutoffHz;
        }

        foreach (var curve in Curves)
        {
            var value = curve.Evaluate(parameters.GetValue(curve.Parameter));
            var layer = curve.Layer;
            if (curve.Target == MusicCurveTarget.Volume)
            {
                if (layer < gainDb.Length)
                    gainDb[layer] = Math.Max(AudioMath.SilenceDb, gainDb[layer] + value);
            }
            else if (layer < cutoffHz.Length)
            {
                cutoffHz[layer] = Layers[layer].Filter == MusicLayerFilter.HighPass ? Math.Max(cutoffHz[layer], value) : Math.Min(cutoffHz[layer], value);
            }
        }
    }

    private MusicManifestLayer[] CompileLayers(MusicManifest manifest, List<string> problems)
    {
        var layers = new List<MusicManifestLayer>();
        foreach (var layer in manifest.Layers)
        {
            if (layer is null || string.IsNullOrEmpty(layer.Id))
            {
                problems.Add("A layer has no id; it is ignored.");
                continue;
            }

            if (layers.Count == MaxLayers)
            {
                problems.Add($"Layer '{layer.Id}' is over the limit of {MaxLayers} layers; it is ignored.");
                continue;
            }

            if (!_layers.TryAdd(layer.Id, layers.Count))
            {
                problems.Add($"Layer '{layer.Id}' is defined twice; the first is used.");
                continue;
            }

            layers.Add(layer);
        }

        return [.. layers];
    }

    private MusicPlanSection[] CompileSections(MusicManifest manifest, MusicGrid grid, List<string> problems)
    {
        var sections = new List<MusicPlanSection>();
        var sources = new List<MusicManifestSection>();
        foreach (var s in manifest.Sections)
        {
            if (s is null || string.IsNullOrEmpty(s.Id))
            {
                problems.Add("A section has no id; it is ignored.");
                continue;
            }

            if (s.Bars < 1)
            {
                problems.Add($"Section '{s.Id}' has no bars; it is ignored.");
                continue;
            }

            if (!_sections.TryAdd(s.Id, sections.Count))
            {
                problems.Add($"Section '{s.Id}' is defined twice; the first is used.");
                continue;
            }

            var length = grid.LengthFrames(s.Bars);
            if (s.LengthFrames != 0 && s.LengthFrames != length)
                problems.Add($"Section '{s.Id}': {s.LengthFrames} frames does not match {s.Bars} bars ({length}); the grid's length is used.");
            var section = new MusicPlanSection(s.Id, string.IsNullOrEmpty(s.Name) ? s.Id : s.Name, s.Kind, s.Bars, length) { Index = sections.Count };
            section.PickupBeats = float.IsFinite(s.PickupBeats) ? Math.Max(0, s.PickupBeats) : 0;
            section.Quantize = s.Quantize is MusicQuantize.SectionEnd or MusicQuantize.ExitMarker or null ? MusicQuantize.Beat : s.Quantize.Value;
            var markers = (s.Markers ?? []).Where(m => m is not null).OrderBy(m => m.Frame).ToArray();
            section.MarkerFrames = [.. markers.Select(m => Math.Clamp(m.Frame, 0, length))];
            section.MarkerNames = [.. markers.Select(m => m.Name ?? "")];
            section.MarkerExit = [.. markers.Select(m => m.Exit)];
            section.ExitFrames = [.. markers.Where(m => m.Exit).Select(m => Math.Clamp(m.Frame, 0, length))];
            sections.Add(section);
            sources.Add(s);
        }

        // Continuations once every id is known.
        for (var i = 0; i < sections.Count; i++)
        {
            var s = sources[i];
            if (sections[i].Kind == MusicSectionKind.Stinger)
                continue;
            if (s.Loop)
            {
                sections[i].Then = i;
            }
            else if (s.Then is { Length: > 0 } then)
            {
                var target = FindSection(then);
                if (target < 0 || sections[target].Kind == MusicSectionKind.Stinger)
                    problems.Add($"Section '{s.Id}' continues to '{then}', which is not a playable section; the music ends there instead.");
                else
                    sections[i].Then = target;
            }
        }

        return [.. sections];
    }

    private int CompileStart(MusicManifest manifest, List<string> problems)
    {
        var start = manifest.Start is { Length: > 0 } id ? FindSection(id) : -1;
        if (start >= 0 && Sections[start].Kind == MusicSectionKind.Section)
            return start;
        var first = Array.FindIndex(Sections, s => s.Kind == MusicSectionKind.Section);
        if (first < 0)
            problems.Add("There is no playable section.");
        else if (manifest.Start is { Length: > 0 })
            problems.Add($"The start section '{manifest.Start}' is not a playable section; '{Sections[first].Id}' is used.");
        return first;
    }

    private MusicCurve[] CompileCurves(MusicManifest manifest, MusicParameterSet parameters, List<string> problems)
    {
        var curves = new List<MusicCurve>();
        foreach (var c in manifest.Curves)
        {
            if (c is null)
                continue;
            var layer = FindLayer(c.Layer);
            var parameter = parameters.IndexOf(c.Parameter);
            if (layer < 0)
                problems.Add($"A curve names layer '{c.Layer}', which does not exist; it is ignored.");
            else if (parameter < 0)
                problems.Add($"A curve on layer '{c.Layer}' names parameter '{c.Parameter}', which does not exist; it is ignored.");
            else if (c.Points is not { Count: > 0 })
                problems.Add($"A curve on layer '{c.Layer}' has no points; it is ignored.");
            else if (c.Target == MusicCurveTarget.Cutoff && Layers[layer].Filter == MusicLayerFilter.None)
                problems.Add($"A cutoff curve on layer '{c.Layer}', which has no filter; it is ignored.");
            else
                curves.Add(new MusicCurve(layer, c.Target, parameter, c.Shape, parameters.KindOf(parameter) != MusicParameterKind.Continuous,
                    c.Points.Where(p => float.IsFinite(p.X) && float.IsFinite(p.Y)).ToArray()));
        }

        return [.. curves];
    }

    private MusicRule[] CompileRules(MusicManifest manifest, MusicParameterSet parameters, List<string> problems)
    {
        var rules = new List<MusicRule>();
        for (var i = 0; i < manifest.Transitions.Count; i++)
        {
            var t = manifest.Transitions[i];
            if (t is null)
                continue;
            var label = $"Transition {(i + 1).ToString(CultureInfo.InvariantCulture)}";
            var to = FindSection(t.To);
            if (to < 0 || Sections[to].Kind != MusicSectionKind.Section)
            {
                problems.Add($"{label} goes to '{t.To}', which is not a playable section; it is ignored.");
                continue;
            }

            var rule = new MusicRule(i, to)
            {
                Quantize = t.Quantize,
                Every = Math.Max(1, t.Every ?? 1),
                Entry = t.Entry,
                FadeOutBeats = t.FadeOutBeats is { } fo && float.IsFinite(fo) ? Math.Max(0, fo) : null,
                FadeInBeats = t.FadeInBeats is { } fi && float.IsFinite(fi) ? Math.Max(0, fi) : 0,
            };

            var from = new List<int>();
            foreach (var id in t.From ?? [])
            {
                if (id == AnySection)
                    rule.AnySource = true;
                else if (FindSection(id) is var s and >= 0)
                    from.Add(s);
                else
                    problems.Add($"{label} comes from '{id}', which does not exist; that source is ignored.");
            }

            rule.From = [.. from];
            if (!rule.AnySource && from.Count == 0)
            {
                problems.Add($"{label} has no source section; it is ignored.");
                continue;
            }

            if (t.Segment is { Length: > 0 } segment)
            {
                rule.Segment = FindSection(segment);
                if (rule.Segment < 0 || Sections[rule.Segment].Kind != MusicSectionKind.Transition)
                {
                    problems.Add($"{label}: '{segment}' is not a transition section; the segment is ignored.");
                    rule.Segment = -1;
                }
            }

            if (t.Stinger is { Length: > 0 } stinger)
            {
                rule.Stinger = FindSection(stinger);
                if (rule.Stinger < 0 || Sections[rule.Stinger].Kind != MusicSectionKind.Stinger)
                {
                    problems.Add($"{label}: '{stinger}' is not a stinger; it is ignored.");
                    rule.Stinger = -1;
                }
            }

            if (t.When is { } when)
            {
                if (!TryCompile(when.All, parameters, label, problems, out var all) || !TryCompile(when.Any, parameters, label, problems, out var any))
                {
                    problems.Add($"{label} never fires (its condition cannot be evaluated); GoToSection still works.");
                }
                else
                {
                    rule.All = all;
                    rule.Any = any;
                    rule.Automatic = true;
                }
            }

            rules.Add(rule);
        }

        return [.. rules];
    }

    private static bool TryCompile(List<MusicComparison>? list, MusicParameterSet parameters, string label, List<string> problems, out MusicPlanComparison[] compiled)
    {
        compiled = [];
        if (list is null)
            return true;
        var result = new MusicPlanComparison[list.Count];
        for (var i = 0; i < list.Count; i++)
        {
            var c = list[i];
            var p = c is null ? -1 : parameters.IndexOf(c.Parameter);
            if (p < 0)
            {
                problems.Add($"{label} tests parameter '{c?.Parameter}', which does not exist.");
                return false;
            }

            float value;
            if (c!.Value.Label is { } text)
            {
                value = parameters.LabelIndex(p, text);
                if (value < 0)
                {
                    problems.Add($"{label} tests '{c.Parameter}' against '{text}', which is not one of its labels.");
                    return false;
                }
            }
            else
            {
                value = c.Value.Number;
            }

            result[i] = new MusicPlanComparison(p, c.Op, value);
        }

        compiled = result;
        return true;
    }
}

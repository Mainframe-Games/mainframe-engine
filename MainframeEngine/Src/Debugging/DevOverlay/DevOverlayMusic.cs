using MainframeEngine.Audio.Music;
using MainframeEngine.UI.Rml;

namespace MainframeEngine;

/// <summary>
/// The dev overlay's Music panel (adaptive music, docs/design/adaptive-music.md): added at the first refresh that finds
/// an <see cref="AdaptiveMusicInstance"/> playing, then kept. For each playing instance it shows the music, the section
/// and bar.beat, the scheduled transition with its countdown and a gain bar per layer, with a control per parameter (a
/// slider, a stepper or its labels), Fire per stinger and Next (the first transition that matches now, else the first
/// from this section, quantized like its rule). Every write goes through the instance (<c>SetParameter</c>,
/// <c>TriggerStinger</c>, <c>GoToSection</c>), so it behaves as game code does. Rows are rebuilt only when the playing
/// instances change; a refresh only copies numbers and the manifest's own strings, so it allocates nothing.
/// </summary>
internal static class DevOverlayMusic
{
    public const string PanelId = "music";

    private const string Rml = """
        <div data-if="instances.size == 0" class="dev-row"><span class="v">No adaptive music playing</span></div>
        <div data-for="m, mi : instances">
          <div class="dev-heading">{{m.music}}</div>
          <div class="dev-row"><span>Section</span><span class="v">{{m.section}} · {{m.bar}}.{{m.beat}}</span></div>
          <div class="dev-row" data-if="m.pending != ''"><span>Next</span><span class="v">{{m.pending}} in {{m.pendingSeconds | format(1)}} s</span></div>
          <div class="dev-bus" data-for="l : m.layers">
            <span class="dev-bus-name">{{l.name}}</span>
            <div class="dev-meter-track"><div class="dev-meter" data-style-width="(l.meter * 100) + '%'"></div></div>
            <span class="v">{{l.db | format(0)}} dB</span>
          </div>
          <div data-for="p, pi : m.params">
            <div class="dev-row"><span>{{p.name}}</span><span class="v" data-if="p.kind != 2">{{p.value | format(2)}}</span><span class="v" data-if="p.kind == 2">{{p.label}}</span></div>
            <div class="dev-row" data-if="p.kind == 0"><input class="dev-slider" type="range" data-attr-min="p.min" data-attr-max="p.max" data-attr-step="p.step" data-value="p.target"/></div>
            <div class="dev-row" data-if="p.kind == 1"><button class="dev-button" data-event-click="step(p.owner, p.param, -1)">-</button><button class="dev-button" data-event-click="step(p.owner, p.param, 1)">+</button></div>
            <div class="dev-row" data-if="p.kind == 2"><span data-for="t : m.labels"><button class="dev-button" data-if="t.param == pi" data-class-active="t.index == p.index" data-event-click="label(t.owner, t.param, t.index)">{{t.text}}</button></span></div>
          </div>
          <div class="dev-row">
            <button class="dev-button" data-for="s : m.stingers" data-event-click="fire(s.owner, s.index)">Fire {{s.name}}</button>
            <button class="dev-button" data-event-click="next(mi)">Next</button>
          </div>
        </div>
        """;

    private static readonly RmlStructType<MusicLabelRow> LabelType = new RmlStructType<MusicLabelRow>()
        .Member("owner", static t => t.Owner)
        .Member("param", static t => t.Parameter)
        .Member("index", static t => t.Index)
        .Member("text", static t => t.Text);

    private static readonly RmlStructType<MusicParameterRow> ParameterType = new RmlStructType<MusicParameterRow>()
        .Member("owner", static p => p.Owner)
        .Member("param", static p => p.Param)
        .Member("name", static p => p.Name)
        .Member("kind", static p => (int)p.Kind)
        .Member("min", static p => p.Min)
        .Member("max", static p => p.Max)
        .Member("step", static p => p.Step)
        .Member("value", static p => p.Value)
        .Member("target", static p => p.Target, static (p, v) => p.Set(v))
        .Member("index", static p => p.LabelIndex)
        .Member("label", static p => p.Label);

    private static readonly RmlStructType<MusicLayerRow> LayerType = new RmlStructType<MusicLayerRow>()
        .Member("name", static l => l.Name)
        .Member("db", static l => l.GainDb)
        .Member("meter", static l => Math.Clamp((l.GainDb + 60f) / 60f, 0f, 1f));

    private static readonly RmlStructType<MusicStingerRow> StingerType = new RmlStructType<MusicStingerRow>()
        .Member("owner", static s => s.Owner)
        .Member("index", static s => s.Index)
        .Member("name", static s => s.Name);

    private static readonly RmlStructType<MusicInstanceRow> InstanceType = new RmlStructType<MusicInstanceRow>()
        .Member("music", static m => m.Music)
        .Member("section", static m => m.Section)
        .Member("bar", static m => m.Bar)
        .Member("beat", static m => m.Beat)
        .Member("pending", static m => m.Pending)
        .Member("pendingSeconds", static m => m.PendingSeconds)
        .List("layers", static m => m.Layers, LayerType)
        .List("params", static m => m.Parameters, ParameterType)
        .List("labels", static m => m.Labels, LabelType) // flat (one level of nesting): each label names its parameter
        .List("stingers", static m => m.Stingers, StingerType);

    /// <summary>Adds the panel once music plays (at a refresh, so only while the overlay shows).</summary>
    public static void Watch(DevOverlay overlay, DevOverlaySources sources)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        var stats = new MusicStats(sources);
        DevOverlayPanel? panel = null;
        overlay.Refreshed += () =>
        {
            stats.Refresh();
            if (panel is null)
            {
                if (stats.Instances.Count == 0)
                    return;
                panel = overlay.AddPanel(PanelId, "Music", Rml, p => p.Model!
                    .BindList("instances", stats.Instances, InstanceType)
                    .Event("fire", e => stats.Fire(Int(e, 0), Int(e, 1)))
                    .Event("next", e => stats.Next(Int(e, 0)))
                    .Event("step", e => stats.Step(Int(e, 0), Int(e, 1), Int(e, 2)))
                    .Event("label", e => stats.Label(Int(e, 0), Int(e, 1), Int(e, 2))));
            }

            panel.Model?.DirtyAll();
        };
    }

    private static int Int(RmlDataEvent e, int index) => index < e.ArgumentCount ? (int)e.GetArgument(index).GetDouble() : -1;
}

/// <summary>The Music panel's rows: one per playing instance, rebuilt when the set changes, refreshed in place.</summary>
internal sealed class MusicStats(DevOverlaySources sources)
{
    private readonly List<AdaptiveMusicInstance> _shown = [];
    private readonly List<MusicDirector?> _shownDirectors = [];

    public List<MusicInstanceRow> Instances { get; } = [];

    public void Refresh()
    {
        var playing = sources.Servers.Get<AudioServer>()?.MusicInstances ?? [];
        if (!Same(playing))
            Rebuild(playing);
        foreach (var row in Instances)
            row.Refresh();
    }

    private bool Same(IReadOnlyList<AdaptiveMusicInstance> playing)
    {
        if (playing.Count != _shown.Count)
            return false;
        for (var i = 0; i < playing.Count; i++)
        {
            if (!ReferenceEquals(playing[i], _shown[i]) || !ReferenceEquals(playing[i].Director, _shownDirectors[i]))
                return false;
        }

        return true;
    }

    private void Rebuild(IReadOnlyList<AdaptiveMusicInstance> playing)
    {
        _shown.Clear();
        _shownDirectors.Clear();
        Instances.Clear();
        foreach (var instance in playing)
        {
            _shown.Add(instance);
            _shownDirectors.Add(instance.Director);
            if (instance.Director is { } director)
                Instances.Add(new MusicInstanceRow(instance, director, Instances.Count));
        }
    }

    private MusicInstanceRow? Row(int index) => (uint)index < (uint)Instances.Count ? Instances[index] : null;

    public void Fire(int instance, int stinger)
    {
        if (Row(instance) is { } row && (uint)stinger < (uint)row.Stingers.Count)
            row.Instance.TriggerStinger(row.Stingers[stinger].Name);
    }

    /// <summary>Takes the transition that matches now (else the first from this section), quantized like its rule.</summary>
    public void Next(int instance)
    {
        if (Row(instance) is not { } row)
            return;
        var plan = row.Director.Plan;
        var section = row.Instance.CurrentSection is { } id ? plan.FindSection(id) : -1;
        if (section < 0)
            return;
        var rule = plan.FirstMatchingRule(section, row.Director.Parameters);
        if (rule is null)
        {
            foreach (var each in plan.Rules)
            {
                if (each.AppliesFrom(section) && each.To != section && plan.Sections[each.To].Kind == MusicSectionKind.Section)
                {
                    rule = each;
                    break;
                }
            }
        }

        if (rule is not null)
            row.Instance.GoToSection(plan.Sections[rule.To].Id, rule.Quantize, rule.FadeOutBeats ?? 0.5f);
    }

    public void Step(int instance, int parameter, int delta)
    {
        if (Row(instance) is { } row && (uint)parameter < (uint)row.Parameters.Count)
        {
            var p = row.Parameters[parameter];
            p.Set(p.Target + delta);
        }
    }

    public void Label(int instance, int parameter, int label)
    {
        if (Row(instance) is { } row && (uint)parameter < (uint)row.Parameters.Count)
            row.Parameters[parameter].Set(label);
    }
}

internal sealed class MusicInstanceRow
{
    // Events name rows by these indices (RmlUi's nested data-for can hand an event a stale outer index).
    public MusicInstanceRow(AdaptiveMusicInstance instance, MusicDirector director, int index)
    {
        Instance = instance;
        Director = director;
        Music = instance.Music?.ResourceName ?? "Adaptive music";
        var plan = director.Plan;
        for (var i = 0; i < director.Parameters.Count; i++)
        {
            Parameters.Add(new MusicParameterRow(this, index, i));
            var labels = director.Parameters.LabelsOf(i);
            for (var l = 0; l < labels.Count && director.Parameters.KindOf(i) == MusicParameterKind.Labeled; l++)
                Labels.Add(new MusicLabelRow { Owner = index, Parameter = i, Index = l, Text = labels[l] });
        }
        foreach (var layer in plan.Layers)
            Layers.Add(new MusicLayerRow { Name = string.IsNullOrEmpty(layer.Name) ? layer.Id : layer.Name });
        foreach (var section in plan.Sections)
        {
            if (section.Kind == MusicSectionKind.Stinger)
                Stingers.Add(new MusicStingerRow { Owner = index, Index = Stingers.Count, Name = section.Id });
        }
    }

    public AdaptiveMusicInstance Instance { get; }

    public MusicDirector Director { get; }

    public string Music { get; }

    public string Section = "";
    public int Bar, Beat;
    public string Pending = "";
    public float PendingSeconds;

    public List<MusicParameterRow> Parameters { get; } = [];

    public List<MusicLayerRow> Layers { get; } = [];

    public List<MusicStingerRow> Stingers { get; } = [];

    /// <summary>Every labeled parameter's labels.</summary>
    public List<MusicLabelRow> Labels { get; } = [];

    public void Refresh()
    {
        var position = Instance.GetPosition();
        Section = position.Section ?? "";
        (Bar, Beat) = (position.Bar, position.Beat);
        Pending = Instance.PendingTransition ?? "";
        PendingSeconds = Math.Max(0f, Instance.PendingSeconds);
        for (var l = 0; l < Layers.Count; l++)
            Layers[l].GainDb = Director.LayerGainDb(l);
        foreach (var p in Parameters)
            p.Refresh();
    }
}

internal sealed class MusicParameterRow
{
    private readonly MusicInstanceRow _owner;
    private readonly int _index;

    public MusicParameterRow(MusicInstanceRow owner, int ownerIndex, int index)
    {
        _owner = owner;
        _index = index;
        Owner = ownerIndex;
        var parameters = owner.Director.Parameters;
        Name = parameters.NameOf(index);
        Kind = parameters.KindOf(index);
        Labels = [.. parameters.LabelsOf(index)];
        (Min, Max) = Kind == MusicParameterKind.Labeled ? (0, Math.Max(0, Labels.Count - 1)) : (parameters.MinOf(index), parameters.MaxOf(index));
        Step = Kind == MusicParameterKind.Continuous ? Math.Max(1e-3f, (Max - Min) / 100f) : 1f;
    }

    public int Owner { get; }

    public int Param => _index;

    public string Name { get; }

    public MusicParameterKind Kind { get; }

    public float Min { get; }

    public float Max { get; }

    public float Step { get; }

    public List<string> Labels { get; }

    /// <summary>The smoothed value.</summary>
    public float Value;

    /// <summary>What it seeks to (the slider).</summary>
    public float Target;

    public int LabelIndex;

    public string Label = "";

    public void Refresh()
    {
        var parameters = _owner.Director.Parameters;
        Value = parameters.GetValue(_index);
        Target = parameters.GetTarget(_index);
        LabelIndex = (int)MathF.Round(Value);
        Label = (uint)LabelIndex < (uint)Labels.Count ? Labels[LabelIndex] : "";
    }

    /// <summary>Sets it through the instance, as game code would (clamped; continuous parameters seek).</summary>
    public void Set(float value)
    {
        _owner.Instance.SetParameter(_index, value);
        Refresh();
    }
}

internal sealed class MusicLayerRow
{
    public string Name = "";
    public float GainDb;
}

internal sealed class MusicLabelRow
{
    public int Owner;
    public int Parameter;
    public int Index;
    public string Text = "";
}

internal sealed class MusicStingerRow
{
    public int Owner;
    public int Index;
    public string Name = "";
}

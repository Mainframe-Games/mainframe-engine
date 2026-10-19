using System.Globalization;
using System.Text;
using MainframeEngine.Audio.Music;
using MainframeEngine.UI.Rml;

namespace MainframeEngine.Editor.Music;

/// <summary>
/// The right panel's Adaptive tab (A; docs/design/future/adaptive-music.md, "The Adaptive panel") for the active song tab:
/// Now (what adaptive playback is on, the pending transition and its countdown, Play / Stop, the last bake), the
/// parameters (an audition control each, with the smoothed value as a ghost while it seeks; Set as Default, an inline
/// editor), the layers (colour, filter, a live gain meter, their curves), the transitions as sentences (from, to, when,
/// quantize, fade: each a menu; the rule that would fire now highlighted; priority by order), the stingers (Fire, quantize,
/// pickup) and the validation problems. Its markup is built into <c>#song-adaptive</c> only when the song, the selection,
/// the playback state or the audition values change; steady frames only move the live values (and allocate nothing then).
/// Every edit is a <see cref="SongDocument"/> step; audition values are kept per tab (<see cref="SongTab.Auditions"/>).
/// </summary>
public sealed class AdaptivePanel(EditorWorkspace workspace)
{
    private const string RootId = "song-adaptive";
    private static readonly string[] Percent = [.. Enumerable.Range(0, 101).Select(i => i.ToString(CultureInfo.InvariantCulture) + "%")];
    private static readonly string[] FadeChoices = ["0", "0.5", "1", "2", "4"];

    private RmlDocument _document;
    private bool _attached;
    private SongTab? _tab;
    private int _signature;
    private bool _built;
    private int _editParam = -1;
    private int _editRule = -1;
    private bool _bakedAvailable;
    private int _auditionVersion;
    private string _bakeText = "";
    private MusicPlan? _plan;
    private MusicParameterSet? _params;
    private float[] _gains = [];
    private float[] _cutoffs = [];

    // What the live rows show (written only on change).
    private long _shownNow = long.MinValue;
    private long _shownPending = long.MinValue;
    private int _shownRule = -2;
    private int[] _shownMeter = [];
    private int[] _shownGhost = [];
    private int[] _meterLayer = [];   // the plan layer of each meter row
    private string[] _meterLayerId = []; // its layer id (the baked plan's index is looked up by it)
    private int[] _ghostParam = [];   // the parameter of each ghost (continuous parameters)

    /// <summary>The parameter whose inline editor is open, or −1.</summary>
    public int EditingParameter => _editParam;

    /// <summary>The transition whose editor (the popover) is open, or −1.</summary>
    public int EditingTransition => _editRule;

    /// <summary>Opens (or, with −1, closes) transition <paramref name="index"/>'s editor.</summary>
    public void EditTransition(int index)
    {
        _editRule = index;
        _built = false;
    }

    /// <summary>The song panel's document (re)loaded.</summary>
    public void Attach(RmlDocument document)
    {
        _document = document;
        _attached = true;
        _built = false;
    }

    /// <summary>Every frame while the Adaptive tab shows (from the <see cref="SongPanel"/>).</summary>
    public void Tick(SongTab? tab)
    {
        if (!_attached)
            return;
        if (!ReferenceEquals(tab, _tab))
        {
            _tab = tab;
            _built = false;
            _editParam = -1;
            _editRule = -1;
        }

        var signature = Signature(tab);
        if (!_built || signature != _signature)
        {
            _signature = signature;
            Rebuild(tab);
        }

        if (tab is not null)
            TickLive(tab);
    }

    // What the markup depends on, as a hash (no allocation on steady frames).
    private int Signature(SongTab? tab)
    {
        if (tab is null)
            return 0;
        var state = tab.Player.AdaptiveState;
        var hash = new HashCode();
        hash.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(tab));
        hash.Add(tab.Document.Version);
        hash.Add(tab.ViewVersion);
        hash.Add(tab.Document.IsReadOnly);
        hash.Add(state.Playing);
        hash.Add(tab.IsPlayingAdaptive);
        hash.Add(tab.AuditionBaked);
        hash.Add(_editParam);
        hash.Add(_editRule);
        hash.Add(_auditionVersion);
        hash.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(tab.Player.Plan));
        hash.Add(workspace.SongView.BakeOf(tab.Document.FilePath ?? "") is { } job ? (int)(job.Progress * 100) : -1);
        hash.Add(workspace.SongView.BakeVersion); // a bake finished: its state is read again
        hash.Add(tab.IsDirty);
        return hash.ToHashCode();
    }

    // ── Markup ───────────────────────────────────────────────────────────────────────────────────────────────────

    private void Rebuild(SongTab? tab)
    {
        _built = true;
        _bakeText = "";
        _shownNow = long.MinValue;
        _shownPending = long.MinValue;
        _shownRule = -2;
        var root = _document.GetElementById(RootId);
        if (root.IsNull)
            return;
        if (tab is null)
        {
            root.SetInnerRml("<div class=\"ad-empty\">Open a song to edit its adaptive music.</div>");
            return;
        }

        var song = tab.Document.Song;
        if (song.Adaptive is not { } adaptive)
        {
            root.SetInnerRml("<div class=\"ad-empty\"><span class=\"icon icon-lg icon-stack-2\"></span><div>This song plays as a timeline. " +
                "Make it adaptive to give it sections, parameters, layers and transitions: music your game changes as it plays.</div>" +
                "<button class=\"with-icon primary\" data-ad=\"make\" data-tooltip=\"Make Adaptive — adds the Sections lane under the ruler (one undo step)\">" +
                "<span class=\"icon icon-sm icon-stack-2\"></span><span>Make Adaptive</span></button></div>");
            return;
        }

        PreparePlan(tab);
        var editable = !tab.Document.IsReadOnly;
        var sb = new StringBuilder(4096);
        BuildNow(sb, tab, adaptive);
        BuildParameters(sb, tab, adaptive, editable);
        BuildLayers(sb, tab, song, adaptive, editable);
        BuildTransitions(sb, adaptive, editable, _editRule);
        BuildStingers(sb, tab, adaptive, editable);
        BuildProblems(sb, song);
        root.SetInnerRml(sb.ToString());
    }

    // The UI's own copy of the parameters (the audition values) to evaluate the layers and the matching rule while stopped.
    private void PreparePlan(SongTab tab)
    {
        var plan = tab.Player.Plan ?? SongAdaptiveCompiler.Compile(tab.Document.Song, 48000);
        if (!ReferenceEquals(plan, _plan))
        {
            _plan = plan;
            _params = plan.CreateParameters();
            _gains = new float[plan.Layers.Length];
            _cutoffs = new float[plan.Layers.Length];
        }

        var parameters = _params!;
        for (var i = 0; i < parameters.Count; i++)
            parameters.SetTarget(i, tab.Auditions.TryGetValue(parameters.NameOf(i), out var v) ? v : parameters.DefaultOf(i), immediate: true);
        plan.EvaluateLayers(parameters, _gains, _cutoffs);
    }

    private void BuildNow(StringBuilder sb, SongTab tab, SongAdaptive adaptive)
    {
        var playing = tab.IsPlayingAdaptive;
        _bakedAvailable = tab.Baked.Available(tab.Document);
        sb.Append("<div class=\"ad-section\"><div class=\"ad-head\"><span>Now</span><span class=\"grow\"></span>")
            .Append("<button class=\"tool-button small").Append(playing ? " active" : "").Append("\" data-ad=\"play\" data-tooltip=\"")
            .Append(playing ? "Stop (Space or Shift+Space)" : "Play Adaptive (Shift+Space) — from the start section with these parameter values; Alt+Shift+Space from the section under the playhead")
            .Append("\"><span class=\"icon icon-sm icon-").Append(playing ? "player-stop" : "player-track-next").Append("\"></span></button>")
            .Append("<button class=\"tool-button small\" data-ad=\"timeline\" data-tooltip=\"Make Timeline Song — removes the sections, parameters, layers and transitions (Ctrl/Cmd+Z brings them back)\"><span class=\"icon icon-sm icon-trash\"></span></button>")
            .Append("</div><div class=\"ad-chips\"><div class=\"ad-chip").Append(tab.AuditionBaked ? "" : " active")
            .Append("\" data-ad=\"mode\" data-j=\"0\" data-tooltip=\"Live — the song as it is now, through the editor's engine and plugins: edits are heard at once\">Live</div><div class=\"ad-chip")
            .Append(tab.AuditionBaked ? " active" : "").Append(_bakedAvailable ? "" : " disabled").Append("\" data-ad=\"mode\" data-j=\"1\" data-tooltip=\"")
            .Append(_bakedAvailable ? "Baked — the last bake's stems through the runtime player, exactly what the game plays" : "Baked — bake the song first (Ctrl+B)")
            .Append("\">Baked</div></div><div class=\"ad-now\"><span id=\"ad-now\" class=\"mono\">")
            .Append(playing ? "" : "Stopped").Append("</span></div><div id=\"ad-pending\" class=\"ad-pending\"></div>");
        sb.Append("<div class=\"ad-bake\"><span class=\"grow\">").Append(RmlText.Escape(BakeText(tab))).Append("</span>")
            .Append("<button class=\"text-button\" data-ad=\"bake\" data-tooltip=\"Bake (Ctrl+B) — the stems and the .mmusic manifest the game plays\">Bake</button></div>");
        if (adaptive.Sections.Count == 0)
            sb.Append("<div class=\"ad-note\">Drag in the Sections lane under the ruler to make the first section.</div>");
        sb.Append("</div>");
    }

    private string BakeText(SongTab tab)
    {
        if (workspace.SongView.BakeOf(tab.Document.FilePath ?? "") is { } job)
            return "Baking… " + job.Status;
        if (_bakeText.Length > 0)
            return _bakeText;
        var manifest = SongBaker.ManifestPathFor(tab.Document.Song, tab.Document.Name);
        var full = AssetDatabase.Current.ToAbsolutePath(manifest);
        if (full is null || !File.Exists(full))
            return _bakeText = "Not baked yet";
        var baked = File.GetLastWriteTime(full);
        var outOfDate = tab.Document.IsDirty || (tab.FilePath is { } path && File.Exists(path) && File.GetLastWriteTimeUtc(path) > File.GetLastWriteTimeUtc(full));
        return _bakeText = string.Create(CultureInfo.InvariantCulture, $"Baked {baked:HH:mm}{(outOfDate ? " · out of date" : " · up to date")}");
    }

    private void BuildParameters(StringBuilder sb, SongTab tab, SongAdaptive adaptive, bool editable)
    {
        sb.Append("<div class=\"ad-section\"><div class=\"ad-head\"><span>Parameters</span><span class=\"grow\"></span>")
            .Append("<button class=\"tool-button small\" data-ad=\"param-reset\" data-tooltip=\"Reset All — every audition value back to its parameter's default\"><span class=\"icon icon-sm icon-refresh\"></span></button>");
        if (editable)
            sb.Append("<button class=\"tool-button small\" data-ad=\"param-add\" data-tooltip=\"Add Parameter — a value your game sets (intensity, area, health…)\"><span class=\"icon icon-sm icon-plus\"></span></button>");
        sb.Append("</div>");
        if (adaptive.Parameters.Count == 0)
            sb.Append("<div class=\"ad-note\">No parameters yet: add one for the game to drive the music with.</div>");
        var ghosts = new List<int>();
        for (var i = 0; i < adaptive.Parameters.Count; i++)
        {
            var p = adaptive.Parameters[i];
            var n = i.ToString(CultureInfo.InvariantCulture);
            var value = AuditionValue(tab, p);
            sb.Append("<div class=\"ad-row\"><div class=\"ad-line\"><span class=\"ad-name\">").Append(RmlText.Escape(p.Name)).Append("</span><span class=\"ad-badge\">")
                .Append(p.Kind switch { MusicParameterKind.Discrete => "discrete", MusicParameterKind.Labeled => "labeled", _ => "continuous" })
                .Append("</span><span class=\"grow\"></span><span class=\"mono ad-val\" id=\"ad-pv-").Append(n).Append("\">").Append(RmlText.Escape(ValueText(p, value))).Append("</span>");
            if (editable)
            {
                sb.Append("<button class=\"tool-button small\" data-ad=\"param-default\" data-i=\"").Append(n)
                    .Append("\" data-tooltip=\"Set as Default — this value is the parameter's starting value in the game\"><span class=\"icon icon-sm icon-check\"></span></button>")
                    .Append("<button class=\"tool-button small").Append(_editParam == i ? " active" : "").Append("\" data-ad=\"param-edit\" data-i=\"").Append(n)
                    .Append("\" data-tooltip=\"Edit — name, kind, range, labels, default and seek speed\"><span class=\"icon icon-sm icon-settings\"></span></button>")
                    .Append("<button class=\"tool-button small\" data-ad=\"param-remove\" data-i=\"").Append(n)
                    .Append("\" data-tooltip=\"Remove the parameter\"><span class=\"icon icon-sm icon-trash\"></span></button>");
            }

            sb.Append("</div>");
            switch (p.Kind)
            {
                case MusicParameterKind.Labeled:
                    sb.Append("<div class=\"ad-chips\">");
                    var labels = p.Labels ?? [];
                    for (var j = 0; j < labels.Count; j++)
                        sb.Append("<div class=\"ad-chip").Append((int)MathF.Round(value) == j ? " active" : "").Append("\" data-ad=\"param-label\" data-i=\"").Append(n)
                            .Append("\" data-j=\"").Append(j.ToString(CultureInfo.InvariantCulture)).Append("\">").Append(RmlText.Escape(labels[j])).Append("</div>");
                    sb.Append("</div>");
                    break;
                case MusicParameterKind.Discrete:
                    sb.Append("<div class=\"ad-chips\"><button class=\"tool-button small\" data-ad=\"param-step\" data-i=\"").Append(n)
                        .Append("\" data-j=\"-1\" data-tooltip=\"One less\"><span class=\"icon icon-sm icon-arrow-down\"></span></button>")
                        .Append("<button class=\"tool-button small\" data-ad=\"param-step\" data-i=\"").Append(n)
                        .Append("\" data-j=\"1\" data-tooltip=\"One more\"><span class=\"icon icon-sm icon-arrow-up\"></span></button></div>");
                    break;
                default:
                    var step = (p.Max - p.Min) / 100f;
                    sb.Append("<div class=\"ad-slider\"><input type=\"range\" class=\"range\" min=\"").Append(Number(p.Min)).Append("\" max=\"").Append(Number(p.Max))
                        .Append("\" step=\"").Append(Number(step > 0 ? step : 0.01f)).Append("\" value=\"").Append(Number(value)).Append("\" data-ad-param=\"").Append(n)
                        .Append("\" data-tooltip=\"Audition — the value the music plays with here; the game sets it with SetParameter\"/>")
                        .Append("<div class=\"ad-ghost hidden\" id=\"ad-pg-").Append(n).Append("\" data-tooltip=\"The value the music has reached (it seeks at the parameter's speed)\"></div></div>");
                    ghosts.Add(i);
                    break;
            }

            if (_editParam == i && editable)
                BuildParameterEditor(sb, p, n);
            sb.Append("</div>");
        }

        sb.Append("</div>");
        _ghostParam = [.. ghosts];
        _shownGhost = new int[_ghostParam.Length];
        Array.Fill(_shownGhost, -2);
    }

    private static void BuildParameterEditor(StringBuilder sb, MusicParameterInfo p, string n)
    {
        sb.Append("<div class=\"ad-edit\"><div class=\"ad-field\"><span>Name</span><input type=\"text\" class=\"text\" value=\"").Append(RmlText.Escape(p.Name))
            .Append("\" data-ad-field=\"name\" data-i=\"").Append(n).Append("\"/></div><div class=\"ad-field\"><span>Kind</span><div class=\"ad-chips\">");
        foreach (var (kind, label) in (ReadOnlySpan<(MusicParameterKind, string)>)[(MusicParameterKind.Continuous, "continuous"), (MusicParameterKind.Discrete, "discrete"), (MusicParameterKind.Labeled, "labeled")])
            sb.Append("<div class=\"ad-chip").Append(p.Kind == kind ? " active" : "").Append("\" data-ad=\"param-kind\" data-i=\"").Append(n).Append("\" data-j=\"")
                .Append(((int)kind).ToString(CultureInfo.InvariantCulture)).Append("\">").Append(label).Append("</div>");
        sb.Append("</div></div>");
        if (p.Kind == MusicParameterKind.Labeled)
        {
            sb.Append("<div class=\"ad-field\"><span>Labels</span><input type=\"text\" class=\"text\" value=\"").Append(RmlText.Escape(string.Join(", ", p.Labels ?? [])))
                .Append("\" data-ad-field=\"labels\" data-i=\"").Append(n).Append("\" data-tooltip=\"Comma-separated: Calm, Combat, Boss\"/></div>");
        }
        else
        {
            Field(sb, "Min", "min", p.Min, n);
            Field(sb, "Max", "max", p.Max, n);
        }

        Field(sb, "Default", "default", p.Default, n);
        if (p.Kind == MusicParameterKind.Continuous)
            Field(sb, "Seek /s", "seek", p.SeekSpeed, n, "Seek speed — units per second the value moves towards a new target (0: it jumps)");
        sb.Append("</div>");
    }

    private static void Field(StringBuilder sb, string label, string field, float value, string n, string? tooltip = null)
    {
        sb.Append("<div class=\"ad-field\"><span>").Append(label).Append("</span><input type=\"text\" class=\"text\" value=\"").Append(Number(value))
            .Append("\" data-ad-field=\"").Append(field).Append("\" data-i=\"").Append(n).Append('"');
        if (tooltip is not null)
            sb.Append(" data-tooltip=\"").Append(tooltip).Append('"');
        sb.Append("/></div>");
    }

    private void BuildLayers(StringBuilder sb, SongTab tab, Song song, SongAdaptive adaptive, bool editable)
    {
        sb.Append("<div class=\"ad-section\"><div class=\"ad-head\"><span>Layers</span><span class=\"grow\"></span>");
        if (editable)
            sb.Append("<button class=\"tool-button small\" data-ad=\"layer-add\" data-tooltip=\"Add Layer — tracks in a layer bake to one stem per section; curves set its volume\"><span class=\"icon icon-sm icon-plus\"></span></button>");
        sb.Append("</div>");
        var meters = new List<int>();
        var meterIds = new List<string>();
        var plan = _plan!;
        if (SongAdaptiveCompiler.NeedsMainLayer(song))
        {
            var count = song.Tracks.Count(t => SongAdaptiveCompiler.LayerOf(song, t) == SongLayer.MainId);
            sb.Append("<div class=\"ad-row\"><div class=\"ad-line\"><span class=\"ad-swatch\" style=\"background-color: #8b93a5;\"></span><span class=\"ad-name\">Main</span>")
                .Append("<span class=\"ad-dim\">").Append(Tracks(count)).Append(" · always full volume</span></div></div>");
        }

        for (var i = 0; i < adaptive.Layers.Count; i++)
        {
            var layer = adaptive.Layers[i];
            var n = i.ToString(CultureInfo.InvariantCulture);
            var count = song.Tracks.Count(t => t.Layer == layer.Id);
            var planIndex = plan.FindLayer(layer.Id);
            sb.Append("<div class=\"ad-row\"><div class=\"ad-line\"><span class=\"ad-swatch\" data-ad=\"layer-color\" data-i=\"").Append(n)
                .Append("\" style=\"background-color: ").Append(layer.Color ?? SongCanvas.Palette[i % SongCanvas.Palette.Length])
                .Append(";\" data-tooltip=\"Layer colour\"></span><span class=\"ad-name\" data-ad-rename=\"layer\" data-i=\"").Append(n)
                .Append("\" data-tooltip=\"Double-click to rename\">").Append(RmlText.Escape(layer.Name)).Append("</span><span class=\"ad-dim\">").Append(Tracks(count))
                .Append("</span><span class=\"grow\"></span><div class=\"ad-chip\" data-ad=\"layer-filter\" data-i=\"").Append(n)
                .Append("\" data-tooltip=\"Filter — a low- or high-pass on the layer whose cutoff a curve can drive\">")
                .Append(layer.Filter switch { MusicLayerFilter.LowPass => "low-pass", MusicLayerFilter.HighPass => "high-pass", _ => "no filter" }).Append("</div>");
            if (editable)
                sb.Append("<button class=\"tool-button small\" data-ad=\"layer-remove\" data-i=\"").Append(n)
                    .Append("\" data-tooltip=\"Remove the layer (its curves too; its tracks go back to Main)\"><span class=\"icon icon-sm icon-trash\"></span></button>");
            sb.Append("</div><div class=\"ad-line ad-wrap\"><div class=\"ad-meter\" data-tooltip=\"The layer's gain now (its curves at the parameters' values)\"><div class=\"ad-meter-fill\" id=\"ad-lm-")
                .Append(meters.Count.ToString(CultureInfo.InvariantCulture)).Append("\"></div></div>");
            meters.Add(planIndex);
            meterIds.Add(layer.Id);
            for (var c = 0; c < adaptive.Curves.Count; c++)
            {
                var curve = adaptive.Curves[c];
                if (curve.Layer != layer.Id)
                    continue;
                sb.Append("<div class=\"ad-chip ad-curve").Append(ReferenceEquals(curve, tab.SelectedCurve) ? " active" : "").Append("\" data-ad=\"curve-open\" data-i=\"")
                    .Append(c.ToString(CultureInfo.InvariantCulture)).Append("\" data-tooltip=\"Edit in the Curves pane: the layer's ")
                    .Append(curve.Target == MusicCurveTarget.Volume ? "volume" : "filter cutoff").Append(" over the parameter\">")
                    .Append(curve.Target == MusicCurveTarget.Volume ? "vol" : "cutoff").Append(" by ").Append(RmlText.Escape(curve.Parameter)).Append("</div>");
            }

            if (editable && adaptive.Parameters.Count > 0)
                sb.Append("<div class=\"ad-chip\" data-ad=\"curve-add\" data-i=\"").Append(n)
                    .Append("\" data-tooltip=\"+ Curve — the layer's volume (or, with a filter, its cutoff) over a parameter\">+ Curve</div>");

            sb.Append("</div></div>");
        }

        if (adaptive.Layers.Count == 0)
            sb.Append("<div class=\"ad-note\">Every track plays in Main. Add layers and pick them on the track headers' chips to mix them by parameter.</div>");
        sb.Append("</div>");
        _meterLayer = [.. meters];
        _meterLayerId = [.. meterIds];
        _shownMeter = new int[_meterLayer.Length];
        Array.Fill(_shownMeter, -1);
    }

    private static string Tracks(int count) => count == 1 ? "1 track" : count.ToString(CultureInfo.InvariantCulture) + " tracks";

    private static void BuildTransitions(StringBuilder sb, SongAdaptive adaptive, bool editable, int editing)
    {
        sb.Append("<div class=\"ad-section\"><div class=\"ad-head\"><span>Transitions</span><span class=\"grow\"></span>");
        if (editable)
            sb.Append("<button class=\"tool-button small\" data-ad=\"rule-add\" data-tooltip=\"Add Transition — the first rule that matches wins\"><span class=\"icon icon-sm icon-plus\"></span></button>");
        sb.Append("</div>");
        if (adaptive.Transitions.Count == 0)
            sb.Append("<div class=\"ad-note\">No transitions: sections only loop or continue. Add one to move between them when a parameter changes.</div>");
        for (var k = 0; k < adaptive.Transitions.Count; k++)
        {
            var t = adaptive.Transitions[k];
            var n = k.ToString(CultureInfo.InvariantCulture);
            sb.Append("<div class=\"ad-row ad-rule\" id=\"ad-rule-").Append(n).Append("\"><div class=\"ad-line\">")
                .Append(Chip("rule-from", n, From(adaptive, t), "From — the sections it leaves")).Append("<span class=\"icon icon-sm icon-arrow-right ad-to\"></span>")
                .Append(Chip("rule-to", n, Name(adaptive, t.To), "To — the section it goes to"))
                .Append("<span class=\"grow\"></span>");
            if (editable)
            {
                sb.Append("<button class=\"tool-button small").Append(editing == k ? " active" : "").Append("\" data-ad=\"rule-edit\" data-i=\"").Append(n)
                    .Append("\" data-tooltip=\"Edit — sources, conditions, timing, entry, fades, segment and stinger\"><span class=\"icon icon-sm icon-settings\"></span></button>");
                sb.Append("<button class=\"tool-button small\" data-ad=\"rule-up\" data-i=\"").Append(n).Append("\" data-tooltip=\"Earlier: tried before the ones below\"><span class=\"icon icon-sm icon-arrow-up\"></span></button>")
                    .Append("<button class=\"tool-button small\" data-ad=\"rule-down\" data-i=\"").Append(n).Append("\" data-tooltip=\"Later\"><span class=\"icon icon-sm icon-arrow-down\"></span></button>")
                    .Append("<button class=\"tool-button small\" data-ad=\"rule-remove\" data-i=\"").Append(n).Append("\" data-tooltip=\"Remove the transition\"><span class=\"icon icon-sm icon-trash\"></span></button>");
            }

            sb.Append("</div><div class=\"ad-line ad-wrap\">").Append(Chip("rule-when", n, When(t), "When — the condition; without one it only fires from game code (GoToSection)"))
                .Append(Chip("rule-quantize", n, QuantizeText(t.Quantize, t.Every), "Timing — where in the music it happens"))
                .Append(Chip("rule-fade", n, FadeText(t.FadeOutBeats), "Fade — the outgoing music's fade after the boundary, in beats"))
                .Append("</div>");
            if (editing == k && editable)
                BuildRuleEditor(sb, adaptive, t, n);
            sb.Append("</div>");
        }

        sb.Append("</div>");
    }

    // The transition popover (A5): every setting of the rule, under its row.
    private static void BuildRuleEditor(StringBuilder sb, SongAdaptive adaptive, MusicTransitionInfo t, string n)
    {
        sb.Append("<div class=\"ad-edit\"><div class=\"ad-field\"><span>From</span><div class=\"ad-chips\">")
            .Append(Toggle("rule-from-toggle", n, -1, "any", t.From.Count == 0 || t.From.Contains(MusicPlan.AnySection), "From any section"));
        for (var i = 0; i < adaptive.Sections.Count; i++)
        {
            var s = adaptive.Sections[i];
            if (s.Kind == MusicSectionKind.Section)
                sb.Append(Toggle("rule-from-toggle", n, i, s.Name, t.From.Contains(s.Id), "Leaves " + s.Name + " (click to add or remove it)"));
        }

        sb.Append("</div></div><div class=\"ad-field\"><span>To</span><div class=\"ad-chips\">");
        for (var i = 0; i < adaptive.Sections.Count; i++)
        {
            var s = adaptive.Sections[i];
            if (s.Kind == MusicSectionKind.Section)
                sb.Append(Toggle("rule-to-set", n, i, s.Name, t.To == s.Id, "Goes to " + s.Name));
        }

        var any = t.When?.Any is { Count: > 0 } && t.When.All is not { Count: > 0 };
        var conditions = (any ? t.When!.Any : t.When?.All) ?? [];
        sb.Append("</div></div><div class=\"ad-field\"><span>When</span><div class=\"ad-chips\">")
            .Append(Toggle("rule-mode", n, 0, "all of", !any, "Every condition must hold"))
            .Append(Toggle("rule-mode", n, 1, "any of", any, "One condition is enough"))
            .Append("<div class=\"ad-chip\" data-ad=\"cond-add\" data-i=\"").Append(n).Append("\" data-tooltip=\"Add a condition on a parameter\">+ Condition</div></div></div>");
        for (var c = 0; c < conditions.Count; c++)
        {
            var cond = conditions[c];
            var j = c.ToString(CultureInfo.InvariantCulture);
            var p = adaptive.FindParameter(cond.Parameter);
            sb.Append("<div class=\"ad-field\"><span></span><div class=\"ad-chips\">")
                .Append(Chip2("cond-param", n, j, cond.Parameter, "The parameter")).Append(Chip2("cond-op", n, j, Op(cond.Op), "The comparison"));
            if (p is { Kind: MusicParameterKind.Continuous })
                sb.Append("<input type=\"text\" class=\"text ad-num\" value=\"").Append(Number(cond.Value.Number)).Append("\" data-ad-cond=\"").Append(n).Append(':').Append(j)
                    .Append("\" data-tooltip=\"The value\"/>");
            else
                sb.Append(Chip2("cond-value", n, j, cond.Value.Label ?? Number(cond.Value.Number), "The value"));
            sb.Append("<button class=\"tool-button small\" data-ad=\"cond-remove\" data-i=\"").Append(n).Append("\" data-j=\"").Append(j)
                .Append("\" data-tooltip=\"Remove the condition\"><span class=\"icon icon-sm icon-x\"></span></button></div></div>");
        }

        sb.Append("<div class=\"ad-field\"><span>Timing</span><div class=\"ad-chips\">")
            .Append(Chip("rule-quantize", n, QuantizeText(t.Quantize, t.Every), "Where in the music it happens"));
        if (t.Quantize == MusicQuantize.Bars)
            sb.Append("<input type=\"text\" class=\"text ad-num\" value=\"").Append((t.Every ?? 1).ToString(CultureInfo.InvariantCulture))
                .Append("\" data-ad-rule=\"").Append(n).Append(":every\" data-tooltip=\"Every how many bars from the section's start\"/>");
        sb.Append("</div></div><div class=\"ad-field\"><span>Entry</span><div class=\"ad-chips\">")
            .Append(Toggle("rule-entry", n, 0, "at its start", t.Entry == MusicEntry.Start, "The destination plays from its start"))
            .Append(Toggle("rule-entry", n, 1, "in sync", t.Entry == MusicEntry.Sync, "The destination plays from the same point into it (parallel arrangements)"))
            .Append("</div></div><div class=\"ad-field\"><span>Fades</span><div class=\"ad-chips\">")
            .Append(Chip("rule-fade", n, "out: " + FadeText(t.FadeOutBeats), "The outgoing music's fade after the boundary"))
            .Append(Chip("rule-fadein", n, "in: " + (t.FadeInBeats is { } fin and > 0 ? string.Create(CultureInfo.InvariantCulture, $"{fin:0.##} beats") : "none"),
                "The incoming music's fade-in"))
            .Append("</div></div><div class=\"ad-field\"><span>Between</span><div class=\"ad-chips\">")
            .Append(Chip("rule-segment", n, "segment: " + (adaptive.FindSection(t.Segment)?.Name ?? "none"), "A transition segment played once in between"))
            .Append(Chip("rule-stinger", n, "stinger: " + (adaptive.FindSection(t.Stinger)?.Name ?? "none"), "A stinger fired at the boundary"))
            .Append("</div></div></div>");
    }

    private static string Toggle(string action, string n, int j, string text, bool active, string tooltip) =>
        "<div class=\"ad-chip" + (active ? " active" : "") + "\" data-ad=\"" + action + "\" data-i=\"" + n + "\" data-j=\"" + j.ToString(CultureInfo.InvariantCulture) +
        "\" data-tooltip=\"" + RmlText.Escape(tooltip) + "\">" + RmlText.Escape(text) + "</div>";

    private static string Chip2(string action, string n, string j, string text, string tooltip) =>
        "<div class=\"ad-chip\" data-ad=\"" + action + "\" data-i=\"" + n + "\" data-j=\"" + j + "\" data-tooltip=\"" + tooltip + "\">" + RmlText.Escape(text) + "</div>";

    private static string Chip(string action, string n, string text, string tooltip) =>
        "<div class=\"ad-chip\" data-ad=\"" + action + "\" data-i=\"" + n + "\" data-tooltip=\"" + tooltip + "\">" + RmlText.Escape(text) + "</div>";

    private static string From(SongAdaptive adaptive, MusicTransitionInfo t) =>
        t.From.Count == 0 || t.From.Contains(MusicPlan.AnySection) ? "any section" : string.Join(", ", t.From.Select(id => Name(adaptive, id)));

    private static string Name(SongAdaptive adaptive, string? id) => adaptive.FindSection(id)?.Name ?? (string.IsNullOrEmpty(id) ? "—" : id + " (missing)");

    private static string When(MusicTransitionInfo t)
    {
        var all = t.When?.All;
        var any = t.When?.Any;
        if (all is not { Count: > 0 } && any is not { Count: > 0 })
            return "only from code";
        var parts = (all is { Count: > 0 } ? all : any!).Select(c => $"{c.Parameter} {Op(c.Op)} {(c.Value.Label ?? Number(c.Value.Number))}");
        return "when " + string.Join(all is { Count: > 0 } ? " and " : " or ", parts);
    }

    private static string Op(MusicCompare op) => op switch
    {
        MusicCompare.Equal => "=",
        MusicCompare.NotEqual => "!=",
        MusicCompare.Less => "<",
        MusicCompare.LessOrEqual => "<=",
        MusicCompare.Greater => ">",
        _ => ">=",
    };

    private static string QuantizeText(MusicQuantize quantize, int? every = null) => quantize switch
    {
        MusicQuantize.Immediate => "at once",
        MusicQuantize.Beat => "next beat",
        MusicQuantize.Bar => "next bar",
        MusicQuantize.Bars => string.Create(CultureInfo.InvariantCulture, $"every {every ?? 1} bars"),
        MusicQuantize.SectionEnd => "section end",
        _ => "exit marker",
    };

    private static string FadeText(float? beats) => beats switch
    {
        null => "default fade",
        0 => "no fade",
        1 => "fade 1 beat",
        { } b => string.Create(CultureInfo.InvariantCulture, $"fade {b:0.##} beats"),
    };

    private static void BuildStingers(StringBuilder sb, SongTab tab, SongAdaptive adaptive, bool editable)
    {
        var any = false;
        var playing = tab.IsPlayingAdaptive;
        for (var i = 0; i < adaptive.Sections.Count; i++)
        {
            var s = adaptive.Sections[i];
            if (s.Kind != MusicSectionKind.Stinger)
                continue;
            if (!any)
            {
                sb.Append("<div class=\"ad-section\"><div class=\"ad-head\"><span>Stingers</span></div>");
                any = true;
            }

            var n = i.ToString(CultureInfo.InvariantCulture);
            sb.Append("<div class=\"ad-row\"><div class=\"ad-line\"><span class=\"icon icon-sm icon-bolt ad-bolt\"></span><span class=\"ad-name\">").Append(RmlText.Escape(s.Name))
                .Append("</span><span class=\"grow\"></span><button class=\"text-button").Append(playing ? "" : " disabled").Append("\" data-ad=\"stinger-fire\" data-i=\"").Append(n)
                .Append("\" data-tooltip=\"Fire (T) — plays over the music at its quantum while the song plays adaptively\">Fire</button></div><div class=\"ad-line\">")
                .Append(editable ? Chip("stinger-quantize", n, QuantizeText(s.Quantize ?? MusicQuantize.Beat), "Lands on — where its downbeat goes") : "")
                .Append(editable ? Chip("stinger-pickup", n, s.PickupBeats == 0 ? "no pickup" : string.Create(CultureInfo.InvariantCulture, $"pickup {s.PickupBeats:0.##} beats"),
                    "Pickup — beats of the stinger that play before its quantum") : "")
                .Append("</div></div>");
        }

        if (any)
            sb.Append("</div>");
    }

    private static void BuildProblems(StringBuilder sb, Song song)
    {
        var problems = SongAdaptiveValidator.Validate(song);
        sb.Append("<div class=\"ad-section\"><div class=\"ad-head\"><span>Problems</span><span class=\"ad-dim\">")
            .Append(problems.Count == 0 ? "" : problems.Count.ToString(CultureInfo.InvariantCulture)).Append("</span></div>");
        if (problems.Count == 0)
            sb.Append("<div class=\"ad-note\">None: the song is ready to bake.</div>");
        foreach (var p in problems)
        {
            sb.Append("<div class=\"ad-problem ").Append(p.Severity == AdaptiveProblemSeverity.Error ? "error" : "warning").Append('"');
            if (p.Subject is { } subject)
                sb.Append(" data-ad=\"problem\" data-subject=\"").Append(RmlText.Escape(subject)).Append('"');
            sb.Append("><span class=\"icon icon-sm icon-").Append(p.Severity == AdaptiveProblemSeverity.Error ? "circle-x" : "alert-triangle").Append("\"></span><span>")
                .Append(RmlText.Escape(p.Message)).Append("</span></div>");
        }

        sb.Append("</div>");
    }

    // ── Live values ──────────────────────────────────────────────────────────────────────────────────────────────

    private void TickLive(SongTab tab)
    {
        if (tab.Document.Song.Adaptive is not { } adaptive || _plan is not { } plan || _params is not { } parameters)
            return;
        if (tab.AuditionBaked && tab.Baked.Instance is { Playing: true, Director: { } director } instance)
        {
            TickBaked(adaptive, instance, director);
            return;
        }

        var state = tab.Player.AdaptiveState;
        var engine = tab.Player.Engine;
        var livePlan = tab.Player.Plan;
        var live = state.Playing && ReferenceEquals(livePlan, plan);
        if (live)
        {
            for (var i = 0; i < parameters.Count && i < SongEngine.MaxPublishedParameters; i++)
                parameters.SetTarget(i, engine.ParameterValue(i), immediate: true);
        }

        // Now: the section and bar.beat; the pending transition and its countdown.
        var nowKey = live && (uint)state.Section < (uint)plan.Sections.Length ? Key(state.Section, plan.Grid.BeatAt(state.LocalFrame)) : -1;
        if (nowKey != _shownNow)
        {
            _shownNow = nowKey;
            if (_document.GetElementById("ad-now") is { IsNull: false } now)
            {
                if (nowKey < 0)
                {
                    now.SetInnerRml(state.Playing ? "" : "Stopped");
                }
                else
                {
                    var beat = plan.Grid.BeatAt(state.LocalFrame);
                    var perBar = Math.Max(1, plan.Grid.BeatsPerBar);
                    now.SetInnerRml(RmlText.Escape(string.Create(CultureInfo.InvariantCulture,
                        $"{plan.Sections[state.Section].Name} · bar {beat / perBar + 1}.{beat % perBar + 1}")));
                }
            }
        }

        var pendingKey = live && (uint)state.Pending < (uint)plan.Sections.Length && state.PendingFrames >= 0
            ? Key(state.Pending, state.PendingFrames * 10 / Math.Max(1, plan.Grid.SampleRate))
            : -1;
        if (pendingKey != _shownPending)
        {
            _shownPending = pendingKey;
            if (_document.GetElementById("ad-pending") is { IsNull: false } pending)
                pending.SetInnerRml(pendingKey < 0 ? "" : RmlText.Escape(string.Create(CultureInfo.InvariantCulture,
                    $"Next: {plan.Sections[state.Pending].Name} in {state.PendingFrames / (double)plan.Grid.SampleRate:0.0} s")));
        }

        // The layers' gains (meters), the ghosts of seeking parameters.
        if (!live)
            plan.EvaluateLayers(parameters, _gains, _cutoffs);
        Span<char> id = stackalloc char[16];
        for (var m = 0; m < _meterLayer.Length; m++)
        {
            var layer = _meterLayer[m];
            var db = layer < 0 ? -80f : live ? engine.LayerGainDb(layer) : layer < _gains.Length ? _gains[layer] : -80f;
            var level = (int)MathF.Round(Math.Clamp((db + 60f) / 66f, 0, 1) * 100);
            if (level == _shownMeter[m])
                continue;
            _shownMeter[m] = level;
            if (_document.GetElementById(ElementId(id, "ad-lm-", m)) is { IsNull: false } fill)
                fill.SetProperty("width", Percent[level]);
        }

        for (var g = 0; g < _ghostParam.Length; g++)
        {
            var p = _ghostParam[g];
            if (p >= adaptive.Parameters.Count)
                continue;
            var info = adaptive.Parameters[p];
            var position = live ? (int)MathF.Round(Math.Clamp((engine.ParameterValue(p) - info.Min) / Math.Max(1e-6f, info.Max - info.Min), 0, 1) * 100) : -1;
            if (position == _shownGhost[g])
                continue;
            _shownGhost[g] = position;
            if (_document.GetElementById(ElementId(id, "ad-pg-", p)) is not { IsNull: false } ghost)
                continue;
            ghost.SetClass("hidden", position < 0);
            if (position >= 0)
                ghost.SetProperty("left", Percent[position]);
        }

        // The transition that would fire now from the current (or selected, or start) section.
        var section = live ? state.Section : plan.FindSection(tab.SelectedSection?.Id ?? adaptive.Start ?? "");
        var rule = section >= 0 ? plan.FirstMatchingRule(section, parameters)?.Index ?? -1 : -1;
        if (rule != _shownRule)
        {
            if (_shownRule >= 0 && _document.GetElementById(ElementId(id, "ad-rule-", _shownRule)) is { IsNull: false } previous)
                previous.SetClass("firing", false);
            _shownRule = rule;
            if (rule >= 0 && _document.GetElementById(ElementId(id, "ad-rule-", rule)) is { IsNull: false } firing)
                firing.SetClass("firing", true);
        }
    }

    // Baked mode: the runtime instance's state (its own plan: indices by id).
    private void TickBaked(SongAdaptive adaptive, AdaptiveMusicInstance instance, MusicDirector director)
    {
        var plan = director.Plan;
        var position = instance.GetPosition();
        var section = director.CurrentSection;
        var nowKey = (uint)section < (uint)plan.Sections.Length ? Key(section, position.Bar * 64L + position.Beat) : -1;
        if (nowKey != _shownNow)
        {
            _shownNow = nowKey;
            if (_document.GetElementById("ad-now") is { IsNull: false } now)
                now.SetInnerRml(nowKey < 0 ? "" : RmlText.Escape(string.Create(CultureInfo.InvariantCulture,
                    $"{plan.Sections[section].Name} · bar {position.Bar}.{position.Beat} · baked")));
        }

        var pending = director.PendingSection;
        var pendingKey = (uint)pending < (uint)plan.Sections.Length ? Key(pending, 0) : -1;
        if (pendingKey != _shownPending)
        {
            _shownPending = pendingKey;
            if (_document.GetElementById("ad-pending") is { IsNull: false } next)
                next.SetInnerRml(pendingKey < 0 ? "" : RmlText.Escape("Next: " + plan.Sections[pending].Name));
        }

        Span<char> id = stackalloc char[16];
        for (var m = 0; m < _meterLayer.Length && m < _meterLayerId.Length; m++)
        {
            var layer = plan.FindLayer(_meterLayerId[m]);
            var db = layer >= 0 ? director.LayerGainDb(layer) : -80f;
            var level = (int)MathF.Round(Math.Clamp((db + 60f) / 66f, 0, 1) * 100);
            if (level == _shownMeter[m])
                continue;
            _shownMeter[m] = level;
            if (_document.GetElementById(ElementId(id, "ad-lm-", m)) is { IsNull: false } fill)
                fill.SetProperty("width", Percent[level]);
        }

        for (var g = 0; g < _ghostParam.Length; g++)
        {
            var p = _ghostParam[g];
            if (p >= adaptive.Parameters.Count)
                continue;
            var info = adaptive.Parameters[p];
            var at = (int)MathF.Round(Math.Clamp((instance.GetParameter(info.Name) - info.Min) / Math.Max(1e-6f, info.Max - info.Min), 0, 1) * 100);
            if (at == _shownGhost[g])
                continue;
            _shownGhost[g] = at;
            if (_document.GetElementById(ElementId(id, "ad-pg-", p)) is not { IsNull: false } ghost)
                continue;
            ghost.SetClass("hidden", false);
            ghost.SetProperty("left", Percent[at]);
        }

        var rule = (uint)section < (uint)plan.Sections.Length ? plan.FirstMatchingRule(section, director.Parameters)?.Index ?? -1 : -1;
        if (rule != _shownRule)
        {
            if (_shownRule >= 0 && _document.GetElementById(ElementId(id, "ad-rule-", _shownRule)) is { IsNull: false } previous)
                previous.SetClass("firing", false);
            _shownRule = rule;
            if (rule >= 0 && _document.GetElementById(ElementId(id, "ad-rule-", rule)) is { IsNull: false } firing)
                firing.SetClass("firing", true);
        }
    }

    private static long Key(long a, long b) => a * 1_000_000_000L + b;

    // "{kind}{index}" in the buffer (no allocation).
    private static ReadOnlySpan<char> ElementId(Span<char> buffer, string kind, int index)
    {
        kind.CopyTo(buffer);
        index.TryFormat(buffer[kind.Length..], out var written, provider: CultureInfo.InvariantCulture);
        return buffer[..(kind.Length + written)];
    }

    // ── Input ────────────────────────────────────────────────────────────────────────────────────────────────────

    private static float AuditionValue(SongTab tab, MusicParameterInfo p) => tab.Auditions.TryGetValue(p.Name, out var v) ? v : p.Default;

    private void Audition(SongTab tab, MusicParameterInfo p, float value)
    {
        value = p.Kind == MusicParameterKind.Continuous ? Math.Clamp(value, p.Min, p.Max) : MathF.Round(value);
        if (p.Kind == MusicParameterKind.Labeled)
            value = Math.Clamp(value, 0, Math.Max(0, (p.Labels?.Count ?? 1) - 1));
        else if (p.Kind == MusicParameterKind.Discrete)
            value = Math.Clamp(value, p.Min, p.Max);
        tab.SetAudition(p.Name, value);
        _auditionVersion++;
    }

    /// <summary>A click in the Adaptive tab (true when it was one of its controls).</summary>
    public bool Click(RmlEvent e)
    {
        if (_tab is not { } tab)
            return false;
        var element = FindWith(e.Target, "data-ad");
        if (element.IsNull)
            return false;
        var action = element.GetAttribute("data-ad") ?? "";
        var i = Int(element, "data-i");
        var j = Int(element, "data-j");
        var doc = tab.Document;
        var adaptive = doc.Song.Adaptive;
        var editable = !doc.IsReadOnly;
        try
        {
            switch (action)
            {
                case "make" when editable:
                    doc.EnableAdaptive();
                    break;
                case "timeline" when editable:
                    tab.Player.Stop();
                    doc.DisableAdaptive();
                    break;
                case "play":
                    tab.PlayAdaptive(fromPlayhead: false);
                    _built = false;
                    break;
                case "mode":
                    tab.SetAuditionBaked(j == 1);
                    break;
                case "curve-open" when adaptive is not null && (uint)i < (uint)adaptive.Curves.Count:
                    tab.SelectCurve(adaptive.Curves[i]);
                    workspace.SetMusicPane(SongBottomPanel.Curves);
                    break;
                case "curve-add" when editable && Layer(adaptive, i) is { } curved:
                    CurveMenu(tab, adaptive!, curved, element);
                    break;
                case "rule-edit":
                    _editRule = _editRule == i ? -1 : i;
                    break;
                case "rule-from-toggle" or "rule-to-set" or "rule-mode" or "rule-entry" or "cond-add" or "cond-remove" when editable && Rule(adaptive, i) is { } edited:
                    EditRule(doc, adaptive!, edited, action, j);
                    break;
                case "cond-param" or "cond-op" or "cond-value" or "rule-fadein" or "rule-segment" or "rule-stinger" when editable && Rule(adaptive, i) is { } menued:
                    RuleDetailMenu(doc, adaptive!, menued, action, j, element);
                    break;
                case "bake":
                    workspace.Commands.Execute("song.bake");
                    break;
                case "param-add" when editable && adaptive is not null:
                    var added = doc.AddParameter(new MusicParameterInfo { Name = "intensity", Kind = MusicParameterKind.Continuous, Min = 0, Max = 1 });
                    _editParam = adaptive.Parameters.IndexOf(added);
                    break;
                case "param-reset" when adaptive is not null:
                    foreach (var p in adaptive.Parameters)
                        tab.SetAudition(p.Name, p.Default);
                    tab.Auditions.Clear();
                    _auditionVersion++;
                    break;
                case "param-edit":
                    _editParam = _editParam == i ? -1 : i;
                    break;
                case "param-remove" when editable && Param(adaptive, i) is { } removed:
                    tab.Auditions.Remove(removed.Name);
                    doc.RemoveParameter(removed);
                    _editParam = -1;
                    break;
                case "param-default" when editable && Param(adaptive, i) is { } p:
                    doc.SetParameterDefault(p, AuditionValue(tab, p));
                    break;
                case "param-label" when Param(adaptive, i) is { } labeled:
                    Audition(tab, labeled, j);
                    break;
                case "param-step" when Param(adaptive, i) is { } discrete:
                    Audition(tab, discrete, AuditionValue(tab, discrete) + j);
                    break;
                case "param-kind" when editable && Param(adaptive, i) is { } kinded:
                    doc.EditParameter(kinded, p =>
                    {
                        p.Kind = (MusicParameterKind)j;
                        if (p.Kind == MusicParameterKind.Labeled && p.Labels is not { Count: > 0 })
                            p.Labels = ["Calm", "Combat"];
                    });
                    break;
                case "layer-add" when editable:
                    doc.AddLayer("Layer " + ((adaptive?.Layers.Count ?? 0) + 1).ToString(CultureInfo.InvariantCulture));
                    break;
                case "layer-remove" when editable && Layer(adaptive, i) is { } layer:
                    doc.RemoveLayer(layer);
                    break;
                case "layer-filter" when editable && Layer(adaptive, i) is { } filtered:
                    doc.SetLayerFilter(filtered, filtered.Filter switch
                    {
                        MusicLayerFilter.None => MusicLayerFilter.LowPass,
                        MusicLayerFilter.LowPass => MusicLayerFilter.HighPass,
                        _ => MusicLayerFilter.None,
                    });
                    break;
                case "layer-color" when editable && Layer(adaptive, i) is { } colored:
                    ColorMenu(doc, colored, element);
                    break;
                case "rule-add" when editable && adaptive is not null:
                    AddRule(tab, adaptive);
                    break;
                case "rule-up" when editable && Rule(adaptive, i) is { } up:
                    doc.MoveTransition(up, i - 1);
                    break;
                case "rule-down" when editable && Rule(adaptive, i) is { } down:
                    doc.MoveTransition(down, i + 1);
                    break;
                case "rule-remove" when editable && Rule(adaptive, i) is { } gone:
                    doc.RemoveTransition(gone);
                    break;
                case "rule-from" or "rule-to" or "rule-when" or "rule-quantize" or "rule-fade" when editable && Rule(adaptive, i) is { } rule:
                    RuleMenu(doc, adaptive!, rule, action, element);
                    break;
                case "stinger-fire" when adaptive is not null && i >= 0 && i < adaptive.Sections.Count:
                    tab.TriggerStinger(adaptive.Sections[i].Id);
                    break;
                case "stinger-quantize" or "stinger-pickup" when editable && adaptive is not null && i >= 0 && i < adaptive.Sections.Count:
                    StingerMenu(doc, adaptive.Sections[i], action == "stinger-quantize", element);
                    break;
                case "problem" when adaptive?.FindSection(element.GetAttribute("data-subject")) is { } subject:
                    tab.SelectSection(subject);
                    tab.ArrangeScrollTick = Math.Max(0, subject.Start - tab.Document.Song.TicksPerBar);
                    break;
                default:
                    return true;
            }
        }
        catch (Exception ex) when (EditorCommands.IsRecoverable(ex))
        {
            workspace.Commands.ReportError("Adaptive edit failed", ex);
        }

        return true;
    }

    /// <summary>A slider moved or a field was edited.</summary>
    public bool Change(RmlEvent e)
    {
        if (_tab is not { } tab || tab.Document.Song.Adaptive is not { } adaptive)
            return false;
        var target = e.Target;
        if (target.GetAttribute("data-ad-param") is { } param && Param(adaptive, ParseInt(param)) is { } p)
        {
            if (float.TryParse(e.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                tab.SetAudition(p.Name, value);
                _params?.SetTarget(p.Name, value, immediate: true); // the meters and the matching rule follow at once
                if (_document.GetElementById("ad-pv-" + param) is { IsNull: false } shown)
                    shown.SetInnerRml(RmlText.Escape(ValueText(p, value)));
            }

            return true;
        }

        if ((target.GetAttribute("data-ad-cond") ?? target.GetAttribute("data-ad-rule")) is { } spec && !tab.Document.IsReadOnly)
        {
            var colon = spec.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0 && Rule(adaptive, ParseInt(spec[..colon])) is { } rule &&
                float.TryParse(e.Value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var entered) && float.IsFinite(entered))
            {
                var what = spec[(colon + 1)..];
                try
                {
                    tab.Document.EditTransition(rule, t =>
                    {
                        if (what == "every")
                        {
                            t.Every = Math.Max(1, (int)entered);
                            return;
                        }

                        var list = Conditions(t);
                        var c = ParseInt(what);
                        if ((uint)c < (uint)list.Count)
                            list[c].Value = new MusicValue(entered);
                    });
                }
                catch (Exception ex) when (EditorCommands.IsRecoverable(ex))
                {
                    workspace.Commands.ReportError("Could not edit the transition", ex);
                }
            }

            return true;
        }

        if (target.GetAttribute("data-ad-field") is not { } field || Param(adaptive, Int(target, "data-i")) is not { } edited || tab.Document.IsReadOnly)
            return false;
        var text = e.Value.Trim();
        var number = float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && float.IsFinite(n);
        try
        {
            var oldName = edited.Name;
            tab.Document.EditParameter(edited, p =>
            {
                switch (field)
                {
                    case "name" when text.Length > 0:
                        p.Name = text;
                        break;
                    case "labels":
                        p.Labels = [.. text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
                        break;
                    case "min" when number:
                        p.Min = n;
                        break;
                    case "max" when number:
                        p.Max = n;
                        break;
                    case "default" when number:
                        p.Default = n;
                        break;
                    case "seek" when number:
                        p.SeekSpeed = Math.Max(0, n);
                        break;
                }
            });
            if (field == "name" && tab.Auditions.Remove(oldName, out var kept))
                tab.Auditions[edited.Name] = kept;
        }
        catch (Exception ex) when (EditorCommands.IsRecoverable(ex))
        {
            workspace.Commands.ReportError("Could not edit the parameter", ex);
        }

        return true;
    }

    /// <summary>A double-click: renames a layer.</summary>
    public bool DoubleClick(RmlEvent e)
    {
        if (_tab is not { } tab || tab.Document.IsReadOnly)
            return false;
        var element = FindWith(e.Target, "data-ad-rename");
        if (element.IsNull || Layer(tab.Document.Song.Adaptive, Int(element, "data-i")) is not { } layer)
            return false;
        workspace.Message.Show(new MessageRequest
        {
            Title = "Rename Layer",
            Message = "The layer's name:",
            Input = layer.Name,
            Buttons = ["Rename", "Cancel"],
            DefaultButton = 0,
            CancelButton = 1,
            Callback = (button, text) =>
            {
                if (button == 0 && !string.IsNullOrWhiteSpace(text) && tab.Document.Song.Adaptive?.Layers.Contains(layer) == true)
                    tab.Document.RenameLayer(layer, text.Trim());
            },
        });
        return true;
    }

    private static void AddRule(SongTab tab, SongAdaptive adaptive)
    {
        // From the selected (or first) playable section to the next playable one, at the next bar; the menus refine it.
        var playable = adaptive.Sections.Where(static s => s.Kind == MusicSectionKind.Section).ToList();
        var from = tab.SelectedSection is { Kind: MusicSectionKind.Section } selected ? selected : playable.FirstOrDefault();
        var to = playable.FirstOrDefault(s => !ReferenceEquals(s, from)) ?? from;
        tab.Document.AddTransition(new MusicTransitionInfo
        {
            From = from is null ? [MusicPlan.AnySection] : [from.Id],
            To = to?.Id ?? "",
            Quantize = MusicQuantize.Bar,
        });
    }

    private void RuleMenu(SongDocument doc, SongAdaptive adaptive, MusicTransitionInfo rule, string action, RmlElement anchor)
    {
        var items = new List<MenuItem>();
        switch (action)
        {
            case "rule-from":
                items.Add(new MenuItem("Any section", "*", Css: rule.From.Contains(MusicPlan.AnySection) ? "current" : null));
                foreach (var s in adaptive.Sections.Where(static s => s.Kind == MusicSectionKind.Section))
                    items.Add(new MenuItem(s.Name, s.Id, Css: rule.From.Contains(s.Id) ? "current" : null));
                break;
            case "rule-to":
                foreach (var s in adaptive.Sections.Where(static s => s.Kind == MusicSectionKind.Section))
                    items.Add(new MenuItem(s.Name, s.Id, Css: rule.To == s.Id ? "current" : null));
                break;
            case "rule-when":
                items.Add(new MenuItem("Only from code (GoToSection)", "none"));
                foreach (var p in adaptive.Parameters)
                {
                    items.Add(MenuItem.Header(p.Name));
                    switch (p.Kind)
                    {
                        case MusicParameterKind.Labeled:
                            foreach (var label in p.Labels ?? [])
                                items.Add(new MenuItem($"{p.Name} = {label}", "eq\n" + p.Name + "\n" + label));
                            break;
                        case MusicParameterKind.Discrete:
                            for (var v = (int)p.Min; v <= (int)p.Max && v - (int)p.Min < 16; v++)
                                items.Add(new MenuItem($"{p.Name} = {v}", "eq\n" + p.Name + "\n#" + v.ToString(CultureInfo.InvariantCulture)));
                            break;
                        default:
                            foreach (var f in (ReadOnlySpan<float>)[0.25f, 0.5f, 0.75f])
                            {
                                var v = p.Min + (p.Max - p.Min) * f;
                                items.Add(new MenuItem($"{p.Name} >= {Number(v)}", "ge\n" + p.Name + "\n#" + Number(v)));
                                items.Add(new MenuItem($"{p.Name} < {Number(v)}", "lt\n" + p.Name + "\n#" + Number(v)));
                            }

                            break;
                    }
                }

                break;
            case "rule-quantize":
                foreach (var q in Enum.GetValues<MusicQuantize>())
                {
                    if (q == MusicQuantize.Bars)
                    {
                        foreach (var every in (ReadOnlySpan<int>)[2, 4, 8])
                            items.Add(new MenuItem(QuantizeText(q, every), "bars\n" + every.ToString(CultureInfo.InvariantCulture),
                                Css: rule.Quantize == q && rule.Every == every ? "current" : null));
                    }
                    else
                    {
                        items.Add(new MenuItem(QuantizeText(q), ((int)q).ToString(CultureInfo.InvariantCulture), Css: rule.Quantize == q ? "current" : null));
                    }
                }

                break;
            default: // fade
                items.Add(new MenuItem("Default (½ beat; none at a section end)", "default", Css: rule.FadeOutBeats is null ? "current" : null));
                foreach (var f in FadeChoices)
                    items.Add(new MenuItem(FadeText(float.Parse(f, CultureInfo.InvariantCulture)), f));
                break;
        }

        Show(items, anchor, command =>
        {
            if (!adaptive.Transitions.Contains(rule))
                return;
            doc.EditTransition(rule, t =>
            {
                switch (action)
                {
                    case "rule-from":
                        t.From = [command];
                        break;
                    case "rule-to":
                        t.To = command;
                        break;
                    case "rule-when":
                        t.When = command == "none" ? null : Condition(command);
                        break;
                    case "rule-quantize" when command.StartsWith("bars\n", StringComparison.Ordinal):
                        t.Quantize = MusicQuantize.Bars;
                        t.Every = ParseInt(command[5..]);
                        break;
                    case "rule-quantize":
                        t.Quantize = (MusicQuantize)ParseInt(command);
                        t.Every = null;
                        break;
                    default:
                        t.FadeOutBeats = command == "default" ? null : float.Parse(command, CultureInfo.InvariantCulture);
                        break;
                }
            });
        });
    }

    // The rule's comparison list (All, or Any when the rule uses any-of), made when missing.
    private static List<MusicComparison> Conditions(MusicTransitionInfo t)
    {
        var group = t.When ??= new MusicConditionGroup();
        if (group.Any is { Count: > 0 } && group.All is not { Count: > 0 })
            return group.Any;
        return group.All ??= [];
    }

    private static MusicComparison NewComparison(MusicParameterInfo p) => p.Kind switch
    {
        MusicParameterKind.Labeled => new MusicComparison { Parameter = p.Name, Op = MusicCompare.Equal, Value = new MusicValue(p.Labels is { Count: > 0 } l ? l[0] : "") },
        MusicParameterKind.Discrete => new MusicComparison { Parameter = p.Name, Op = MusicCompare.Equal, Value = new MusicValue(p.Min) },
        _ => new MusicComparison { Parameter = p.Name, Op = MusicCompare.GreaterOrEqual, Value = new MusicValue((p.Min + p.Max) / 2) },
    };

    // The popover's chips that act at once: sources, destination, all/any, entry, conditions added or removed.
    internal static void EditRule(SongDocument doc, SongAdaptive adaptive, MusicTransitionInfo rule, string action, int j)
    {
        var section = (uint)j < (uint)adaptive.Sections.Count ? adaptive.Sections[j].Id : null;
        doc.EditTransition(rule, t =>
        {
            switch (action)
            {
                case "rule-from-toggle" when section is null:
                    t.From = [MusicPlan.AnySection];
                    break;
                case "rule-from-toggle":
                    var from = t.From.Contains(MusicPlan.AnySection) ? [] : new List<string>(t.From);
                    if (!from.Remove(section))
                        from.Add(section);
                    t.From = from.Count == 0 ? [MusicPlan.AnySection] : from;
                    break;
                case "rule-to-set" when section is not null:
                    t.To = section;
                    break;
                case "rule-mode":
                    var list = Conditions(t);
                    t.When = j == 1 ? new MusicConditionGroup { Any = list } : new MusicConditionGroup { All = list };
                    break;
                case "rule-entry":
                    t.Entry = j == 1 ? MusicEntry.Sync : MusicEntry.Start;
                    break;
                case "cond-add" when adaptive.Parameters.Count > 0:
                    Conditions(t).Add(NewComparison(adaptive.Parameters[0]));
                    break;
                case "cond-remove":
                    var conditions = Conditions(t);
                    if ((uint)j < (uint)conditions.Count)
                        conditions.RemoveAt(j);
                    if (conditions.Count == 0)
                        t.When = null;
                    break;
            }
        });
    }

    // The popover's chips that open a menu: a condition's parameter, comparison or value; the fade-in, segment, stinger.
    private void RuleDetailMenu(SongDocument doc, SongAdaptive adaptive, MusicTransitionInfo rule, string action, int j, RmlElement anchor)
    {
        var conditions = rule.When?.Any is { Count: > 0 } && rule.When.All is not { Count: > 0 } ? rule.When.Any : rule.When?.All;
        var cond = conditions is not null && (uint)j < (uint)conditions.Count ? conditions[j] : null;
        var items = new List<MenuItem>();
        switch (action)
        {
            case "cond-param":
                foreach (var p in adaptive.Parameters)
                    items.Add(new MenuItem(p.Name, p.Name, Css: cond?.Parameter == p.Name ? "current" : null));
                break;
            case "cond-op":
                var labeled = adaptive.FindParameter(cond?.Parameter)?.Kind == MusicParameterKind.Labeled;
                foreach (var op in Enum.GetValues<MusicCompare>())
                {
                    if (!labeled || op is MusicCompare.Equal or MusicCompare.NotEqual)
                        items.Add(new MenuItem(Op(op), ((int)op).ToString(CultureInfo.InvariantCulture), Css: cond?.Op == op ? "current" : null));
                }

                break;
            case "cond-value" when adaptive.FindParameter(cond?.Parameter) is { } vp:
                if (vp.Kind == MusicParameterKind.Labeled)
                {
                    foreach (var label in vp.Labels ?? [])
                        items.Add(new MenuItem(label, "L" + label, Css: cond?.Value.Label == label ? "current" : null));
                }
                else
                {
                    for (var v = (int)vp.Min; v <= (int)vp.Max && v - (int)vp.Min < 32; v++)
                        items.Add(new MenuItem(v.ToString(CultureInfo.InvariantCulture), "N" + v.ToString(CultureInfo.InvariantCulture)));
                }

                break;
            case "rule-fadein":
                foreach (var f in FadeChoices)
                    items.Add(new MenuItem(f == "0" ? "No fade-in" : $"Fade in {f} beats", f));
                break;
            case "rule-segment" or "rule-stinger":
                var kind = action == "rule-segment" ? MusicSectionKind.Transition : MusicSectionKind.Stinger;
                items.Add(new MenuItem("None", "-", Css: (action == "rule-segment" ? rule.Segment : rule.Stinger) is null ? "current" : null));
                foreach (var s in adaptive.Sections)
                {
                    if (s.Kind == kind)
                        items.Add(new MenuItem(s.Name, s.Id, Css: (action == "rule-segment" ? rule.Segment : rule.Stinger) == s.Id ? "current" : null));
                }

                if (items.Count == 1)
                    items.Add(new MenuItem(kind == MusicSectionKind.Transition ? "No transition segments: set a section's kind in the Sections lane" : "No stingers yet", null, Enabled: false));
                break;
        }

        Show(items, anchor, command =>
        {
            if (!adaptive.Transitions.Contains(rule))
                return;
            doc.EditTransition(rule, t =>
            {
                var list = Conditions(t);
                var c = (uint)j < (uint)list.Count ? list[j] : null;
                switch (action)
                {
                    case "cond-param" when c is not null && adaptive.FindParameter(command) is { } p:
                        list[j] = NewComparison(p);
                        break;
                    case "cond-op" when c is not null:
                        c.Op = (MusicCompare)ParseInt(command);
                        break;
                    case "cond-value" when c is not null:
                        c.Value = command[0] == 'L' ? new MusicValue(command[1..]) : new MusicValue(ParseInt(command[1..]));
                        break;
                    case "rule-fadein":
                        t.FadeInBeats = float.Parse(command, CultureInfo.InvariantCulture);
                        break;
                    case "rule-segment":
                        t.Segment = command == "-" ? null : command;
                        break;
                    case "rule-stinger":
                        t.Stinger = command == "-" ? null : command;
                        break;
                }
            });
        });
    }

    // + Curve: the layer's volume (or, with a filter, its cutoff) over one of the parameters; then it opens in the Curves pane.
    private void CurveMenu(SongTab tab, SongAdaptive adaptive, SongLayer layer, RmlElement anchor)
    {
        var items = new List<MenuItem>();
        foreach (var p in adaptive.Parameters)
        {
            items.Add(new MenuItem("Volume over " + p.Name, "v" + p.Name, Icon: "wave-sine"));
            if (layer.Filter != MusicLayerFilter.None)
                items.Add(new MenuItem("Cutoff over " + p.Name, "c" + p.Name, Icon: "filter"));
        }

        Show(items, anchor, command =>
        {
            if (adaptive.FindParameter(command[1..]) is not { } p || !adaptive.Layers.Contains(layer))
                return;
            var curve = tab.Document.AddCurve(SongCurves.NewCurve(layer.Id, command[0] == 'c' ? MusicCurveTarget.Cutoff : MusicCurveTarget.Volume, p));
            tab.SelectCurve(curve);
            workspace.SetMusicPane(SongBottomPanel.Curves);
        });
    }

    // "eq\nparam\nLabel" or "ge\nparam\n#0.5": one comparison.
    private static MusicConditionGroup Condition(string command)
    {
        var parts = command.Split('\n');
        var value = parts[2].StartsWith('#') ? new MusicValue(float.Parse(parts[2][1..], CultureInfo.InvariantCulture)) : new MusicValue(parts[2]);
        var op = parts[0] switch { "ge" => MusicCompare.GreaterOrEqual, "lt" => MusicCompare.Less, _ => MusicCompare.Equal };
        return new MusicConditionGroup { All = [new MusicComparison { Parameter = parts[1], Op = op, Value = value }] };
    }

    private void StingerMenu(SongDocument doc, SongSection stinger, bool quantize, RmlElement anchor)
    {
        var items = new List<MenuItem>();
        if (quantize)
        {
            foreach (var q in (ReadOnlySpan<MusicQuantize>)[MusicQuantize.Immediate, MusicQuantize.Beat, MusicQuantize.Bar])
                items.Add(new MenuItem(QuantizeText(q), ((int)q).ToString(CultureInfo.InvariantCulture), Css: (stinger.Quantize ?? MusicQuantize.Beat) == q ? "current" : null));
        }
        else
        {
            foreach (var p in (ReadOnlySpan<string>)["0", "0.5", "1", "2"])
                items.Add(new MenuItem(p == "0" ? "No pickup" : $"Pickup {p} beats", p));
        }

        Show(items, anchor, command =>
        {
            var q = quantize ? (MusicQuantize)ParseInt(command) : stinger.Quantize ?? MusicQuantize.Beat;
            var pickup = quantize ? stinger.PickupBeats : float.Parse(command, CultureInfo.InvariantCulture);
            doc.SetStingerTiming(stinger, q, pickup);
        });
    }

    private void ColorMenu(SongDocument doc, SongLayer layer, RmlElement anchor)
    {
        var items = SongCanvas.Palette.Select(c => new MenuItem(c, c, Css: layer.Color == c ? "current" : null)).ToList();
        Show(items, anchor, command => doc.SetLayerColor(layer, command));
    }

    private void Show(List<MenuItem> items, RmlElement anchor, Action<string> picked)
    {
        var scale = MathF.Max(0.01f, workspace.Host.PixelScale);
        var b = anchor.Bounds;
        workspace.Popup.Show(items, b.X / scale, (b.Y + b.Height) / scale + 2, command =>
        {
            if (command is null)
                return;
            try
            {
                picked(command);
            }
            catch (Exception ex) when (EditorCommands.IsRecoverable(ex))
            {
                workspace.Commands.ReportError("Adaptive edit failed", ex);
            }
        });
    }

    private static MusicParameterInfo? Param(SongAdaptive? adaptive, int i) => adaptive is not null && (uint)i < (uint)adaptive.Parameters.Count ? adaptive.Parameters[i] : null;

    private static SongLayer? Layer(SongAdaptive? adaptive, int i) => adaptive is not null && (uint)i < (uint)adaptive.Layers.Count ? adaptive.Layers[i] : null;

    private static MusicTransitionInfo? Rule(SongAdaptive? adaptive, int i) => adaptive is not null && (uint)i < (uint)adaptive.Transitions.Count ? adaptive.Transitions[i] : null;

    private static RmlElement FindWith(RmlElement element, string attribute)
    {
        for (var el = element; !el.IsNull && el.Id != RootId; el = el.Parent)
        {
            if (el.GetAttribute(attribute) is not null)
                return el;
        }

        return default;
    }

    private static int Int(RmlElement element, string attribute) => element.GetAttribute(attribute) is { } v ? ParseInt(v) : -1;

    private static int ParseInt(string value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : -1;

    private static string Number(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string ValueText(MusicParameterInfo p, float value) => p.Kind switch
    {
        MusicParameterKind.Labeled when p.Labels is { } labels && (int)value >= 0 && (int)value < labels.Count => labels[(int)value],
        MusicParameterKind.Discrete => ((int)MathF.Round(value)).ToString(CultureInfo.InvariantCulture),
        _ => value.ToString("0.00", CultureInfo.InvariantCulture),
    };
}

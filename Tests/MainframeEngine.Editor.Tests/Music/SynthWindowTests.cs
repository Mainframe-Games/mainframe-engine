using System.Numerics;
using MainframeEngine.Editor.Music;

namespace MainframeEngine.Editor.Tests.Music;

/// <summary>The wavetable synth's window in the headless editor: knobs, presets, drag-to-modulate, the envelope / LFO editor, Matrix and FX.</summary>
[Collection(nameof(SerialEditor))]
public sealed class SynthWindowTests
{
    private static (HeadlessEditor Editor, SongTab Tab, SongTrack Track, SynthWindow Window) Open()
    {
        var editor = new HeadlessEditor();
        var path = SongTab.CreateFile(Path.Combine(editor.Directory, "Project", "Content", "Music", "Theme.msong"));
        editor.Workspace.Commands.OpenSong(path);
        editor.Tick(3);
        var tab = Assert.IsType<SongTab>(editor.Workspace.Session.ActiveTab);
        var track = tab.Document.AddTrack(SongTrackKind.Instrument, "Lead");
        tab.Document.SetTrackInstrument(track, InstrumentDescriptor.Wavetable());
        var window = editor.Workspace.SynthWindow;
        window.Open(tab, track);
        editor.Tick(3);
        return (editor, tab, track, window);
    }

    private static string Text(HeadlessEditor editor, string id) =>
        editor.Workspace.SynthWindow.Document.GetElementById(id).InnerRml;

    private static Dictionary<string, float> Params(SongTrack track) => track.Instrument!.Patch!.Params!;

    private static List<SynthModRoute> Routes(SongTrack track) => track.Instrument!.Patch!.Matrix!;

    [Fact]
    public void TheBrowserOpensTheWindowWhichShowsTheSynthAndFollowsEdits()
    {
        using var editor = new HeadlessEditor();
        var path = SongTab.CreateFile(Path.Combine(editor.Directory, "Project", "Content", "Music", "Theme.msong"));
        editor.Workspace.Commands.OpenSong(path);
        editor.Tick(3);
        var tab = Assert.IsType<SongTab>(editor.Workspace.Session.ActiveTab);
        var track = tab.Document.AddTrack(SongTrackKind.Instrument, "Lead");
        Assert.True(editor.Workspace.SongPanel.Browser.Drop("track:" + tab.Document.Song.Tracks.IndexOf(track), "inst:wavetable"));
        var window = editor.Workspace.SynthWindow;
        editor.Tick(3);
        Assert.Same(track.Instrument, window.Instrument);
        Assert.False(editor.Workspace.PluginWindow.IsOpen);
        Assert.Contains(SynthParams.ProductName + " — Lead — Theme", Text(editor, "sw-name"), StringComparison.Ordinal);
        var cutoff = SynthParams.F1.Cutoff;
        Assert.Equal("20.0 kHz", Text(editor, "kv-" + cutoff));
        Assert.Equal("Saw (Basic)", Text(editor, "sw-table-a"));
        Assert.Empty(editor.RmlMessages);

        var steps = tab.Document.History.Position;
        window.BeginKnob(cutoff);
        window.DragKnob(-50, fine: false);
        window.DragKnob(-100, fine: false);
        window.EndDrag();
        editor.Tick(1);
        Assert.True(Params(track)["f1.cutoff"] < 2000);
        Assert.NotEqual("20.0 kHz", Text(editor, "kv-" + cutoff));
        Assert.Equal(steps + 1, tab.Document.History.Position); // one drag, one undo step

        tab.Document.SetTrackInstrument(track, InstrumentDescriptor.Zzfx(SongDocument.DefaultZzfxLine)); // a new instrument closes it
        editor.Tick(1);
        Assert.False(window.IsOpen);
        Assert.False(window.Visible);
    }

    [Fact]
    public void PresetArrowsStepThroughACategoryAndTheTitleNamesThePreset()
    {
        var (editor, tab, track, window) = Open();
        using (editor)
        {
            window.StepPreset(tab, 1); // no preset yet: the first factory one
            editor.Tick(2);
            Assert.Equal("factory:Bass/Sub Bass", track.Instrument!.Preset);
            Assert.Contains("· Sub Bass", Text(editor, "sw-name"), StringComparison.Ordinal);
            window.StepPreset(tab, 1);
            Assert.Equal("factory:Bass/Reese", track.Instrument.Preset);
            window.StepPreset(tab, -1);
            window.StepPreset(tab, -1); // wraps to the category's last
            Assert.Equal("factory:Bass/" + FactoryPresets.All.Last(p => p.Category == "Bass").Name, track.Instrument.Preset);
            editor.Tick(2);
            Assert.Empty(editor.RmlMessages);
        }
    }

    [Fact]
    public void DroppingASourceOnAKnobModulatesItAndAltDragSetsTheAmount()
    {
        var (editor, tab, track, window) = Open();
        using (editor)
        {
            var position = SynthParams.A.Position;
            window.BeginSourceDrag(SynthParams.SourceIndex("lfo1"));
            editor.Tick(1);
            Assert.True(editor.Workspace.SynthWindow.Document.QuerySelector("[data-drop]").IsClassSet("drop-target"));
            window.DropSource(SynthParams.SourceIndex("lfo1"), position);
            editor.Tick(2);
            var route = Assert.Single(Routes(track));
            Assert.Equal(("lfo1", "a.wt_pos", 0.25f), (route.Source, route.Target, route.Amount));
            var dial = editor.Workspace.SynthWindow.Document.GetElementById("km-" + position).Parent;
            Assert.True(dial.IsClassSet("modulated"));
            Assert.Equal("1", Text(editor, "sc-" + SynthParams.SourceIndex("lfo1")));
            Assert.Equal("Matrix 1", Text(editor, "sw-tab-matrix"));

            window.DropSource(SynthParams.SourceIndex("lfo1"), position); // the same source again: no second route
            Assert.Single(Routes(track));

            var steps = tab.Document.History.Position;
            var before = SynthParams.Value(Params(track), position);
            Assert.True(window.BeginRouteDrag(position));
            window.DragKnob(50, fine: false); // a quarter of the travel: +0.25
            window.DragKnob(100, fine: false);
            window.EndDrag();
            Assert.Equal(0.75f, Routes(track)[0].Amount, 3);
            Assert.Equal(before, SynthParams.Value(Params(track), position)); // the knob itself did not move
            Assert.Equal(steps + 1, tab.Document.History.Position);
            Assert.False(window.BeginRouteDrag(SynthParams.F1.Cutoff)); // no route there

            window.RouteAction(tab, track, 0, "invert");
            Assert.Equal(-0.75f, Routes(track)[0].Amount, 3);
            window.RouteAction(tab, track, 0, "bipolar");
            Assert.True(Routes(track)[0].Bipolar);
            window.RouteAction(tab, track, 0, "remove");
            editor.Tick(2);
            Assert.Empty(Routes(track));
            Assert.False(dial.IsClassSet("modulated"));
            Assert.Empty(editor.RmlMessages);
        }
    }

    [Fact]
    public void DraggingAnEnvelopeHandleSetsItsTimeAsOneUndoStep()
    {
        var (editor, tab, track, window) = Open();
        using (editor)
        {
            window.SelectModSource(1); // envelope 2
            editor.Tick(2);
            Assert.Equal("Envelope 2", Text(editor, "sw-mod-title"));
            var canvas = window.ModCanvas;
            canvas.ViewSize = new Vector2(400, 92);
            canvas.Values = Params(track);
            var env = SynthParams.Env[1];
            var attackBefore = SynthParams.Value(Params(track), env.Attack);
            var (attack, _, _, attackStart, _, _) = canvas.EnvelopeHandles();
            var steps = tab.Document.History.Position;

            window.BeginModHandle(attack, bend: false);
            window.DragModHandle(attack + new Vector2(20, 0), 0);
            window.DragModHandle(attack + new Vector2(40, 0), 0);
            window.EndDrag();
            var attackAfter = SynthParams.Value(Params(track), env.Attack);
            Assert.True(attackAfter > attackBefore, $"{attackAfter} > {attackBefore}");
            Assert.Equal(canvas.SegmentSeconds(attack.X + 40 - attackStart), attackAfter, 3);
            Assert.Equal(steps + 1, tab.Document.History.Position);
            tab.Document.History.Undo();
            Assert.Equal(attackBefore, SynthParams.Value(Params(track), env.Attack), 4);
        }
    }

    [Fact]
    public void DrawingAnLfoStartsFromItsShapeAndClicksAddPoints()
    {
        var (editor, tab, track, window) = Open();
        using (editor)
        {
            window.SelectModSource(4); // LFO 1
            editor.Tick(2);
            Assert.Equal("LFO 1", Text(editor, "sw-mod-title"));
            Assert.False(editor.Workspace.SynthWindow.Document.GetElementById("sw-draw").IsNull);
            window.StartDrawing(tab, track);
            var shape = SynthParams.Lfo[0].Shape;
            Assert.Equal((float)SynthLfoWave.Drawn, Params(track)[SynthParams.All[shape].Id]);
            var points = track.Instrument!.Patch!.LfoShapes![0]!.Points;
            Assert.Equal(9, points.Count);
            Assert.Equal(0.5f, points[0][1], 2); // a sine starts in the middle

            editor.Tick(2);
            var canvas = window.ModCanvas;
            canvas.ViewSize = new Vector2(400, 92);
            window.BeginModHandle(new Vector2(0.53f * 400, 30), bend: false); // between two points: a new one
            window.EndDrag();
            Assert.Equal(10, track.Instrument.Patch.LfoShapes[0]!.Points.Count);

            tab.Document.History.Undo();
            tab.Document.History.Undo();
            Assert.Equal(0, Params(track).GetValueOrDefault(SynthParams.All[shape].Id));
            editor.Tick(2);
            Assert.Empty(editor.RmlMessages);
        }
    }

    [Fact]
    public void TheMatrixAndFxTabsListAndEditTheirItems()
    {
        var (editor, tab, track, window) = Open();
        using (editor)
        {
            tab.Document.AddModRoute(track, new SynthModRoute { Source = "env2", Target = "f1.cutoff", Amount = 0.5f });
            var chorus = tab.Document.AddSynthFx(track, "chorus");
            tab.Document.AddSynthFx(track, "reverb");
            window.ShowPage("matrix");
            editor.Tick(2);
            Assert.Equal("matrix", window.Page);
            var document = editor.Workspace.SynthWindow.Document;
            Assert.True(document.GetElementById("sw-page-osc").IsClassSet("hidden"));
            Assert.False(document.GetElementById("sw-page-matrix").IsClassSet("hidden"));
            Assert.Contains("Filter 1 · Cutoff", Text(editor, "sw-matrix"), StringComparison.Ordinal);
            Assert.Equal("FX 2", Text(editor, "sw-tab-fx"));

            window.ShowPage("fx");
            editor.Tick(2);
            Assert.Contains("Chorus", Text(editor, "sw-fx"), StringComparison.Ordinal);
            window.FxAction(tab, track, 1, "up");
            Assert.Equal("reverb", track.Instrument!.Patch!.Fx![0].Builtin);
            window.FxAction(tab, track, 1, "bypass");
            Assert.True(chorus.Bypass);
            window.FxAction(tab, track, 1, "open"); // the synth's effect in the plugin window
            editor.Tick(2);
            Assert.Same(chorus, editor.Workspace.PluginWindow.Insert);
            Assert.Contains("Chorus — Lead — " + SynthParams.ProductName, editor.Workspace.PluginWindow.Document.GetElementById("pw-name").InnerRml, StringComparison.Ordinal);
            window.FxAction(tab, track, 1, "remove");
            editor.Tick(2);
            Assert.False(editor.Workspace.PluginWindow.IsOpen); // its effect left the chain
            Assert.Single(track.Instrument.Patch.Fx);
            Assert.Empty(editor.RmlMessages);
        }
    }

    [Fact]
    public void TheFxTabEditsTheSelectedEffectInItsPanel()
    {
        var (editor, tab, track, window) = Open();
        using (editor)
        {
            var eq = tab.Document.AddSynthFx(track, "eq");
            var compressor = tab.Document.AddSynthFx(track, "compressor");
            window.ShowPage("fx");
            editor.Tick(3);
            var document = editor.Workspace.SynthWindow.Document;
            Assert.Same(eq, window.SelectedFx); // the first effect
            Assert.Equal("Channel EQ", Text(editor, "sx-name"));
            Assert.False(document.GetElementById("sx-graph").IsClassSet("hidden"));
            Assert.True(document.GetElementById("sx-none").IsClassSet("hidden"));
            Assert.False(document.QuerySelector(".fx-row.selected").IsNull);
            Assert.Empty(editor.RmlMessages);

            var panel = window.FxPanel;
            var steps = tab.Document.History.Position;
            panel.BeginKnob(EqEffect.Definition.IndexOf("peak2Gain"));
            panel.DragKnob(25, fine: false);
            panel.DragKnob(50, fine: false);
            panel.EndDrag();
            Assert.Equal(48f * 50 / 200, eq.Params!["peak2Gain"], 2);
            Assert.Equal(steps + 1, tab.Document.History.Position); // one drag, one undo step
            editor.Tick(1);
            Assert.Equal("+12.0 dB", Text(editor, "fkv-" + EqEffect.Definition.IndexOf("peak2Gain")));

            var graph = panel.Graph!;
            Assert.True(graph.ViewSize.X > 100, $"the curve is laid out ({graph.ViewSize})");
            panel.BeginBand(3); // Band 2
            panel.DragBand(new Vector2(graph.X(3000), graph.Y(-9)));
            panel.EndDrag();
            Assert.InRange(eq.Params["peak2Freq"], 2950, 3050);
            Assert.Equal(-9, eq.Params["peak2Gain"], 1);

            window.FxAction(tab, track, 1, "select");
            editor.Tick(2);
            Assert.Same(compressor, window.SelectedFx);
            Assert.Equal("Compressor", Text(editor, "sx-name"));
            Assert.True(document.GetElementById("sx-graph").IsClassSet("hidden"));
            Assert.False(document.GetElementById("sx-meter").IsClassSet("hidden"));

            steps = tab.Document.History.Position;
            window.MoveFx(1, 0); // the grip's drag: the compressor first
            Assert.Same(compressor, track.Instrument!.Patch!.Fx![0]);
            Assert.Equal(steps + 1, tab.Document.History.Position);
            Assert.NotNull(tab.Player.FindBuiltin(compressor)); // its meter reads the synth's running compressor

            window.FxAction(tab, track, 0, "remove");
            window.FxAction(tab, track, 0, "remove");
            editor.Tick(2);
            Assert.Null(window.SelectedFx);
            Assert.False(document.GetElementById("sx-none").IsClassSet("hidden"));
            Assert.Empty(editor.RmlMessages);
        }
    }

    [Fact]
    public void HoveringASourceTileMarksTheKnobsItModulates()
    {
        var (editor, tab, track, window) = Open();
        using (editor)
        {
            tab.Document.AddModRoute(track, new SynthModRoute { Source = "env2", Target = "f1.cutoff", Amount = 0.5f });
            editor.Tick(2);
            var dial = editor.Workspace.SynthWindow.Document.GetElementById("km-" + SynthParams.F1.Cutoff).Parent;
            window.HighlightSource(SynthParams.SourceIndex("env2"));
            Assert.True(dial.IsClassSet("source-target"));
            window.HighlightSource(SynthParams.SourceIndex("lfo1"));
            Assert.False(dial.IsClassSet("source-target"));
            window.HighlightSource(-1);
            Assert.False(dial.IsClassSet("source-target"));
        }
    }

    [Fact]
    public void TheKeyboardPlaysTheTrackAndLeavingMusicModeCloses()
    {
        var (editor, tab, track, window) = Open();
        using (editor)
        {
            window.PressKey(60);
            editor.Tick(2);
            window.ReleaseKey();
            Assert.NotNull(tab.Player.FindInstrument(track) as WavetableSynth);
            editor.Workspace.Session.NewScene();
            editor.Tick(2);
            Assert.False(window.IsOpen);
        }
    }
}

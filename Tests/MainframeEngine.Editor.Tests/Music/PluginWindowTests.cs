using MainframeEngine.Editor.Music;

namespace MainframeEngine.Editor.Tests.Music;

/// <summary>The built-in effect window in the headless editor: opening, following edits and undo, closing with its insert.</summary>
[Collection(nameof(SerialEditor))]
public sealed class PluginWindowTests
{
    private static (HeadlessEditor Editor, SongTab Tab, SongTrack Track) Open()
    {
        var editor = new HeadlessEditor();
        var path = SongTab.CreateFile(Path.Combine(editor.Directory, "Project", "Content", "Music", "Theme.msong"));
        editor.Workspace.Commands.OpenSong(path);
        editor.Tick(3);
        var tab = Assert.IsType<SongTab>(editor.Workspace.Session.ActiveTab);
        var track = tab.Document.AddTrack(SongTrackKind.Instrument, "Lead");
        return (editor, tab, track);
    }

    private static string Text(HeadlessEditor editor, string id) =>
        editor.Workspace.PluginWindow.Document.GetElementById(id).InnerRml;

    [Fact]
    public void TheWindowShowsTheEffectsParametersAndFollowsEditsAndUndo()
    {
        var (editor, tab, track) = Open();
        using (editor)
        {
            var insert = tab.Document.AddBuiltinInsert(track, "compressor");
            var window = editor.Workspace.PluginWindow;
            window.Open(tab, track, insert);
            editor.Tick(2);
            Assert.True(window.Visible);
            Assert.Equal("compressor", window.Info!.Id);
            Assert.Contains("Compressor — Lead — Theme", Text(editor, "pw-name"), StringComparison.Ordinal);
            Assert.Equal("-18.0 dB", Text(editor, "kv-0"));
            Assert.Empty(editor.RmlMessages);

            tab.Document.SetInsertParam(insert, "threshold", -24);
            editor.Tick(1);
            Assert.Equal("-24.0 dB", Text(editor, "kv-0"));
            tab.Document.History.Undo();
            editor.Tick(1);
            Assert.Equal("-18.0 dB", Text(editor, "kv-0"));

            tab.Document.History.Undo(); // the insert goes: so does its window
            editor.Tick(1);
            Assert.False(window.IsOpen);
            Assert.False(window.Visible);
        }
    }

    [Fact]
    public void TheEqWindowDrawsItsCurveAndTheDelayShowsItsTime()
    {
        var (editor, tab, track) = Open();
        using (editor)
        {
            var window = editor.Workspace.PluginWindow;
            window.Open(tab, track, tab.Document.AddBuiltinInsert(track, "eq"));
            editor.Tick(3);
            Assert.False(editor.Workspace.PluginWindow.Document.GetElementById("pw-graph").IsClassSet("hidden"));
            Assert.Empty(editor.RmlMessages);

            window.Open(tab, null, tab.Document.AddBuiltinInsert(null, "delay"));
            editor.Tick(2);
            Assert.Equal("1/8 dotted · 375.0 ms at 120 BPM", Text(editor, "pw-readout"));
            tab.Document.SetTempo(100);
            editor.Tick(1);
            Assert.Equal("1/8 dotted · 450.0 ms at 100 BPM", Text(editor, "pw-readout"));
        }
    }

    [Fact]
    public void LeavingMusicModeClosesTheWindow()
    {
        var (editor, tab, track) = Open();
        using (editor)
        {
            var window = editor.Workspace.PluginWindow;
            window.Open(tab, track, tab.Document.AddBuiltinInsert(track, "reverb"));
            editor.Tick(2);
            Assert.True(window.IsOpen);
            editor.Workspace.Session.NewScene();
            editor.Tick(2);
            Assert.False(window.IsOpen);
        }
    }

    [Fact]
    public void AKnobDragIsOneUndoStepAndAHandleDragMovesItsBand()
    {
        var (editor, tab, track) = Open();
        using (editor)
        {
            var window = editor.Workspace.PluginWindow;
            var eq = tab.Document.AddBuiltinInsert(track, "eq");
            window.Open(tab, track, eq);
            editor.Tick(3);
            var steps = tab.Document.History.Position;

            var gain = EqEffect.Definition.IndexOf("peak2Gain");
            window.BeginKnob(gain);
            for (var dp = 10; dp <= 50; dp += 10)
                window.DragKnob(dp, fine: false);
            window.EndDrag();
            Assert.Equal(48f * 50 / 200, eq.Params!["peak2Gain"], 2); // a quarter of the way up a ±24 dB knob
            Assert.Equal(steps + 1, tab.Document.History.Position);

            window.BeginBand(3); // Band 2
            var graph = window.Graph!;
            window.DragBand(new System.Numerics.Vector2(graph.X(2000), graph.Y(-6)));
            window.DragBand(new System.Numerics.Vector2(graph.X(3000), graph.Y(-9)));
            window.EndDrag();
            Assert.InRange(eq.Params["peak2Freq"], 2950, 3050);
            Assert.Equal(-9, eq.Params["peak2Gain"], 1);
            Assert.Equal(steps + 2, tab.Document.History.Position);
            tab.Document.History.Undo();
            Assert.Equal(800, eq.Params["peak2Freq"]);
        }
    }

    [Fact]
    public void TheSynthsParametersShowInTheWindowAndFollowEdits()
    {
        var (editor, tab, track) = Open();
        using (editor)
        {
            Assert.True(editor.Workspace.SongPanel.Browser.Drop("track:" + tab.Document.Song.Tracks.IndexOf(track), "inst:wavetable"));
            var window = editor.Workspace.PluginWindow;
            editor.Tick(3);
            Assert.Same(track.Instrument, window.Instrument);
            Assert.Contains(SynthParams.ProductName + " — Lead — Theme", Text(editor, "pw-name"), StringComparison.Ordinal);
            var cutoff = SynthParams.F1.Cutoff;
            Assert.Equal("20.0 kHz", Text(editor, "kv-" + cutoff));
            Assert.Empty(editor.RmlMessages);

            window.BeginKnob(cutoff);
            window.DragKnob(-100, fine: false);
            window.EndDrag();
            editor.Tick(1);
            Assert.True(track.Instrument!.Patch!.Params!["f1.cutoff"] < 2000);
            Assert.NotEqual("20.0 kHz", Text(editor, "kv-" + cutoff));

            tab.Document.SetTrackInstrument(track, InstrumentDescriptor.Zzfx(SongDocument.DefaultZzfxLine)); // a new instrument closes it
            editor.Tick(1);
            Assert.False(window.IsOpen);
        }
    }

    [Fact]
    public void PresetArrowsStepThroughACategoryAndTheTitleNamesThePreset()
    {
        var (editor, tab, track) = Open();
        using (editor)
        {
            tab.Document.SetTrackInstrument(track, InstrumentDescriptor.Wavetable());
            var window = editor.Workspace.PluginWindow;
            window.OpenSynth(tab, track);
            editor.Tick(2);
            window.StepPreset(tab, 1); // no preset yet: the first factory one
            editor.Tick(2);
            Assert.Equal("factory:Bass/Sub Bass", track.Instrument!.Preset);
            Assert.Contains("· Sub Bass", Text(editor, "pw-name"), StringComparison.Ordinal);
            window.StepPreset(tab, 1);
            Assert.Equal("factory:Bass/Reese", track.Instrument.Preset);
            window.StepPreset(tab, -1);
            window.StepPreset(tab, -1); // wraps to the category's last
            Assert.Equal("factory:Bass/" + FactoryPresets.All.Last(p => p.Category == "Bass").Name, track.Instrument.Preset);
            editor.Tick(2);
            Assert.Equal("A: Saw (Basic)", Text(editor, "pw-table-a"));
            Assert.Empty(editor.RmlMessages);
        }
    }
}

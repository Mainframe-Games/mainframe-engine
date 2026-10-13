namespace MainframeEngine.Editor.Tests.Music;

/// <summary>
/// Editor Settings › MIDI in the headless editor: the open dialog redraws its device list when the workspace's MIDI
/// service reports a change (the workspace ticks the dialog; it is not a <c>[Tool]</c> node, so it has no <c>OnProcess</c>).
/// </summary>
[Collection(nameof(SerialEditor))]
public sealed class MidiSettingsDialogTests : IDisposable
{
    private readonly FakePluginHost _host = new();
    private readonly HeadlessEditor _editor;

    public MidiSettingsDialogTests()
    {
        _editor = new HeadlessEditor(configure: o => o with { MidiHost = () => _host });
    }

    public void Dispose() => _editor.Dispose();

    private string MidiList() => _editor.Workspace.EditorSettingsDialog.Document.GetElementById("es-midi").InnerRml;

    [Fact]
    public void ADevicePluggedInWhileTheDialogIsOpenShowsUpAfterATick()
    {
        var w = _editor.Workspace;
        _host.PlugMidi("Keys");
        w.Midi.Refresh();
        w.EditorSettingsDialog.Open();
        _editor.Tick();
        Assert.Contains("Keys", MidiList(), StringComparison.Ordinal);
        Assert.DoesNotContain("Pads", MidiList(), StringComparison.Ordinal);

        _host.PlugMidi("Pads"); // announced like the helper's poll: the service takes it in its next Update
        _editor.Tick();
        Assert.Contains("Pads", MidiList(), StringComparison.Ordinal);

        _host.UnplugMidi("Pads");
        _editor.Tick();
        Assert.Contains("offline", MidiList(), StringComparison.Ordinal);
        Assert.Empty(_editor.RmlMessages);
    }
}

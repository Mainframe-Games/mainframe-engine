using System.Diagnostics;
using MainframeEngine.Editor.Music;
using static MainframeEngine.Editor.Tests.Music.MusicTestUtil;

namespace MainframeEngine.Editor.Tests.Music;

/// <summary>
/// MIDI keyboard input (ADR 0148): live notes reach the armed/selected track's instrument, recordings land at
/// latency-compensated positions, loop passes overdub into one clip, the sustain pedal holds lengths, input quantize,
/// one undo entry per take, device unplug/replug through the fake host; the real helper with a virtual keyboard.
/// </summary>
public sealed class MidiInputTests : IDisposable
{
    private const double TicksPerSecond = 120 * 960 / 60.0; // 1920
    private readonly string _root = Directory.CreateTempSubdirectory("mf-midi-").FullName;
    private readonly RecordingInstrument _a = new();
    private readonly RecordingInstrument _b = new();
    private readonly SongDocument _doc;
    private readonly SongPlayer _player;
    private readonly SongMidiInput _input;
    private SongTrack? _selected;
    private long _clock = Stopwatch.Frequency * 100;
    private double _position;

    public MidiInputTests()
    {
        var song = NewSong();
        AddInstrumentTrack(song, "a", InstrumentDescriptor.Zzfx(SongDocument.DefaultZzfxLine));
        AddInstrumentTrack(song, "b", InstrumentDescriptor.Zzfx(SongDocument.DefaultZzfxLine));
        _doc = new SongDocument(song, Path.Combine(_root, "Midi.msong"));
        _player = new SongPlayer(null, (track, _) => track.Id == "a" ? _a : _b);
        _player.Update(_doc);
        Run(_player.Engine, 1); // the snapshot is taken at a block start
        _selected = song.Tracks[0];
        _input = new SongMidiInput(_doc, _player, () => _selected)
        {
            Clock = () => _clock,
            Position = () => _position,
            DeviceLatencySeconds = 0,
        };
    }

    public void Dispose()
    {
        _player.Dispose();
        _doc.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private SongTrack A => _doc.Song.Tracks[0];
    private SongTrack B => _doc.Song.Tracks[1];

    private void Send(byte status, byte data1, byte data2, double secondsAgo = 0) =>
        _input.OnMidi(new MidiInputEvent(1, _clock - (long)(secondsAgo * Stopwatch.Frequency), status, data1, data2));

    private void At(double tick)
    {
        _position = tick;
        _input.Update();
    }

    private List<(int Pitch, bool On)> Heard(RecordingInstrument instrument)
    {
        Run(_player.Engine, 1);
        var events = instrument.Events.Select(e => (e.Pitch, e.On)).ToList();
        instrument.Events.Clear();
        return events;
    }

    [Fact]
    public void LiveNotesPlayTheArmedTrackElseTheSelectedOne()
    {
        Send(0x90, 60, 100);
        Assert.Equal([(60, true)], Heard(_a));
        Send(0x80, 60, 0);
        Assert.Equal([(60, false)], Heard(_a));

        _input.ToggleArm(B); // armed wins over selected
        Send(0x91, 62, 90);
        Send(0x91, 62, 0); // note-on velocity 0 is a note-off
        Assert.Equal([(62, true), (62, false)], Heard(_b));
        Assert.Empty(Heard(_a));
        Send(0xB0, 1, 64); // modulation: ignored live (no controller API)
        Assert.Empty(Heard(_b));
    }

    [Fact]
    public void TheSustainPedalHoldsNoteOffsUntilItIsReleased()
    {
        Send(0xB0, 64, 127);
        Send(0x90, 60, 100);
        Send(0x80, 60, 0);
        Assert.Equal([(60, true)], Heard(_a));
        Send(0xB0, 64, 0);
        Assert.Equal([(60, false)], Heard(_a));
    }

    [Fact]
    public void RecordedNotesLandWhereTheyWereHeard()
    {
        _input.DeviceLatencySeconds = 0.01;
        Assert.Null(_input.StartRecording());
        Assert.Equal("a", _input.ArmedTrackId); // the selected track was armed
        _position = 1920;
        Send(0x90, 60, 100, secondsAgo: 0.05); // heard at 1920 - (0.05 + 0.01) * 1920 = 1804.8
        Send(0x80, 60, 0); // 1920 - 19.2 = 1900.8
        _position = 3840;
        _input.StopRecording();

        var clip = Assert.IsType<MidiClip>(Assert.Single(A.Clips));
        Assert.Equal(0, clip.Start);
        Assert.Equal(3840, clip.Length);
        var note = Assert.Single(clip.Notes);
        Assert.Equal((60, 1805L, 96L, 100), (note.Pitch, note.Start, note.Length, note.Velocity));
        Assert.Equal("Record", Assert.Single(_doc.History.Actions).Name);
        _doc.History.Undo();
        Assert.Empty(A.Clips);
        _doc.History.Redo();
        Assert.Single(Assert.IsType<MidiClip>(Assert.Single(A.Clips)).Notes);
    }

    [Fact]
    public void LoopPassesOverdubIntoOneClipAndOneUndoEntry()
    {
        _doc.SetLoop(true, 0, 3840);
        var before = _doc.History.Actions.Count;
        _input.ToggleArm(A);
        _input.StartRecording();
        At(960);
        Send(0x90, 60, 100);
        At(1440);
        Send(0x80, 60, 0);
        At(3700);
        Send(0x90, 67, 80); // held across the wrap
        At(100); // wrapped: pass 1 is written so it plays on pass 2
        var clip = Assert.IsType<MidiClip>(Assert.Single(A.Clips));
        Assert.Single(clip.Notes);
        Send(0x80, 67, 0); // after the wrap: held to the loop's end
        At(1920);
        Send(0x90, 64, 100);
        At(2400);
        Send(0x80, 64, 0);
        At(3000);
        _input.ToggleRecording();

        Assert.Same(clip, Assert.Single(A.Clips));
        Assert.Equal((0L, 3840L), (clip.Start, clip.Length));
        Assert.Equal([(60, 960L, 480L), (67, 3700L, 140L), (64, 1920L, 480L)], clip.Notes.Select(n => (n.Pitch, n.Start, n.Length)));
        Assert.Equal(before + 1, _doc.History.Actions.Count);
        _doc.History.Undo();
        Assert.Empty(A.Clips);
    }

    [Fact]
    public void SustainExtendsRecordedLengthsAndQuantizeSnapsStarts()
    {
        _input.Quantize = MidiQuantize.Sixteenth;
        _input.StartRecording();
        _position = 1000; // quantized to 960
        Send(0xB0, 64, 127);
        Send(0x90, 60, 100);
        _position = 1200;
        Send(0x80, 60, 0);
        _position = 1960;
        Send(0xB0, 64, 0); // the pedal ends it: 960 ticks long
        _input.Quantize = MidiQuantize.Eighth;
        _position = 2200; // quantized to 2400
        Send(0x90, 62, 100);
        _position = 2300;
        Send(0x80, 62, 0);
        _input.StopRecording();

        var clip = Assert.IsType<MidiClip>(Assert.Single(A.Clips));
        Assert.Equal([(60, 960L, 960L), (62, 2400L, 100L)], clip.Notes.Select(n => (n.Pitch, n.Start, n.Length)));
    }

    [Fact]
    public void ATakeWithoutNotesLeavesNoHistory()
    {
        _input.StartRecording();
        _position = 4000;
        _input.StopRecording();
        Assert.Empty(A.Clips);
        Assert.Empty(_doc.History.Actions);
        _selected = null;
        _input.ToggleArm(A); // disarms (the take armed it)
        Assert.Null(_input.ArmedTrackId);
        Assert.NotNull(_input.StartRecording()); // nothing armed or selected
    }

    private sealed class Sink : IMidiInputSink
    {
        public readonly List<MidiInputEvent> Events = [];

        public void OnMidi(in MidiInputEvent midiEvent) => Events.Add(midiEvent);
    }

    [Fact]
    public void EnabledDevicesOpenGoOfflineWhenUnpluggedAndReopenWhenBack()
    {
        var host = new FakePluginHost();
        var keys = host.PlugMidi("Keys");
        var pads = host.PlugMidi("Pads");
        using var service = new MidiInputService(() => host);
        var sink = new Sink();
        service.Sink = sink;
        service.Update();
        Assert.Equal(0, host.Starts); // nothing enabled: no helper

        service.SetEnabled(["Keys"]);
        service.Update();
        Assert.Equal(new[] { keys }, host.OpenMidi);
        Assert.Contains(service.Devices, d => d is { Name: "Keys", Online: true, Enabled: true, Open: true });
        Assert.Contains(service.Devices, d => d is { Name: "Pads", Enabled: false, Open: false });
        host.SendMidi(keys, 5, 0x90, 60, 100);
        host.SendMidi(pads, 5, 0x90, 61, 100); // not open: never delivered
        service.Update();
        Assert.Equal(60, Assert.Single(sink.Events).Data1);

        host.UnplugMidi("Keys");
        service.Update();
        Assert.Contains(service.Devices, d => d is { Name: "Keys", Online: false, Open: false });
        host.SendMidi(keys, 6, 0x90, 62, 100);
        service.Update();
        Assert.Single(sink.Events);

        host.PlugMidi("Keys");
        service.Update();
        Assert.Contains(service.Devices, d => d is { Name: "Keys", Online: true, Open: true });
        host.SendMidi(keys, 7, 0x90, 64, 100);
        service.Update();
        Assert.Equal(64, sink.Events[^1].Data1);

        service.SetEnabled([]);
        service.Update();
        Assert.Empty(host.OpenMidi);
    }

    [Fact]
    public void TheRealHelperDeliversAVirtualKeyboardsNotes()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows has no virtual MIDI ports.");
        var helper = PluginHostClient.Locate();
        Assert.SkipWhen(helper is null, $"No {PluginHostClient.ExecutableName} for this platform yet (natives.yml builds it).");
        using var client = new PluginHostClient(helper!);
        Assert.SkipWhen(!client.Start().Capabilities.HasFlag(PluginHostCapabilities.Midi), "This mfplughost has no MIDI input (built before it, or no ALSA).");

        var name = "mf-test-" + Environment.ProcessId;
        using var source = Process.Start(new ProcessStartInfo(helper!, ["--midi-test-source", name, "10"]) { UseShellExecute = false })!;
        try
        {
            MidiInputDevice? device = null;
            var until = DateTime.UtcNow.AddSeconds(5);
            while (device is null && DateTime.UtcNow < until)
            {
                device = client.ListMidiInputs().Where(d => d.Name.Contains(name, StringComparison.Ordinal) && d.Online).Cast<MidiInputDevice?>().FirstOrDefault();
                if (device is null)
                    Thread.Sleep(100);
            }

            Assert.SkipWhen(device is null, "The virtual MIDI port did not appear (no MIDI service on this machine).");
            client.OpenMidiInput(device!.Value.Id);
            MidiInputEvent e = default;
            var got = false;
            until = DateTime.UtcNow.AddSeconds(5);
            while (!got && DateTime.UtcNow < until)
            {
                got = client.TryTakeMidiEvent(out e) && e.IsNoteOn;
                if (!got)
                    Thread.Sleep(5);
            }

            Assert.True(got, "no note from the virtual keyboard");
            Assert.Equal((device.Value.Id, 60, 100), (e.Device, (int)e.Data1, (int)e.Data2));
            var age = (Stopwatch.GetTimestamp() - e.Timestamp) / (double)Stopwatch.Frequency;
            Assert.InRange(age, -0.05, 1.0); // the helper's clock is mapped onto the editor's
        }
        finally
        {
            source.Kill();
        }
    }
}

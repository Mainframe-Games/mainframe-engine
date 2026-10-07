namespace MainframeEngine.Editor.Music;

/// <summary>Receives MIDI messages on the UI thread (the active song tab's <see cref="SongMidiInput"/>).</summary>
public interface IMidiInputSink
{
    void OnMidi(in MidiInputEvent midiEvent);
}

/// <summary>A MIDI input as Editor Settings › MIDI shows it.</summary>
/// <param name="Name">The port name (what the settings remember).</param>
/// <param name="Online">Plugged in.</param>
/// <param name="Enabled">Listened to (Editor Settings).</param>
/// <param name="Open">Open in the helper now.</param>
public sealed record MidiDeviceState(uint Id, string Name, bool Online, bool Enabled, bool Open);

/// <summary>
/// The editor's MIDI keyboards (ADR 0148), owned by the workspace. RtMidi runs in a helper process of its own
/// (<c>mfplughost</c>, started on first need: a device is enabled or the settings list them); the enabled devices (by
/// port name, <see cref="EditorSettings.MidiInputs"/>) stay open, are marked offline when unplugged and are opened again
/// when they come back (the helper polls its port list about once a second). <see cref="Update"/> runs every UI frame:
/// it applies device changes and hands each message to <see cref="Sink"/> (the active song tab). Messages carry the
/// helper's timestamp mapped to the editor's clock, so the frame they are read on does not move recorded notes.
/// </summary>
public sealed class MidiInputService : IDisposable
{
    private readonly Func<IPluginHost?> _hostFactory;
    private readonly HashSet<string> _enabled = new(StringComparer.Ordinal);
    private readonly List<MidiDeviceState> _devices = [];
    private readonly HashSet<uint> _open = [];
    private IPluginHost? _host;
    private bool _startFailed;
    private bool _needsList;
    private bool _disposed;

    public MidiInputService(Func<IPluginHost?> hostFactory)
    {
        _hostFactory = hostFactory ?? throw new ArgumentNullException(nameof(hostFactory));
    }

    /// <summary>Every device seen since the helper started (offline ones included).</summary>
    public IReadOnlyList<MidiDeviceState> Devices => _devices;

    /// <summary>Why MIDI input is unavailable (no helper, a helper without MIDI, it failed to start), or null.</summary>
    public string? Error { get; private set; }

    /// <summary>Where messages go (UI thread); null drops them.</summary>
    public IMidiInputSink? Sink { get; set; }

    /// <summary>Bumped whenever <see cref="Devices"/> or <see cref="Error"/> changed (the settings dialog redraws).</summary>
    public int Version { get; private set; }

    /// <summary>Sets the enabled devices (port names) and opens/closes them; starts the helper if any is enabled.</summary>
    public void SetEnabled(IEnumerable<string> names)
    {
        _enabled.Clear();
        foreach (var name in names)
            _enabled.Add(name);
        _startFailed = false;
        if (_enabled.Count > 0 || _host is not null)
            _needsList = true;
    }

    /// <summary>Lists the devices now (starts the helper): Editor Settings opening.</summary>
    public void Refresh()
    {
        _startFailed = false;
        _needsList = true;
        Update();
    }

    /// <summary>UI thread, every frame.</summary>
    public void Update()
    {
        if (_disposed)
            return;
        if (_needsList)
        {
            _needsList = false;
            List();
        }

        if (_host is not { IsRunning: true } host)
            return;
        try
        {
            if (host.TryTakeMidiDevices(out var devices))
                Apply(devices);
            while (host.TryTakeMidiEvent(out var e))
            {
                if (_open.Contains(e.Device))
                    Sink?.OnMidi(e);
            }
        }
        catch (PluginHostException ex)
        {
            Fail(ex.Message);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_host is not null)
        {
            _host.Restarted -= OnRestarted;
            _host.Dispose();
            _host = null;
        }
    }

    private IPluginHost? Host()
    {
        if (_host is not null || _startFailed)
            return _host;
        _host = _hostFactory();
        if (_host is null)
        {
            _startFailed = true;
            SetError("MIDI input needs the plugin host (mfplughost), which is not available in this editor build.");
            return null;
        }

        _host.Restarted += OnRestarted;
        return _host;
    }

    private void OnRestarted(PluginHostInfo info)
    {
        _open.Clear(); // a new helper: nothing is open
        _needsList = true;
    }

    private void List()
    {
        if (Host() is not { } host)
            return;
        try
        {
            if ((host.Start().Capabilities & PluginHostCapabilities.Midi) == 0)
            {
                _startFailed = true;
                SetError("This plugin host was built without MIDI input (on Linux: no ALSA).");
                return;
            }

            Apply(host.ListMidiInputs());
        }
        catch (PluginHostException e)
        {
            Fail(e.Message);
        }
    }

    // Takes a port list: online state, opens enabled devices that are online and not open, closes disabled ones.
    private void Apply(IReadOnlyList<MidiInputDevice> devices)
    {
        Error = null;
        foreach (var d in devices)
            if (!d.Online)
                _open.Remove(d.Id); // the helper closed it

        var host = _host!;
        foreach (var d in devices)
        {
            var want = d.Online && _enabled.Contains(d.Name);
            try
            {
                if (want && !_open.Contains(d.Id))
                {
                    host.OpenMidiInput(d.Id);
                    _open.Add(d.Id);
                    Log.Info($"[Music] MIDI input '{d.Name}' open.");
                }
                else if (!want && _open.Remove(d.Id))
                {
                    host.CloseMidiInput(d.Id);
                }
            }
            catch (PluginHostException e)
            {
                Log.Warning($"[Music] MIDI input '{d.Name}': {e.Message}");
            }
        }

        _devices.Clear();
        foreach (var d in devices)
            _devices.Add(new MidiDeviceState(d.Id, d.Name, d.Online, _enabled.Contains(d.Name), _open.Contains(d.Id)));
        Version++;
    }

    private void Fail(string message)
    {
        _open.Clear();
        SetError($"MIDI input stopped: {message}");
        _needsList = _enabled.Count > 0 && !_startFailed; // the next request restarts the helper
    }

    private void SetError(string message)
    {
        if (Error != message)
            Log.Warning($"[Music] {message}");
        Error = message;
        Version++;
    }
}

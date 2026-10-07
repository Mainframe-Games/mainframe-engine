using System.Buffers.Binary;
using MainframeEngine.Editor.Music;

namespace MainframeEngine.Editor.Tests.Music;

/// <summary>
/// An in-process <see cref="IPluginHost"/> for tests (music editor proposal, "Testing"): scripted encode results,
/// simulated crashes and timeouts, with the real client's stop/restart semantics (a failed request stops the host and
/// raises <see cref="Stopped"/>; the next request restarts it and raises <see cref="Restarted"/>).
/// </summary>
internal sealed class FakePluginHost : IPluginHost
{
    private bool _crashed;
    private int _pid = 1000;

    /// <summary>Performs an encode (default: copies the WAV bytes to the output, which is not a real Ogg file).</summary>
    public Func<string, string, int, PluginHostEncodeResult> EncodeHandler { get; set; } = static (wav, ogg, _) =>
    {
        File.Copy(wav, ogg, overwrite: true);
        return new PluginHostEncodeResult(0, 0, 2);
    };

    /// <summary>The next request behaves as if the helper crashed during it.</summary>
    public bool CrashOnNextRequest { get; set; }

    /// <summary>The next request behaves as if the helper missed its deadline.</summary>
    public bool TimeoutOnNextRequest { get; set; }

    public List<string> Requests { get; } = [];

    public int Starts { get; private set; }

    public bool IsRunning => Info is not null;

    public PluginHostInfo? Info { get; private set; }

    public event Action<PluginHostStop>? Stopped;

    public event Action<PluginHostInfo>? Restarted;

    public PluginHostInfo Start()
    {
        if (Info is not null)
            return Info;
        Starts++;
        Info = new PluginHostInfo(PluginHostProtocol.Version, "fake", PluginHostCapabilities.Encode | PluginHostCapabilities.Midi, ++_pid);
        if (_crashed)
        {
            _crashed = false;
            Restarted?.Invoke(Info);
        }

        return Info;
    }

    public void Ping(TimeSpan? timeout = null) => Request("ping");

    public PluginHostEncodeResult Encode(string wavPath, string oggPath, int quality, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        Request("encode");
        return EncodeHandler(wavPath, oggPath, quality);
    }

    public void Dispose()
    {
        Info = null;
        DropPlugins();
    }

    // ── VST3 simulation: "plugins" are specs by class ID; audio goes through the real shared memory file. ──────────

    private readonly Dictionary<uint, FakeInstance> _instances = [];
    private PluginSharedMemory? _shm;
    private uint _nextInstance;

    /// <summary>The fake plugins by class ID.</summary>
    public Dictionary<string, FakePlugin> Plugins { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The next process calls miss their deadline.</summary>
    public int MissProcesses { get; set; }

    /// <summary>Processing an instance of this class crashes the helper (the shared memory names the instance).</summary>
    public string? CrashOnProcessClass { get; set; }

    public int ProcessCalls { get; private set; }

    public IReadOnlyCollection<uint> LoadedInstances => _instances.Keys;

    /// <summary>The last state set on each instance (by id).</summary>
    public byte[]? StateOf(uint instance) => _instances.TryGetValue(instance, out var i) ? i.State : null;

    public void SetupSharedMemory(string path, long size)
    {
        Request("setupShm");
        _shm?.Dispose();
        _shm = PluginSharedMemory.Open(path, size);
    }

    public PluginLoadResult LoadPlugin(string bundlePath, string classId, int slot, int sampleRate, int maxBlock)
    {
        Request("load");
        if (!Plugins.TryGetValue(classId, out var plugin))
            throw new PluginHostException($"Plugin host load failed: class {classId} is not in {bundlePath} (error 3).");
        var id = ++_nextInstance;
        _instances[id] = new FakeInstance(plugin, slot) { State = [.. plugin.DefaultState] };
        return new PluginLoadResult(id, plugin.Latency, plugin.Instrument ? 0 : 2, 2, false, plugin.Name);
    }

    public void UnloadPlugin(uint instance)
    {
        Request("unload");
        _instances.Remove(instance);
    }

    public byte[] GetPluginState(uint instance)
    {
        Request("getState");
        return [.. Get(instance).State];
    }

    public void SetPluginState(uint instance, ReadOnlySpan<byte> state)
    {
        Request("setState");
        Get(instance).State = state.ToArray();
    }

    public int GetPluginLatency(uint instance)
    {
        Request("latency");
        return Get(instance).Plugin.Latency;
    }

    public void SetPluginsOffline(bool offline)
    {
        Request("setOffline");
        Offline = offline;
    }

    public bool Offline { get; private set; }

    public void OpenPluginEditor(uint instance, string title)
    {
        Request("openEditor");
        Get(instance);
        LastEditorTitle = title;
    }

    public string? LastEditorTitle { get; private set; }

    public void ClosePluginEditor(uint instance) => Request("closeEditor");

    /// <summary>Simulates the user closing an instance's editor window.</summary>
    public void CloseEditorByUser(uint instance) => _closed.Enqueue(instance);

    private readonly Queue<uint> _closed = new();

    public bool TryTakeClosedEditor(out uint instance) => _closed.TryDequeue(out instance);

    // ── MIDI simulation: devices by id, events injected with editor-clock timestamps. ─────────────────────────────

    private readonly List<MidiInputDevice> _midiDevices = [];
    private readonly Queue<MidiInputEvent> _midiEvents = new();
    private IReadOnlyList<MidiInputDevice>? _midiChanged;

    /// <summary>The devices currently open.</summary>
    public HashSet<uint> OpenMidi { get; } = [];

    /// <summary>Adds (or replugs) a device: online, and announced like the helper's poll would.</summary>
    public uint PlugMidi(string name)
    {
        var index = _midiDevices.FindIndex(d => d.Name == name);
        if (index < 0)
        {
            _midiDevices.Add(new MidiInputDevice((uint)_midiDevices.Count + 1, name, true));
            index = _midiDevices.Count - 1;
        }
        else
        {
            _midiDevices[index] = _midiDevices[index] with { Online = true };
        }

        _midiChanged = [.. _midiDevices];
        return _midiDevices[index].Id;
    }

    /// <summary>Unplugs a device: offline and closed (as the helper does), announced.</summary>
    public void UnplugMidi(string name)
    {
        var index = _midiDevices.FindIndex(d => d.Name == name);
        _midiDevices[index] = _midiDevices[index] with { Online = false };
        OpenMidi.Remove(_midiDevices[index].Id);
        _midiChanged = [.. _midiDevices];
    }

    /// <summary>Queues a message from <paramref name="device"/> if it is open (as an open port would deliver it).</summary>
    public void SendMidi(uint device, long timestamp, byte status, byte data1, byte data2)
    {
        if (OpenMidi.Contains(device))
            _midiEvents.Enqueue(new MidiInputEvent(device, timestamp, status, data1, data2));
    }

    public IReadOnlyList<MidiInputDevice> ListMidiInputs()
    {
        Request("midiList");
        return [.. _midiDevices];
    }

    public void OpenMidiInput(uint device)
    {
        Request("midiOpen");
        if (_midiDevices.Find(d => d.Id == device) is not { Online: true })
            throw new PluginHostException($"MIDI device {device} is offline");
        OpenMidi.Add(device);
    }

    public void CloseMidiInput(uint device)
    {
        Request("midiClose");
        OpenMidi.Remove(device);
    }

    public bool TryTakeMidiEvent(out MidiInputEvent midiEvent) => _midiEvents.TryDequeue(out midiEvent);

    public bool TryTakeMidiDevices([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IReadOnlyList<MidiInputDevice>? devices)
    {
        devices = _midiChanged;
        _midiChanged = null;
        return devices is not null;
    }

    public PluginProcessResult ProcessPlugins(ReadOnlySpan<byte> payload, TimeSpan deadline)
    {
        if (Info is null || _shm is not { } shm)
            return PluginProcessResult.Failed;
        ProcessCalls++;
        if (MissProcesses > 0)
        {
            MissProcesses--;
            return PluginProcessResult.Missed;
        }

        var frames = (int)BinaryPrimitives.ReadUInt32LittleEndian(payload);
        var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(payload[24..]);
        for (var e = 0; e < count; e++)
        {
            var id = BinaryPrimitives.ReadUInt32LittleEndian(payload[(28 + 8 * e)..]);
            var input = BinaryPrimitives.ReadUInt32LittleEndian(payload[(32 + 8 * e)..]);
            if (!_instances.TryGetValue(id, out var inst))
                return PluginProcessResult.Failed;
            if (string.Equals(inst.Plugin.ClassId, CrashOnProcessClass, StringComparison.OrdinalIgnoreCase))
            {
                shm.CurrentInstance = id;
                Stop("Plugin host stopped (exit code 134)", "process");
                return PluginProcessResult.Failed;
            }

            var inL = shm.Channel(inst.Slot, PluginSharedMemory.InLeft);
            var inR = shm.Channel(inst.Slot, PluginSharedMemory.InRight);
            if (input != uint.MaxValue)
            {
                shm.Channel((int)input, PluginSharedMemory.OutLeft)[..frames].CopyTo(inL);
                shm.Channel((int)input, PluginSharedMemory.OutRight)[..frames].CopyTo(inR);
            }

            var outL = shm.Channel(inst.Slot, PluginSharedMemory.OutLeft);
            var outR = shm.Channel(inst.Slot, PluginSharedMemory.OutRight);
            if (inst.Plugin.Instrument)
            {
                outL[..frames].Clear();
                for (var i = 0; i < (int)shm.EventCount(inst.Slot); i++)
                {
                    var ev = shm.ReadEvent(inst.Slot, i);
                    if (ev.On)
                        outL[ev.Offset] += ev.Velocity / 127f;
                }

                outL[..frames].CopyTo(outR);
                shm.EventCount(inst.Slot) = 0;
            }
            else
            {
                for (var f = 0; f < frames; f++)
                {
                    outL[f] = inst.Delay(0, inL[f] * inst.Plugin.Gain);
                    outR[f] = inst.Delay(1, inR[f] * inst.Plugin.Gain);
                    inst.Advance();
                }
            }
        }

        return PluginProcessResult.Ok;
    }

    private FakeInstance Get(uint instance) =>
        _instances.TryGetValue(instance, out var i) ? i : throw new PluginHostException($"Plugin host failed: unknown plugin instance {instance} (error 3).");

    private void DropPlugins()
    {
        _instances.Clear();
        _shm?.Dispose();
        _shm = null;
    }

    private sealed class FakeInstance(FakePlugin plugin, int slot)
    {
        private readonly float[] _ring = new float[Math.Max(1, plugin.Latency) * 2];
        private int _position;

        public FakePlugin Plugin { get; } = plugin;

        public int Slot { get; } = slot;

        public byte[] State { get; set; } = [];

        public float Delay(int channel, float value)
        {
            if (Plugin.Latency == 0)
                return value;
            var at = 2 * _position + channel;
            var old = _ring[at];
            _ring[at] = value;
            return old;
        }

        public void Advance()
        {
            if (Plugin.Latency > 0)
                _position = (_position + 1) % Plugin.Latency;
        }
    }

    private void Request(string what)
    {
        Start();
        Requests.Add(what);
        if (CrashOnNextRequest)
        {
            CrashOnNextRequest = false;
            Stop("Plugin host stopped (exit code 139)", what);
            throw new PluginHostException($"Plugin host stopped (exit code 139) during {what}.");
        }

        if (TimeoutOnNextRequest)
        {
            TimeoutOnNextRequest = false;
            Stop($"Plugin host did not answer {what} within 10 s", what);
            throw new PluginHostTimeoutException($"The plugin host did not answer {what} within 10 s.");
        }
    }

    private void Stop(string reason, string what)
    {
        Info = null;
        _crashed = true;
        _instances.Clear(); // a new helper process has no plugins (the shared memory file stays mapped here)
        Stopped?.Invoke(new PluginHostStop(reason, what));
    }
}

/// <summary>A fake VST3 class: an instrument (an impulse of velocity/127 at each note-on) or an effect (gain, then a delay of <see cref="Latency"/> frames).</summary>
internal sealed record FakePlugin(string ClassId, string Name, bool Instrument = false, float Gain = 1f, int Latency = 0)
{
    public byte[] DefaultState { get; init; } = [1, 2, 3];

    public PluginDescriptor Descriptor => new() { Format = "vst3", ClassId = ClassId, Name = Name, Vendor = "Fake" };
}

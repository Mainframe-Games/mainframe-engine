using System.Buffers.Binary;

namespace MainframeEngine.Editor.Music;

/// <summary>
/// The VST3 plugins of one song engine (ADR 0147): instances in the helper, the shared memory their audio and note
/// events go through, crash recovery and state capture. UI-thread methods load, unload and capture; the render thread
/// batches every chain of a block into one <see cref="IPluginHost.ProcessPlugins"/> round trip
/// (<see cref="BeginBatch"/>, <see cref="AddChain"/>, <see cref="RunBatch"/>, <see cref="ReadOutput"/>) without
/// allocating. The song model stays the truth for plugin state: captures are committed to the model (undoably, by the
/// caller), and a model state that differs from the plugin's (undo, redo) is pushed back by <see cref="Maintain"/>.
/// </summary>
public sealed class PluginRack : IDisposable
{
    /// <summary>Plugin instances one song can run.</summary>
    public const int MaxSlots = 128;

    /// <summary>Crashes of one plugin within a minute that disable it for the session.</summary>
    public const int CrashesToDisable = 2;

    /// <summary>Offline renders wait this long for a block (a watchdog, not a deadline).</summary>
    public static readonly TimeSpan OfflineBlockTimeout = TimeSpan.FromSeconds(10);

    private const int PayloadHeader = 28;
    private const uint OwnInput = 0xFFFFFFFF;

    private readonly Func<IPluginHost?> _hostFactory;
    private readonly Func<string, string?> _resolve;
    private readonly PluginInstance?[] _slots = new PluginInstance?[MaxSlots];
    private readonly List<PluginInstance> _instances = [];
    private readonly byte[] _payload = new byte[PayloadHeader + MaxSlots * 8];
    private readonly Lock _crashGate = new();
    private IPluginHost? _host;
    private bool _hostTried;
    private PluginSharedMemory? _shm;
    private volatile bool _shmSent;
    private volatile bool _needsReload;
    private long _retryAt;
    private int _version;
    private int _xruns;
    private int _entryCount;
    private bool _disposed;

    /// <param name="hostFactory">Creates the helper client on first use (null result: no helper; plugins show an error).</param>
    /// <param name="sampleRate">The engine's rate (every plugin runs at it, in blocks of <see cref="SongEngine.BlockFrames"/>).</param>
    /// <param name="resolve">Class ID → bundle path (default: <see cref="PluginCatalog.Resolve"/>).</param>
    /// <param name="offline">Offline render: kOffline processing, a <see cref="OfflineBlockTimeout"/> watchdog instead of the deadline.</param>
    public PluginRack(Func<IPluginHost?> hostFactory, int sampleRate, Func<string, string?>? resolve = null, bool offline = false)
    {
        _hostFactory = hostFactory ?? throw new ArgumentNullException(nameof(hostFactory));
        _resolve = resolve ?? PluginCatalog.Resolve;
        SampleRate = sampleRate;
        Offline = offline;
    }

    public int SampleRate { get; }

    public bool Offline { get; }

    /// <summary>Changes when an instance loads, fails, crashes or changes latency (the player rebuilds its snapshot).</summary>
    public int Version => Volatile.Read(ref _version);

    /// <summary>Blocks whose plugin round trip missed its deadline (played as silence).</summary>
    public int Xruns => Volatile.Read(ref _xruns);

    /// <summary>The song's name, for editor window titles.</summary>
    public string SongName { get; set; } = "Song";

    public IReadOnlyList<PluginInstance> Instances => _instances;

    /// <summary>The helper client, once a plugin needed it.</summary>
    public IPluginHost? Host => _host;

    // ── UI thread ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Creates and loads an instance for <paramref name="owner"/> (an <see cref="InstrumentDescriptor"/> or <see cref="PluginInsert"/>).</summary>
    public PluginInstance Acquire(object owner, PluginDescriptor descriptor, bool instrument, string context)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var slot = Array.IndexOf(_slots, null);
        var instance = new PluginInstance(this, Math.Max(slot, 0), owner, descriptor, instrument, context);
        if (slot < 0)
        {
            instance.Error = $"more than {MaxSlots} plugins in one song";
            return instance;
        }

        _slots[slot] = instance;
        lock (_crashGate)
            _instances.Add(instance);
        Load(instance);
        return instance;
    }

    /// <summary>Captures the instance's state into its owner (so undoing the removal restores it), then unloads it.</summary>
    public void Release(PluginInstance instance)
    {
        lock (_crashGate)
        {
            if (!_instances.Remove(instance))
                return;
        }

        if (instance.IsLoaded && CaptureState(instance) is { } state)
            SetOwnerState(instance.Owner, state);
        Unload(instance);
        _slots[instance.Slot] = null;
        Bump();
    }

    /// <summary>Reads every loaded plugin's state; <paramref name="commit"/> gets each owner whose state changed (save, render).</summary>
    public void CaptureStates(Action<object, string?> commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        foreach (var instance in _instances.ToList())
            CaptureInto(instance, commit);
    }

    /// <summary>
    /// Per frame: reloads plugins after a helper crash, captures the state of editor windows the user closed (through
    /// <paramref name="commit"/>), and pushes model states that differ from the plugins' (undo, redo).
    /// </summary>
    public void Maintain(Action<object, string?>? commit)
    {
        if (_disposed)
            return;
        if (_needsReload && Environment.TickCount64 >= Volatile.Read(ref _retryAt))
            Reload();
        if (_host is not { } host)
            return;
        while (host.TryTakeClosedEditor(out var id))
        {
            if (_instances.Find(i => i.HelperId == id) is { } instance && commit is not null)
                CaptureInto(instance, commit);
        }

        foreach (var instance in _instances)
        {
            var wanted = GetOwnerState(instance.Owner);
            if (!instance.IsLoaded || string.Equals(wanted, instance.LoadedState, StringComparison.Ordinal))
                continue;
            Guard(instance, () =>
            {
                host.SetPluginState(instance.HelperId, string.IsNullOrEmpty(wanted) ? instance.InitialState : Convert.FromBase64String(wanted));
                instance.LoadedState = wanted;
                UpdateLatency(host, instance);
            });
        }
    }

    /// <summary>Opens the plugin's own editor window ("Plugin — Track — Song").</summary>
    public void OpenEditor(PluginInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (!instance.IsLoaded || _host is null)
            throw new PluginHostException(instance.Error ?? $"{instance.Name} is not loaded.");
        _host.OpenPluginEditor(instance.HelperId, $"{instance.Name} — {instance.Context} — {SongName}");
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var instance in _instances)
            Unload(instance);
        _instances.Clear();
        if (_host is { } host)
        {
            host.Stopped -= OnStopped;
            host.Dispose();
        }

        _shm?.Dispose();
    }

    // ── Render thread (no allocation) ────────────────────────────────────────────────────────────────────────────

    /// <summary>Starts a block's batch.</summary>
    public void BeginBatch() => _entryCount = 0;

    /// <summary>
    /// Adds a chain — a plugin instrument (its <paramref name="events"/>) and/or plugin effects; a chain without a loaded
    /// instrument starts from <paramref name="input"/> (interleaved stereo). Returns the slot whose output will hold the
    /// chain's result, or −1 when nothing in it is loaded (the chain passes its input through).
    /// </summary>
    public int AddChain(PluginInstrument? instrument, ReadOnlySpan<NoteEvent> events, ReadOnlySpan<IEffect> effects, ReadOnlySpan<float> input, int frames)
    {
        var shm = _shm;
        if (shm is null || !_shmSent)
            return -1;
        var previous = -1;
        if (instrument is { Instance: { IsLoaded: true } inst })
        {
            var count = 0;
            if (instrument.TakeAllNotesOff())
            {
                for (var pitch = 0; pitch < 128 && count < shm.MaxEvents; pitch++)
                    shm.WriteEvent(inst.Slot, count++, 0, false, pitch, 0);
            }

            foreach (var e in events)
            {
                if (count >= shm.MaxEvents)
                    break;
                shm.WriteEvent(inst.Slot, count++, Math.Clamp(e.Offset, 0, frames - 1), e.On, e.Pitch, e.Velocity);
            }

            shm.EventCount(inst.Slot) = (uint)count;
            AddEntry(inst.HelperId, OwnInput);
            previous = inst.Slot;
        }

        foreach (var effect in effects)
        {
            if (effect is not PluginEffect { Instance: { IsLoaded: true } fx })
                continue;
            if (previous < 0)
            {
                var left = shm.Channel(fx.Slot, PluginSharedMemory.InLeft);
                var right = shm.Channel(fx.Slot, PluginSharedMemory.InRight);
                for (var f = 0; f < frames; f++)
                {
                    left[f] = input[2 * f];
                    right[f] = input[2 * f + 1];
                }

                AddEntry(fx.HelperId, OwnInput);
            }
            else
            {
                AddEntry(fx.HelperId, (uint)previous);
            }

            shm.EventCount(fx.Slot) = 0;
            previous = fx.Slot;
        }

        return previous;
    }

    /// <summary>One round trip for the batch. Missed (deadline) counts an xrun; callers then play the chains as silence.</summary>
    public PluginProcessResult RunBatch(int frames, bool playing, double tempo, long position)
    {
        if (_entryCount == 0)
            return PluginProcessResult.Ok;
        if (_host is not { } host || _needsReload || !_shmSent)
            return PluginProcessResult.Failed;
        var span = _payload.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, (uint)frames);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], playing ? 1u : 0u);
        BinaryPrimitives.WriteDoubleLittleEndian(span[8..], tempo);
        BinaryPrimitives.WriteInt64LittleEndian(span[16..], position);
        BinaryPrimitives.WriteUInt32LittleEndian(span[24..], (uint)_entryCount);
        var deadline = Offline ? OfflineBlockTimeout : TimeSpan.FromSeconds(4.0 * frames / SampleRate);
        var result = host.ProcessPlugins(span[..(PayloadHeader + _entryCount * 8)], deadline);
        if (result == PluginProcessResult.Missed)
            Interlocked.Increment(ref _xruns);
        return result;
    }

    /// <summary>Copies a slot's output (after <see cref="RunBatch"/>) into interleaved <paramref name="stereo"/>.</summary>
    public void ReadOutput(int slot, Span<float> stereo, int frames)
    {
        var shm = _shm!;
        var left = shm.Channel(slot, PluginSharedMemory.OutLeft);
        var right = shm.Channel(slot, PluginSharedMemory.OutRight);
        for (var f = 0; f < frames; f++)
        {
            stereo[2 * f] = left[f];
            stereo[2 * f + 1] = right[f];
        }
    }

    // ── Internals ────────────────────────────────────────────────────────────────────────────────────────────────

    internal static string? GetOwnerState(object owner) => owner switch
    {
        InstrumentDescriptor d => d.State,
        PluginInsert i => i.State,
        _ => null,
    };

    internal static void SetOwnerState(object owner, string? state)
    {
        switch (owner)
        {
            case InstrumentDescriptor d:
                d.State = state;
                break;
            case PluginInsert i:
                i.State = state;
                break;
        }
    }

    private void AddEntry(uint id, uint inputSlot)
    {
        var at = PayloadHeader + _entryCount * 8;
        BinaryPrimitives.WriteUInt32LittleEndian(_payload.AsSpan(at), id);
        BinaryPrimitives.WriteUInt32LittleEndian(_payload.AsSpan(at + 4), inputSlot);
        _entryCount++;
    }

    private IPluginHost? GetHost()
    {
        if (_host is null && !_hostTried)
        {
            _hostTried = true;
            _host = _hostFactory();
            if (_host is not null)
                _host.Stopped += OnStopped;
        }

        return _host;
    }

    private void Load(PluginInstance instance)
    {
        instance.Error = null;
        instance.HelperId = 0;
        if (instance.Disabled)
        {
            instance.Error = instance.DisabledReason;
            return;
        }

        var classId = instance.Descriptor.ClassId;
        var bundle = classId is null ? null : _resolve(classId);
        if (bundle is null)
        {
            instance.Error = "missing plugin: " + instance.DisplayName;
            Bump();
            return;
        }

        if (GetHost() is not { } host)
        {
            instance.Error = "the plugin host (mfplughost) is not available";
            Bump();
            return;
        }

        try
        {
            EnsureShm(host);
            var loaded = host.LoadPlugin(bundle, classId!, instance.Slot, SampleRate, SongEngine.BlockFrames);
            instance.Name = string.IsNullOrEmpty(loaded.Name) ? instance.Name : loaded.Name;
            instance.HasEditor = loaded.HasEditor;
            instance.LatencyFrames = loaded.LatencyFrames;
            var state = GetOwnerState(instance.Owner);
            if (instance.InitialState.Length == 0)
                instance.InitialState = host.GetPluginState(loaded.Instance); // what "no saved state" means (undo to it)
            if (!string.IsNullOrEmpty(state))
            {
                host.SetPluginState(loaded.Instance, Convert.FromBase64String(state));
                instance.LatencyFrames = host.GetPluginLatency(loaded.Instance);
            }

            instance.LoadedState = state;
            instance.HelperId = loaded.Instance; // last: the render thread starts using it
        }
        catch (Exception e) when (e is PluginHostException or FormatException)
        {
            instance.Error = e is FormatException ? "the saved plugin state is not valid base64" : e.Message;
            Log.Warning($"[Music] Plugin {instance.DisplayName} on {instance.Context}: {instance.Error}");
        }

        Bump();
    }

    private void Unload(PluginInstance instance)
    {
        var id = instance.HelperId;
        instance.HelperId = 0;
        if (id == 0 || _host is not { IsRunning: true } host)
            return;
        try
        {
            host.UnloadPlugin(id);
        }
        catch (PluginHostException e)
        {
            Log.Warning($"[Music] Unloading {instance.DisplayName}: {e.Message}");
        }
    }

    private void EnsureShm(IPluginHost host)
    {
        _shm ??= PluginSharedMemory.Create(SongEngine.BlockFrames, MaxSlots, SongEngine.MaxEventsPerBlock);
        if (_shmSent && host.IsRunning)
            return;
        host.SetupSharedMemory(_shm.Path, _shm.Size);
        if (Offline)
            host.SetPluginsOffline(true);
        _shmSent = true;
    }

    private string? CaptureState(PluginInstance instance)
    {
        if (!instance.IsLoaded || _host is not { } host)
            return null;
        string? state = null;
        Guard(instance, () =>
        {
            state = Convert.ToBase64String(host.GetPluginState(instance.HelperId));
            instance.LoadedState = state;
        });
        return state;
    }

    private void CaptureInto(PluginInstance instance, Action<object, string?> commit)
    {
        if (CaptureState(instance) is { } state && !string.Equals(state, GetOwnerState(instance.Owner), StringComparison.Ordinal))
            commit(instance.Owner, state);
    }

    private void UpdateLatency(IPluginHost host, PluginInstance instance)
    {
        var latency = host.GetPluginLatency(instance.HelperId);
        if (latency != instance.LatencyFrames)
        {
            instance.LatencyFrames = latency;
            Bump();
        }
    }

    private static void Guard(PluginInstance instance, Action action)
    {
        try
        {
            action();
        }
        catch (Exception e) when (e is PluginHostException or FormatException)
        {
            Log.Warning($"[Music] Plugin {instance.DisplayName} on {instance.Context}: {e.Message}");
        }
    }

    // The helper stopped (any thread): blame the instance it was calling into, mark everything unloaded, reload later.
    private void OnStopped(PluginHostStop stop)
    {
        lock (_crashGate)
        {
            _shmSent = false;
            var blamed = _shm?.CurrentInstance ?? 0;
            foreach (var instance in _instances.ToArray())
            {
                if (blamed != 0 && instance.HelperId == blamed)
                    instance.RecordCrash();
                instance.HelperId = 0;
            }

            _needsReload = true;
            Volatile.Write(ref _retryAt, 0);
            Bump();
        }
    }

    private void Reload()
    {
        _needsReload = false;
        if (_shm is not null)
            _shm.CurrentInstance = 0;
        foreach (var instance in _instances.ToList())
        {
            if (!instance.IsLoaded)
                Load(instance);
            if (_needsReload)
                return; // it crashed the host again: the next Maintain carries on
        }

        if (_host is { IsRunning: false } && _instances.Exists(i => !i.IsLoaded && !i.Disabled))
        {
            // The host could not start (restart limit): try again shortly.
            _needsReload = true;
            Volatile.Write(ref _retryAt, Environment.TickCount64 + 5000);
        }
    }

    private void Bump() => Interlocked.Increment(ref _version);
}

/// <summary>One plugin of a <see cref="PluginRack"/>: an instrument or an insert of a track or the master.</summary>
public sealed class PluginInstance
{
    private readonly Queue<long> _crashes = new();
    private uint _helperId;

    internal PluginInstance(PluginRack rack, int slot, object owner, PluginDescriptor descriptor, bool instrument, string context)
    {
        Rack = rack;
        Slot = slot;
        Owner = owner;
        Descriptor = descriptor;
        IsInstrument = instrument;
        Context = context;
        Name = descriptor.Name ?? descriptor.ClassId ?? "plugin";
    }

    public PluginRack Rack { get; }

    /// <summary>Its shared memory slot.</summary>
    public int Slot { get; }

    /// <summary>The <see cref="InstrumentDescriptor"/> or <see cref="PluginInsert"/> it plays.</summary>
    public object Owner { get; }

    public PluginDescriptor Descriptor { get; }

    public bool IsInstrument { get; }

    /// <summary>The track's name (or "Master") for window titles and messages.</summary>
    public string Context { get; }

    public string Name { get; internal set; }

    public string DisplayName => string.IsNullOrEmpty(Descriptor.Vendor) ? Name : $"{Name} ({Descriptor.Vendor})";

    /// <summary>The helper's id while loaded (0 otherwise).</summary>
    public uint HelperId
    {
        get => Volatile.Read(ref _helperId);
        internal set => Volatile.Write(ref _helperId, value);
    }

    public bool IsLoaded => HelperId != 0;

    public bool HasEditor { get; internal set; }

    public int LatencyFrames { get; internal set; }

    /// <summary>Why it is not playing ("missing plugin: …", a load error, disabled after crashes), or null.</summary>
    public string? Error { get; internal set; }

    /// <summary>Crashed the helper <see cref="PluginRack.CrashesToDisable"/> times within a minute: not loaded again this session.</summary>
    public bool Disabled { get; private set; }

    internal string DisabledReason => $"disabled: {DisplayName} crashed the plugin host {PluginRack.CrashesToDisable} times in a minute";

    /// <summary>The plugin's state right after its first load (pushed when the model's state goes back to none).</summary>
    internal byte[] InitialState { get; set; } = [];

    /// <summary>The state (base64) the plugin has as far as the rack knows: loaded, pushed or captured.</summary>
    internal string? LoadedState { get; set; }

    internal void RecordCrash()
    {
        var now = Environment.TickCount64;
        _crashes.Enqueue(now);
        while (_crashes.Count > 0 && now - _crashes.Peek() > 60_000)
            _crashes.Dequeue();
        if (_crashes.Count >= PluginRack.CrashesToDisable && !Disabled)
        {
            Disabled = true;
            Error = DisabledReason;
            Log.Warning($"[Music] {Error} (on {Context}).");
        }
    }
}

/// <summary>A track's VST3 instrument (<see cref="PluginRack"/>). The song engine batches it; <see cref="Process"/> is the stand-alone path.</summary>
public sealed class PluginInstrument(PluginInstance instance) : IInstrument
{
    private int _allNotesOff;

    public PluginInstance Instance { get; } = instance;

    public int LatencyFrames => Instance.IsLoaded ? Instance.LatencyFrames : 0;

    public void Process(TrackSnapshot track, ReadOnlySpan<NoteEvent> events, Span<float> stereo)
    {
        var rack = Instance.Rack;
        var frames = stereo.Length / 2;
        rack.BeginBatch();
        var slot = rack.AddChain(this, events, default, default, frames);
        if (slot < 0 || rack.RunBatch(frames, true, 120, 0) != PluginProcessResult.Ok)
            return;
        var shm = Instance.Rack;
        Span<float> output = stackalloc float[frames * 2];
        shm.ReadOutput(slot, output, frames);
        for (var i = 0; i < output.Length; i++)
            stereo[i] += output[i];
    }

    /// <summary>The next block starts with a note-off for every pitch.</summary>
    public void AllNotesOff() => Volatile.Write(ref _allNotesOff, 1);

    internal bool TakeAllNotesOff() => Interlocked.Exchange(ref _allNotesOff, 0) == 1;
}

/// <summary>A VST3 insert effect (<see cref="PluginRack"/>). The song engine batches it; <see cref="Process"/> is the stand-alone path.</summary>
public sealed class PluginEffect(PluginInstance instance) : IEffect
{
    public PluginInstance Instance { get; } = instance;

    public int LatencyFrames => Instance.IsLoaded ? Instance.LatencyFrames : 0;

    public void Process(Span<float> stereo)
    {
        var rack = Instance.Rack;
        var frames = stereo.Length / 2;
        rack.BeginBatch();
        ReadOnlySpan<IEffect> chain = [this];
        var slot = rack.AddChain(null, default, chain, stereo, frames);
        if (slot >= 0 && rack.RunBatch(frames, true, 120, 0) == PluginProcessResult.Ok)
            rack.ReadOutput(slot, stereo, frames);
    }

    public void Reset()
    {
    }
}

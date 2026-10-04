using System.Numerics;
using MainframeEngine.Audio;
using SoundFlow.Structs;

namespace MainframeEngine;

/// <summary>Which output <see cref="AudioServer.Create"/> uses.</summary>
public enum AudioDeviceMode
{
    /// <summary>The default playback device; the null device when there is none or it fails to open.</summary>
    Auto,

    /// <summary>The null device in real time (no sound, playback still advances).</summary>
    Null,

    /// <summary>The null device advanced only by <see cref="AudioServer.RenderNullDevice"/> (deterministic tests).</summary>
    NullManual,
}

/// <summary>Audio settings for <see cref="EngineOptions.Audio"/> and <see cref="AudioServer.Create"/>.</summary>
public struct AudioOptions()
{
    /// <summary>Creates the <see cref="AudioServer"/> at startup.</summary>
    public bool Enabled = true;

    public AudioDeviceMode Device = AudioDeviceMode.Auto;

    /// <summary>Mix rate requested from the device (miniaudio converts if the hardware differs).</summary>
    public int SampleRate = 48000;

    /// <summary>Device period: lower is less latency, higher is safer against dropouts.</summary>
    public int BufferMilliseconds = 10;

    /// <summary>Bus layout resource to load; missing or null uses <see cref="AudioBusLayout.CreateDefault"/>.</summary>
    public string? BusLayoutPath = AudioBusLayout.DefaultPath;

    /// <summary>Commands the game thread can have in flight to the audio thread before they wait for the next flush.</summary>
    public int CommandQueueCapacity = 4096;
}

/// <summary>Counters for debug overlays and QA.</summary>
public readonly record struct AudioServerStats(
    int ActiveVoices,
    int TotalVoices,
    long Steals,
    long Rejected,
    int Underruns,
    int StreamErrors,
    long RenderedFrames,
    long Blocks,
    int PendingCommands,
    bool Faulted);

/// <summary>A playing (or finished) voice; invalid once the sound ends, is stopped or is stolen.</summary>
public readonly record struct AudioVoiceHandle(int Index, int Generation)
{
    public bool IsValid => Generation != 0;
}

/// <summary>
/// The audio server (one per process): owns the output device, the bus mixer, the voice pools and the command queue
/// to the audio thread. Audio nodes are front-ends to it; <see cref="PlayOneShot"/> plays sounds without a node.
/// </summary>
/// <remarks>
/// <para><b>Threads.</b> Nodes and game code run on the game thread and never touch SoundFlow: every change becomes a
/// small <see cref="AudioCommand"/> appended to a pending batch, and <see cref="Flush"/> (called by
/// <see cref="Process"/> once per frame, after the scene tree's transform sync) publishes the batch to the audio thread
/// through a lock-free single-producer/single-consumer ring. The audio thread (miniaudio's device callback, or the
/// null device) drains it before mixing each block and reports finished voices back through a second ring. Streamed
/// sounds are decoded by a third thread. Steady-state frames allocate nothing on the game or audio thread.</para>
/// <para><b>Startup never throws</b>: without a usable device (CI, servers, a missing native library) the server runs
/// on the null device, which keeps time but makes no sound.</para>
/// <para><b>Voices</b> are pooled per bus (<see cref="AudioBusInfo.MaxVoices"/>). A node with
/// <c>MaxPolyphony</c> voices restarts its oldest when it plays again; a full pool steals the voice with the lowest
/// priority, then the quietest, then the oldest — never one with a higher priority than the new sound (which is then
/// dropped and counted in <see cref="AudioServerStats.Rejected"/>).</para>
/// </remarks>
public sealed class AudioServer : IFrameServer
{
    private static int _soundFlowLogHooked;

    private readonly AudioOutput _output;
    private readonly AudioOptions _options;
    private readonly SpscRing<AudioCommand> _commands;
    private readonly SpscRing<AudioEvent> _events = new(1024);
    private readonly AudioMixRoot _root;
    private readonly AudioStreamer _streamer = new();
    private readonly List<AudioGraph> _retiring = [];
    private readonly List<AudioListener3D> _listeners = [];

    private AudioGraph _graph;
    private AudioBus[] _buses = [];
    private VoiceSlot[] _slots = [];
    private AudioCommand[] _pending = new AudioCommand[256];
    private int _pendingCount;
    private bool _busesDirty = true;
    private long _sequence;
    private long _steals;
    private long _rejected;
    private bool _faultLogged;
    private bool _disposed;

    // Listener state (refreshed each Process).
    private AudioListener3D? _currentListener;
    private Transform3D _listener3D = Transform3D.Identity;
    private Vector3 _listenerVelocity;
    private Vector2 _listener2D;
    private bool _listenerInitialized;

    private AudioServer(AudioOutput output, AudioBusLayout layout, SceneTree? tree, in AudioOptions options)
    {
        _output = output;
        _options = options;
        Tree = tree;
        _commands = new SpscRing<AudioCommand>(Math.Max(64, options.CommandQueueCapacity));
        _root = new AudioMixRoot(output.Engine, output.Format, _commands, _events);
        _graph = BuildGraph(layout);
        Enqueue(new AudioCommand { Type = AudioCommandType.SwapGraph, Ref = _graph });
        Flush();
        output.Start(_root);
    }

    /// <summary>
    /// Creates the server with the device chosen by <paramref name="options"/>. Never throws for device or layout
    /// problems: it logs them and falls back to the null device / the default layout.
    /// </summary>
    public static AudioServer Create(in AudioOptions options, SceneTree? tree = null)
    {
        HookSoundFlowLog();
        var layout = LoadLayout(options.BusLayoutPath);
        var sampleRate = Math.Clamp(options.SampleRate, 8000, 192_000);

        AudioOutput? output = null;
        string reason;
        switch (options.Device)
        {
            case AudioDeviceMode.NullManual:
                reason = "manual";
                break;
            case AudioDeviceMode.Null:
                reason = "audio device disabled";
                break;
            default:
                output = SoundFlowOutput.TryOpen(sampleRate, options.BufferMilliseconds, out var failure);
                reason = failure ?? string.Empty;
                break;
        }

        if (output is not null)
        {
            try
            {
                var server = new AudioServer(output, layout, tree, options);
                Log.Info($"[Audio] Output: {output.Name}, {sampleRate} Hz stereo, {server.TotalVoices} voices on {server._buses.Length} buses");
                return server;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                reason = $"starting the device failed: {e.Message}";
                DisposeQuietly(output);
            }
        }

        var mode = options.Device == AudioDeviceMode.NullManual ? NullAudioMode.Manual : NullAudioMode.Realtime;
        if (options.Device == AudioDeviceMode.Auto)
            Log.Warning($"[Audio] No usable audio device ({reason}); using the null device (silent).");
        var nullServer = new AudioServer(new NullOutput(sampleRate, options.BufferMilliseconds, mode, reason), layout, tree, options);
        if (options.Device != AudioDeviceMode.NullManual)
            Log.Info($"[Audio] Output: {nullServer.DeviceName}, {sampleRate} Hz, {nullServer.TotalVoices} voices");
        return nullServer;
    }

    // ---------------------------------------------------------------------------------------------
    // Properties
    // ---------------------------------------------------------------------------------------------

    /// <summary>The tree whose <see cref="SceneTree.Paused"/> state pauses voices and whose cameras are the default listeners.</summary>
    public SceneTree? Tree { get; set; }

    /// <summary>Output device name ("Null device (…)" for the null device).</summary>
    public string DeviceName => _output.Name;

    public bool IsNullDevice => _output.IsNull;

    /// <summary>Mix sample rate in Hz.</summary>
    public int SampleRate => _output.Format.SampleRate;

    /// <summary>Buses in layout order; <see cref="Master"/> is first.</summary>
    public IReadOnlyList<AudioBus> Buses => _buses;

    public AudioBus Master => _buses[0];

    /// <summary>Voices across every bus pool.</summary>
    public int TotalVoices => _slots.Length;

    /// <summary>Speed of sound for doppler, in world units per second.</summary>
    public float SpeedOfSound { get; set; } = 343f;

    /// <summary>Scales every doppler shift (0 disables doppler globally).</summary>
    public float DopplerScale { get; set; } = 1f;

    /// <summary>Horizontal distance (2D units, pixels) from the 2D listener at which <see cref="AudioPlayer2D"/> pans fully.</summary>
    public float PanDistance2D { get; set; } = 960f;

    /// <summary>The listener 3D sounds are heard from this frame (current <see cref="AudioListener3D"/>, else the active camera).</summary>
    public Transform3D Listener3D => _listener3D;

    /// <summary>The 2D listener position (the active <see cref="Camera2D"/>, else the origin).</summary>
    public Vector2 Listener2D => _listener2D;

    /// <summary>The current <see cref="AudioListener3D"/>, if any.</summary>
    public AudioListener3D? CurrentListener => _currentListener;

    /// <summary>Delta time of the last <see cref="Process"/> (seconds).</summary>
    internal float FrameDelta { get; private set; }

    /// <summary>Increments on every <see cref="Process"/>; nodes cache per-frame values against it.</summary>
    internal long FrameIndex { get; private set; }

    internal Vector3 ListenerVelocity => _listenerVelocity;

    public AudioServerStats Stats
    {
        get
        {
            var active = 0;
            foreach (ref readonly var slot in _slots.AsSpan())
            {
                if (slot.Active)
                    active++;
            }

            return new AudioServerStats(active, _slots.Length, _steals, _rejected, _root.Underruns, _streamer.Errors,
                _root.RenderedFrames, _root.Blocks, _commands.Count + _pendingCount, _root.Fault is not null);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Buses
    // ---------------------------------------------------------------------------------------------

    /// <summary>The bus named <paramref name="name"/>, or null.</summary>
    public AudioBus? GetBus(string name)
    {
        var index = FindBus(name);
        return index >= 0 ? _buses[index] : null;
    }

    internal int FindBus(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return -1;
        for (var i = 0; i < _buses.Length; i++)
        {
            if (string.Equals(_buses[i].Name, name, StringComparison.Ordinal))
                return i;
        }

        return -1;
    }

    internal void OnBusChanged() => _busesDirty = true;

    /// <summary>A layout resource describing the buses as they are now (volume, mute, solo included).</summary>
    public AudioBusLayout GetBusLayout()
    {
        var layout = new AudioBusLayout { ResourceName = "Audio bus layout" };
        foreach (var bus in _buses)
        {
            layout.Buses.Add(new AudioBusInfo
            {
                Name = bus.Name,
                Send = bus.Index == 0 ? string.Empty : bus.Send,
                VolumeDb = bus.VolumeDb,
                Mute = bus.Mute,
                Solo = bus.Solo,
                MaxVoices = bus.MaxVoices,
                Effects = [.. bus.Effects],
            });
        }

        return layout;
    }

    /// <summary>Saves <see cref="GetBusLayout"/> as a <c>.mres</c> (default: the project's layout file); returns its UID.</summary>
    public string SaveBusLayout(string? path = null)
    {
        var target = path ?? _options.BusLayoutPath ?? AudioBusLayout.DefaultPath;
        var full = AssetDatabase.Current.ToAbsolutePath(target);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        return ResourceSaver.Save(GetBusLayout(), target);
    }

    /// <summary>
    /// Replaces the bus layout (an editor action). Every playing voice stops (without <c>Finished</c>); the new graph
    /// is built here and swapped in on the audio thread at its next block.
    /// </summary>
    public void ApplyBusLayout(AudioBusLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (layout.Validate() is { } error)
            throw new ArgumentException($"Invalid bus layout: {error}", nameof(layout));

        for (var i = 0; i < _slots.Length; i++)
        {
            if (_slots[i].Active)
                Release(i, finished: false, sendStop: false);
        }

        foreach (var voice in _graph.Voices)
        {
            if (voice.StreamChannel is { } channel)
            {
                channel.Request(null, 0, false, 0, 0);
                _streamer.Unregister(channel);
            }
        }

        _retiring.Add(_graph);
        _graph = BuildGraph(layout);
        _pendingCount = 0; // everything pending targeted the old graph
        Enqueue(new AudioCommand { Type = AudioCommandType.SwapGraph, Ref = _graph });
        _streamer.Wake();
        Flush();
    }

    private AudioGraph BuildGraph(AudioBusLayout layout)
    {
        if (layout.Validate() is { } error)
        {
            Log.Error($"[Audio] Invalid bus layout ({error}); using the default layout.");
            layout = AudioBusLayout.CreateDefault();
        }

        var buses = new AudioBus[layout.Buses.Count];
        var firstVoice = 0;
        for (var i = 0; i < buses.Length; i++)
        {
            var info = layout.Buses[i];
            AudioBus? parent = null;
            if (i > 0)
            {
                for (var p = 0; p < i; p++)
                {
                    if (buses[p].Name == info.Send)
                        parent = buses[p];
                }
            }

            buses[i] = new AudioBus(this, i, info, parent, firstVoice);
            firstVoice += Math.Max(0, info.MaxVoices);
        }

        _buses = buses;
        _slots = new VoiceSlot[firstVoice];
        for (var i = 0; i < _slots.Length; i++)
            _slots[i].PendingParams = -1;
        Span<float> gains = stackalloc float[buses.Length];
        ComputeBusGains(gains);
        _busesDirty = false;
        return AudioGraph.Build(_output.Engine, _output.Format, layout, gains);
    }

    private void ComputeBusGains(Span<float> gains)
    {
        var anySolo = false;
        foreach (var bus in _buses)
            anySolo |= bus.Solo;

        for (var i = 0; i < _buses.Length; i++)
        {
            var bus = _buses[i];
            var soloedAbove = false; // this bus or an ancestor is soloed
            for (var b = bus; b is not null; b = b.Parent)
                soloedAbove |= b.Solo;
            var soloedBelow = false; // a descendant is soloed (this bus must pass it through)
            foreach (var other in _buses)
            {
                if (other.Solo && !ReferenceEquals(other, bus) && other.IsDescendantOf(bus))
                    soloedBelow = true;
            }

            var audible = !anySolo || soloedAbove || soloedBelow;
            var gain = bus.Mute || !audible ? 0f : AudioMath.DbToLinear(bus.VolumeDb);
            bus.EffectiveGain = gain;
            bus.DirectAudible = !anySolo || soloedAbove;
            gains[i] = gain;
        }
    }

    private void UpdateBusGains()
    {
        if (!_busesDirty)
            return;
        _busesDirty = false;
        Span<float> gains = stackalloc float[_buses.Length];
        ComputeBusGains(gains);
        for (var i = 0; i < gains.Length; i++)
        {
            var command = new AudioCommand { Type = AudioCommandType.SetBusGain, Index = i };
            command.Params.Gain = gains[i];
            Enqueue(command);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Playback API
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Plays <paramref name="stream"/> once without a node (UI clicks, impacts). With a <paramref name="position"/>
    /// it is positional (inverse attenuation, unit size 1, no max distance). Returns an invalid handle when the
    /// stream cannot load or every voice of the bus is busy with higher-priority sounds.
    /// </summary>
    public AudioVoiceHandle PlayOneShot(AudioStream stream, string bus = "SFX", float volumeDb = 0f, float pitchScale = 1f,
        Vector3? position = null, int priority = 0, ProcessMode processMode = ProcessMode.Pausable)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (stream.GetSource() is not { } source)
            return default;

        var busIndex = FindBus(bus);
        if (busIndex < 0)
            busIndex = 0;

        var volume = AudioMath.DbToLinear(volumeDb);
        var pitch = float.IsNaN(pitchScale) ? 1f : Math.Clamp(pitchScale, 0.01f, 16f);
        var parameters = VoiceParams.Default;
        ComputeOneShot(volume, pitch, position, ref parameters);
        stream.GetLoopFrames(source, out var loopStart, out var loopEnd);
        var handle = Play(source, busIndex, owner: null, priority, startFrame: 0, stream.Loop, loopStart, loopEnd,
            position.HasValue, parameters, processMode);
        if (!handle.IsValid)
            return handle;

        ref var slot = ref _slots[handle.Index];
        slot.OneShotVolume = volume;
        slot.OneShotPitch = pitch;
        slot.OneShotPosition = position ?? default;
        return handle;
    }

    /// <summary>Stops a voice (short fade-out). No <c>Finished</c> signal.</summary>
    public void Stop(AudioVoiceHandle handle)
    {
        if (IsCurrent(handle))
            Release(handle.Index, finished: false, sendStop: true);
    }

    /// <summary>True while the voice plays (or is paused).</summary>
    public bool IsPlaying(AudioVoiceHandle handle) => IsCurrent(handle);

    /// <summary>Playback position of a voice in seconds (as of the last audio block); 0 when not playing.</summary>
    public double GetPlaybackPosition(AudioVoiceHandle handle)
    {
        if (!IsCurrent(handle))
            return 0;
        ref var slot = ref _slots[handle.Index];
        var frames = Volatile.Read(ref _graph.Voices[handle.Index].Position);
        return slot.Source is { SampleRate: > 0 } source ? frames / source.SampleRate : 0;
    }

    /// <summary>Moves a playing voice to <paramref name="seconds"/>.</summary>
    public void Seek(AudioVoiceHandle handle, double seconds)
    {
        if (!IsCurrent(handle))
            return;
        ref var slot = ref _slots[handle.Index];
        var source = slot.Source!;
        var frame = Math.Max(0, seconds) * source.SampleRate;
        if (source.Frames > 0)
            frame = Math.Min(frame, source.Frames);
        if (source is AudioStreamSource streamSource)
        {
            // Streams restart decoding at the new position under the same voice generation.
            StartVoice(handle.Index, ref slot, streamSource, frame, slot.Last);
            if (slot.Paused)
                Enqueue(new AudioCommand { Type = AudioCommandType.PauseVoice, Index = handle.Index, Generation = handle.Generation });
            return;
        }

        Enqueue(new AudioCommand { Type = AudioCommandType.SeekVoice, Index = handle.Index, Generation = handle.Generation, Frame = frame });
    }

    /// <summary>Publishes the commands queued since the last flush to the audio thread (also done by <see cref="Process"/>).</summary>
    public void Flush()
    {
        if (_pendingCount == 0)
            return;
        var sent = _commands.EnqueueBatch(_pending.AsSpan(0, _pendingCount));
        if (sent == _pendingCount)
        {
            _pendingCount = 0;
            for (var i = 0; i < _slots.Length; i++)
                _slots[i].PendingParams = -1;
            return;
        }

        // The ring is full (the audio thread is not keeping up, or the manual null device is not being rendered):
        // keep the rest, in order, for the next flush.
        Array.Copy(_pending, sent, _pending, 0, _pendingCount - sent);
        _pendingCount -= sent;
        for (var i = 0; i < _slots.Length; i++)
            _slots[i].PendingParams = -1;
        for (var i = 0; i < _pendingCount; i++)
        {
            if (_pending[i].Type == AudioCommandType.SetVoiceParams)
                _slots[_pending[i].Index].PendingParams = i;
        }
    }

    /// <summary>
    /// Blocks until every playing streamed voice has at least <paramref name="frames"/> frames decoded ahead (or its
    /// file fully decoded), or <paramref name="timeout"/> passes. For tests and tools driving the manual null device
    /// faster than real time; returns false on timeout.
    /// </summary>
    public bool WaitForStreams(int frames, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var ready = true;
            for (var i = 0; i < _slots.Length && ready; i++)
            {
                ref var slot = ref _slots[i];
                if (!slot.Active || slot.Source is not AudioStreamSource source || _graph.Voices[i].StreamChannel is not { } channel)
                    continue;
                var buffered = channel.BufferedSamples(channel.RequestGeneration);
                ready = buffered < 0 || buffered >= (long)frames * source.Channels;
            }

            if (ready)
                return true;
            if (DateTime.UtcNow >= deadline)
                return false;
            _streamer.Wake();
            Thread.Sleep(1);
        }
    }

    /// <summary>Manual null device only: renders <paramref name="frames"/> frames on this thread (optionally capturing the stereo mix).</summary>
    public void RenderNullDevice(int frames, Span<float> capture = default)
    {
        if (_output is not NullOutput nullOutput)
            throw new InvalidOperationException("RenderNullDevice needs AudioDeviceMode.NullManual.");
        Flush();
        nullOutput.Render(frames, capture);
    }

    // ---------------------------------------------------------------------------------------------
    // Node-facing API
    // ---------------------------------------------------------------------------------------------

    internal AudioVoiceHandle PlayForOwner(IAudioVoiceOwner owner, AudioStream stream, string? bus, int priority,
        double fromSeconds, bool loop, bool positional, in VoiceParams initial)
    {
        if (_disposed || stream.GetSource() is not { } source)
            return default;
        var busIndex = FindBus(bus);
        if (busIndex < 0)
            busIndex = 0;
        stream.GetLoopFrames(source, out var loopStart, out var loopEnd);
        var startFrame = Math.Max(0, fromSeconds) * source.SampleRate;
        if (source.Frames > 0)
            startFrame = Math.Min(startFrame, source.Frames);
        return Play(source, busIndex, owner, priority, startFrame, loop || stream.Loop, loopStart, loopEnd, positional, initial);
    }

    internal void RegisterListener(AudioListener3D listener)
    {
        if (!_listeners.Contains(listener))
            _listeners.Add(listener);
        if (listener.Current)
            MakeListenerCurrent(listener, current: true);
    }

    internal void UnregisterListener(AudioListener3D listener)
    {
        _listeners.Remove(listener);
        if (ReferenceEquals(_currentListener, listener))
            _currentListener = FindFlaggedListener();
    }

    /// <summary>
    /// Only one listener is current (Godot semantics): making one current clears the others; clearing the current
    /// one falls back to another flagged listener, else to the active camera.
    /// </summary>
    internal void MakeListenerCurrent(AudioListener3D listener, bool current)
    {
        if (current)
        {
            foreach (var other in _listeners)
            {
                if (!ReferenceEquals(other, listener))
                    other.ClearCurrentFlag();
            }

            _currentListener = listener;
        }
        else if (ReferenceEquals(_currentListener, listener))
        {
            _currentListener = FindFlaggedListener();
        }
    }

    private AudioListener3D? FindFlaggedListener()
    {
        foreach (var other in _listeners)
        {
            if (other.Current)
                return other;
        }

        return null;
    }

    // ---------------------------------------------------------------------------------------------
    // Frame
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Once per frame (the scene tree calls it after transform sync): reports finished voices, applies pause, updates
    /// the listener, recomputes every playing voice's gain / pan / filter / pitch, and flushes the batch.
    /// </summary>
    public void Process(in GameTime gameTime)
    {
        if (_disposed)
            return;
        FrameIndex++;
        FrameDelta = gameTime.DeltaTime;

        DrainEvents();
        UpdateListener(gameTime.DeltaTime);
        UpdateBusGains();

        var paused = Tree?.Paused ?? false;
        for (var i = 0; i < _slots.Length; i++)
        {
            ref var slot = ref _slots[i];
            if (!slot.Active)
            {
                if (slot.CloseStreamAtFrame != 0 && FrameIndex >= slot.CloseStreamAtFrame)
                {
                    slot.CloseStreamAtFrame = 0;
                    _graph.Voices[i].StreamChannel?.Request(null, 0, false, 0, 0);
                }

                continue;
            }

            var run = slot.Owner?.VoicesActive ?? CanProcess(slot.ProcessMode, paused);
            if (run == slot.Paused)
            {
                slot.Paused = !run;
                Enqueue(new AudioCommand
                {
                    Type = run ? AudioCommandType.ResumeVoice : AudioCommandType.PauseVoice,
                    Index = i,
                    Generation = slot.Generation,
                });
            }

            var parameters = VoiceParams.Default;
            if (slot.Owner is { } owner)
                owner.UpdateVoice(this, ref parameters);
            else
                ComputeOneShot(ref slot, ref parameters);
            SendParams(i, ref slot, parameters, force: false);
        }

        UpdateMeters();
        Flush();
        _streamer.Wake();

        if (_root.Fault is { } fault && !_faultLogged)
        {
            _faultLogged = true;
            Log.Error($"[Audio] The mixer stopped after an error on the audio thread: {fault}");
        }
    }

    private static bool CanProcess(ProcessMode mode, bool paused) => mode switch
    {
        ProcessMode.Pausable or ProcessMode.Inherit => !paused,
        ProcessMode.WhenPaused => paused,
        ProcessMode.Always => true,
        _ => false,
    };

    private void ComputeOneShot(ref VoiceSlot slot, ref VoiceParams parameters) =>
        ComputeOneShot(slot.OneShotVolume, slot.OneShotPitch, slot.Positional ? slot.OneShotPosition : null, ref parameters);

    // One-shots: volume and pitch, plus (when positioned) inverse attenuation with unit size 1 and full panning.
    private void ComputeOneShot(float volume, float pitch, Vector3? position, ref VoiceParams parameters)
    {
        parameters.Gain = volume;
        parameters.Pitch = pitch;
        if (position is not { } p)
            return;
        AudioMath.ProjectToListener(_listener3D, p, 1f, out var distance, out var pan);
        parameters.Gain *= AudioMath.Attenuation(AttenuationModel.Inverse, distance, 1f, 0f);
        AudioMath.PanGains(pan, out parameters.PanLeft, out parameters.PanRight);
    }

    private void SendParams(int index, ref VoiceSlot slot, VoiceParams parameters, bool force)
    {
        if (!_buses[slot.Bus].DirectAudible)
            parameters.Gain = 0f;
        slot.Gain = parameters.Gain;
        if (!force && Close(slot.Last, parameters))
            return;
        slot.Last = parameters;

        var command = new AudioCommand
        {
            Type = AudioCommandType.SetVoiceParams,
            Index = index,
            Generation = slot.Generation,
            Params = parameters,
        };
        if (slot.PendingParams >= 0)
        {
            _pending[slot.PendingParams] = command; // latest wins: one params update per voice per batch
            return;
        }

        slot.PendingParams = _pendingCount;
        Enqueue(command);
    }

    private static bool Close(in VoiceParams a, in VoiceParams b) =>
        Math.Abs(a.Gain - b.Gain) < 1e-5f && Math.Abs(a.PanLeft - b.PanLeft) < 1e-5f && Math.Abs(a.PanRight - b.PanRight) < 1e-5f &&
        Math.Abs(a.LowPassHz - b.LowPassHz) < 0.5f && Math.Abs(a.Pitch - b.Pitch) < 1e-5f;

    private void UpdateListener(float delta)
    {
        Transform3D listener;
        if (_currentListener is { } node && node.IsInsideTree)
            listener = node.GlobalTransform;
        else if (Tree?.Root.ActiveCamera3D is { } camera)
            listener = camera.GlobalTransform;
        else
            listener = Transform3D.Identity;

        _listenerVelocity = _listenerInitialized && delta > 0f ? (listener.Origin - _listener3D.Origin) / delta : Vector3.Zero;
        _listener3D = listener;
        _listener2D = Tree?.Root.ActiveCamera2D is { } camera2D ? camera2D.GlobalPosition : Vector2.Zero;
        _listenerInitialized = true;
    }

    private void UpdateMeters()
    {
        var buses = _graph.Buses;
        for (var i = 0; i < _buses.Length && i < buses.Length; i++)
        {
            _buses[i].Peak = buses[i].Fader.Peak;
            _buses[i].ActiveVoices = 0;
        }

        foreach (ref readonly var slot in _slots.AsSpan())
        {
            if (slot.Active)
                _buses[slot.Bus].ActiveVoices++;
        }
    }

    private void DrainEvents()
    {
        while (_events.TryDequeue(out var e))
        {
            switch (e.Type)
            {
                case AudioEventType.VoiceFinished:
                    if ((uint)e.Index < (uint)_slots.Length && _slots[e.Index].Active && _slots[e.Index].Generation == e.Generation)
                        Release(e.Index, finished: true, sendStop: false);
                    break;
                case AudioEventType.GraphRetired:
                    if (e.Ref is AudioGraph graph && _retiring.Remove(graph))
                        graph.Dispose();
                    break;
            }
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Voice allocation
    // ---------------------------------------------------------------------------------------------

    private AudioVoiceHandle Play(AudioSource source, int busIndex, IAudioVoiceOwner? owner, int priority, double startFrame,
        bool loop, long loopStart, long loopEnd, bool positional, VoiceParams initial, ProcessMode processMode = ProcessMode.Pausable)
    {
        var bus = _buses[busIndex];
        var index = AllocateVoice(bus, priority);
        if (index < 0)
        {
            _rejected++;
            return default;
        }

        ref var slot = ref _slots[index];
        if (slot.CloseStreamAtFrame != 0 && source is not AudioStreamSource)
            _graph.Voices[index].StreamChannel?.Request(null, 0, false, 0, 0); // the previous sound's file: close it now
        slot.Generation++;
        slot.CloseStreamAtFrame = 0;
        if (slot.Generation == 0)
            slot.Generation = 1; // 0 means "invalid handle"
        slot.Active = true;
        slot.Paused = false;
        slot.Owner = owner;
        slot.Priority = priority;
        slot.Sequence = ++_sequence;
        slot.Bus = busIndex;
        slot.Source = source;
        slot.Positional = positional;
        slot.Loop = loop;
        slot.LoopStart = loopStart;
        slot.LoopEnd = loopEnd;
        slot.ProcessMode = processMode;
        slot.OneShotVolume = 1f;
        slot.OneShotPitch = 1f;
        if (!bus.DirectAudible)
            initial.Gain = 0f;
        slot.Gain = initial.Gain;
        slot.Last = initial;
        StartVoice(index, ref slot, source, startFrame, initial);

        // Started while it may not run (e.g. the tree is paused): pause it in the same batch, before it sounds.
        if (!(owner?.VoicesActive ?? CanProcess(processMode, Tree?.Paused ?? false)))
        {
            slot.Paused = true;
            Enqueue(new AudioCommand { Type = AudioCommandType.PauseVoice, Index = index, Generation = slot.Generation });
        }

        return new AudioVoiceHandle(index, slot.Generation);
    }

    private void StartVoice(int index, ref VoiceSlot slot, AudioSource source, double startFrame, in VoiceParams parameters)
    {
        var streamGeneration = 0;
        if (source is AudioStreamSource streamSource)
        {
            var voice = _graph.Voices[index];
            if (voice.StreamChannel is not { } channel)
            {
                channel = new AudioStreamChannel(index);
                voice.StreamChannel = channel; // published to the audio thread by the play command below
            }

            if (!channel.Registered)
            {
                _streamer.Register(channel);
                channel.Registered = true;
            }

            streamGeneration = channel.Request(streamSource, (long)startFrame, slot.Loop, slot.LoopStart, slot.LoopEnd);
            _streamer.Wake();
        }

        if (slot.PendingParams >= 0)
        {
            _pending[slot.PendingParams].Type = AudioCommandType.None; // superseded by the play
            slot.PendingParams = -1;
        }

        Enqueue(new AudioCommand
        {
            Type = AudioCommandType.PlayVoice,
            Index = index,
            Generation = slot.Generation,
            StreamGeneration = streamGeneration,
            Ref = source,
            Frame = startFrame,
            Positional = slot.Positional,
            Loop = slot.Loop,
            LoopStart = slot.LoopStart,
            LoopEnd = slot.LoopEnd,
            Params = parameters,
        });
    }

    private int AllocateVoice(AudioBus bus, int priority)
    {
        var first = bus.FirstVoice;
        var end = first + bus.MaxVoices;
        if (bus.MaxVoices == 0)
            return -1;

        // A free voice, preferring the one released longest ago (its fade-out has finished).
        var best = -1;
        for (var i = first; i < end; i++)
        {
            if (!_slots[i].Active && (best < 0 || _slots[i].FreedSequence < _slots[best].FreedSequence))
                best = i;
        }

        if (best >= 0)
            return best;

        // Steal: lowest priority, then quietest, then oldest; never a voice more important than the new sound.
        var victim = -1;
        for (var i = first; i < end; i++)
        {
            ref var s = ref _slots[i];
            if (s.Priority > priority)
                continue;
            if (victim < 0)
            {
                victim = i;
                continue;
            }

            ref var v = ref _slots[victim];
            if (s.Priority < v.Priority ||
                (s.Priority == v.Priority && (s.Gain < v.Gain - 1e-6f || (Math.Abs(s.Gain - v.Gain) <= 1e-6f && s.Sequence < v.Sequence))))
                victim = i;
        }

        if (victim < 0)
            return -1;
        _steals++;
        Release(victim, finished: false, sendStop: true);
        return victim;
    }

    /// <summary>Ends a voice on the game side: notifies its owner, frees the slot, and (optionally) tells the audio thread.</summary>
    private void Release(int index, bool finished, bool sendStop)
    {
        ref var slot = ref _slots[index];
        if (!slot.Active)
            return;
        var handle = new AudioVoiceHandle(index, slot.Generation);
        if (sendStop)
            Enqueue(new AudioCommand { Type = AudioCommandType.StopVoice, Index = index, Generation = slot.Generation });
        if (slot.PendingParams >= 0)
        {
            _pending[slot.PendingParams].Type = AudioCommandType.None;
            slot.PendingParams = -1;
        }

        var owner = slot.Owner;
        if (slot.Source is AudioStreamSource)
            slot.CloseStreamAtFrame = FrameIndex + 2; // after the stop fade; closes the file on the streaming thread
        slot.Active = false;
        slot.Paused = false;
        slot.Owner = null;
        slot.Source = null;
        slot.FreedSequence = ++_sequence;

        owner?.OnVoiceEnded(handle, finished);
    }

    internal bool IsCurrent(AudioVoiceHandle handle) =>
        handle.IsValid && (uint)handle.Index < (uint)_slots.Length && _slots[handle.Index].Active &&
        _slots[handle.Index].Generation == handle.Generation;

    private void Enqueue(in AudioCommand command)
    {
        if (_pendingCount == _pending.Length)
            Array.Resize(ref _pending, _pending.Length * 2); // rare: only when commands outpace the audio thread
        _pending[_pendingCount++] = command;
    }

    // ---------------------------------------------------------------------------------------------
    // Setup / teardown
    // ---------------------------------------------------------------------------------------------

    private static AudioBusLayout LoadLayout(string? path)
    {
        if (string.IsNullOrEmpty(path) || !ResourceLoader.Exists(path))
            return AudioBusLayout.CreateDefault();
        try
        {
            var layout = ResourceLoader.Load<AudioBusLayout>(path);
            if (layout.Validate() is { } error)
            {
                Log.Error($"[Audio] '{path}': {error}; using the default bus layout.");
                return AudioBusLayout.CreateDefault();
            }

            return layout;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.Error($"[Audio] Cannot load the bus layout '{path}' ({e.Message}); using the default layout.");
            return AudioBusLayout.CreateDefault();
        }
    }

    private static void HookSoundFlowLog()
    {
        if (Interlocked.Exchange(ref _soundFlowLogHooked, 1) != 0)
            return;
        SoundFlow.Utils.Log.OnLog += static entry =>
        {
            switch (entry.Level)
            {
                case SoundFlow.Utils.LogLevel.Warning:
                    Log.Warning($"[SoundFlow] {entry.Message}");
                    break;
                case SoundFlow.Utils.LogLevel.Error:
                case SoundFlow.Utils.LogLevel.Critical:
                    Log.Error($"[SoundFlow] {entry.Message}");
                    break;
            }
        };
    }

    private static void DisposeQuietly(IDisposable disposable)
    {
        try
        {
            disposable.Dispose();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.Warning($"[Audio] Cleanup failed: {e.Message}");
        }
    }

    /// <summary>Stops the device and the streaming thread, then releases every voice, mixer and decoder.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        for (var i = 0; i < _slots.Length; i++)
        {
            if (_slots[i].Active)
                Release(i, finished: false, sendStop: false);
        }

        DisposeQuietly(_output); // no more audio callbacks after this
        _streamer.Dispose();
        DisposeQuietly(_graph);
        foreach (var graph in _retiring)
            DisposeQuietly(graph);
        _retiring.Clear();
        _listeners.Clear();
    }

    private struct VoiceSlot
    {
        public int Generation;
        public bool Active;
        public bool Paused;
        public IAudioVoiceOwner? Owner;
        public int Priority;
        public long Sequence;
        public long FreedSequence;
        public float Gain;
        public VoiceParams Last;
        public int PendingParams;
        public int Bus;
        public AudioSource? Source;
        public bool Positional;
        public bool Loop;
        public long LoopStart;
        public long LoopEnd;
        public ProcessMode ProcessMode;
        public float OneShotVolume;
        public float OneShotPitch;
        public Vector3 OneShotPosition;
        public long CloseStreamAtFrame;
    }
}

/// <summary>A node that plays voices through the <see cref="AudioServer"/> (the audio player nodes).</summary>
internal interface IAudioVoiceOwner
{
    /// <summary>Whether the owner's voices may play now (process mode vs pause, <c>StreamPaused</c>).</summary>
    bool VoicesActive { get; }

    /// <summary>Fills this frame's mix parameters (volume × attenuation, pan, low-pass, pitch × doppler).</summary>
    void UpdateVoice(AudioServer server, ref VoiceParams parameters);

    /// <summary>The voice ended: played out (<paramref name="finished"/>), stopped or stolen.</summary>
    void OnVoiceEnded(AudioVoiceHandle handle, bool finished);
}

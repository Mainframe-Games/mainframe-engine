using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace MainframeEngine;

/// <summary>
/// The frame profiler (G10): nested CPU scopes on any thread (<see cref="ProfilerMarker"/>), GPU zones
/// (<see cref="GpuProfiler"/>), per-frame counters (<see cref="ProfilerCounter"/>), a history ring of frames with spike
/// capture and freeze, and Chrome trace export. Process-wide and static like <see cref="Log"/>, so physics, audio,
/// loaders and game code record without a tree. Records only while a consumer holds it (<see cref="Acquire"/>: the F12
/// overlay's Profiler section, the editor's Profiler tab, <c>--profile</c>); otherwise a scope is one static load and a
/// branch. Setting the <c>AppContext</c> switch <see cref="DisabledSwitch"/> compiles scopes out (<see cref="IsAvailable"/>).
/// See docs/design/profiler.md.
/// </summary>
/// <remarks>
/// Frames are bounded by the engine (<see cref="BeginFrame"/>/<see cref="EndFrame"/> on the main thread). Other threads
/// record once named (<see cref="SetThreadName"/>); their events are drained at each frame end and belong to the frame in
/// which they ended. Storage is preallocated when recording first starts; nothing allocates per frame afterwards.
/// Read the history on the main thread through <see cref="Snapshot"/>.
/// </remarks>
public static partial class Profiler
{
    /// <summary>The <c>AppContext</c> switch that turns the profiler off for the process (scopes compile to nothing).</summary>
    public const string DisabledSwitch = "MainframeEngine.Profiler.Disabled";

    /// <summary>Markers that can be registered (ids are <see cref="ushort"/>; 0 is the "unknown" marker).</summary>
    public const int MaxMarkers = 4096;

    /// <summary>Counters that can be registered.</summary>
    public const int MaxCounters = 64;

    /// <summary>The id a counter gets once the registry is full (its values are not recorded).</summary>
    public const ushort InvalidCounter = ushort.MaxValue;

    /// <summary>Thread lanes at most (0 = main; <see cref="ProfilerEvent.GpuThread"/> is the GPU's).</summary>
    public const int MaxThreads = 64;

    private const int MainBufferCapacity = 16384;
    private const int ThreadBufferCapacity = 8192;
    private const int SpikeEventCapacity = 8192;

    /// <summary>False when the <see cref="DisabledSwitch"/> is set: nothing records, scopes cost nothing.</summary>
    public static readonly bool IsAvailable = ReadAvailability();

    /// <summary>What <see cref="IsAvailable"/> is set from: false when the <see cref="DisabledSwitch"/> is on.</summary>
    internal static bool ReadAvailability() => !(AppContext.TryGetSwitch(DisabledSwitch, out var disabled) && disabled);

    // The one field every scope reads (IsAvailable && consumers > 0 && !Paused).
    internal static bool s_recording;

    private static readonly Lock s_gate = new();
    private static ProfilerOptions s_options = new();
    private static int s_consumers;
    private static int s_nextToken;
    private static readonly HashSet<int> s_tokens = [];
    private static bool s_paused;
    private static ProfilerHistory? s_history;

    // ── Markers ──────────────────────────────────────────────────────────────────────────────────────────────

    private static readonly ProfilerMarkerInfo[] s_markers = new ProfilerMarkerInfo[MaxMarkers];
    private static int s_markerCount = 1;
    private static readonly Dictionary<(string Name, ProfilerCategory Category, ProfilerMarkerKind Kind, string File, int Line), ushort> s_markerIds = [];
    private static readonly ConcurrentDictionary<string, ushort> s_literalMarkers = new(ReferenceEqualityComparer.Instance);
    private static bool s_markersFullWarned;

    // ── Counters ─────────────────────────────────────────────────────────────────────────────────────────────

    private static readonly ProfilerCounterInfo[] s_counters = new ProfilerCounterInfo[MaxCounters];
    private static int s_counterCount;
    private static readonly long[] s_counterValues = new long[MaxCounters];
    private static readonly bool[] s_counterResets = new bool[MaxCounters];

    // ── Threads ──────────────────────────────────────────────────────────────────────────────────────────────

    private static readonly ProfilerThreadBuffer?[] s_buffers = new ProfilerThreadBuffer?[MaxThreads];
    private static int s_bufferCount;
    private static int s_generation = 1;
    private static int s_mainThreadId;
    [ThreadStatic] private static ProfilerThreadBuffer? t_buffer;
    [ThreadStatic] private static string? t_threadName;
    private static bool s_threadsFullWarned;

    // ── Frame state ──────────────────────────────────────────────────────────────────────────────────────────

    private static long s_frameNumber;
    private static bool s_frameOpen;
    private static bool s_frameRecording;
    private static long s_frameStart;
    private static ProfilerThreadBuffer? s_frameScopeBuffer; // the Frame scope's (ended by marker in EndFrame)
    private static long s_frameAllocStart;
    private static readonly int[] s_gcStart = new int[3];
    private static TimeSpan s_gcPauseStart;
    private static long s_gpuExpectedFrame = -1;
    private static long s_droppedEvents;
    private static long s_unbalancedScopes;
    private static readonly bool[] s_unbalancedWarned = new bool[MaxMarkers];
    private static int s_version;
    private static long s_freezeFrame = -1;
    private static int s_freezeWait;

    static Profiler()
    {
        s_markers[0] = new ProfilerMarkerInfo(0, "(unknown)", ProfilerCategory.Engine, ProfilerMarkerKind.Cpu, "", 0);
        s_threadNames[0] = "Main";
        s_threadNames[ProfilerEvent.GpuThread] = "GPU";
    }

    private static readonly string?[] s_threadNames = new string?[256];

    /// <summary>True while scopes record (available, at least one consumer, not <see cref="Paused"/>).</summary>
    public static bool IsRecording => s_recording;

    /// <summary>Consumers holding the profiler (<see cref="Acquire"/>).</summary>
    public static int ConsumerCount
    {
        get
        {
            lock (s_gate)
                return s_consumers;
        }
    }

    /// <summary>
    /// Freezes the history: frames stop being recorded (scopes cost what they do when off) so the UI can inspect it.
    /// GPU zones of frames already recorded still arrive.
    /// </summary>
    public static bool Paused
    {
        get => s_paused;
        set
        {
            lock (s_gate)
            {
                s_paused = value;
                s_freezeFrame = -1;
                UpdateRecording();
            }
        }
    }

    /// <summary>The recorder's settings (<see cref="Configure"/>).</summary>
    public static ProfilerOptions Options => s_options;

    /// <summary>
    /// Applies <paramref name="options"/>. Sizes take effect when recording next starts from nothing (no consumer); the
    /// engine calls this from its constructor with <see cref="EngineOptions.Profiler"/>.
    /// </summary>
    public static void Configure(ProfilerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options = options.Validated();
        lock (s_gate)
        {
            var resize = s_history is { } history && (history.Options.HistoryFrames != options.HistoryFrames
                || history.Options.EventCapacity != options.EventCapacity || history.Options.MaxGpuZones != options.MaxGpuZones
                || history.Options.SpikeSlots != options.SpikeSlots);
            s_options = options;
            if (resize && s_consumers == 0)
                s_history = null;
        }
    }

    /// <summary>Records allocations per scope (<see cref="ProfilerOptions.TrackAllocations"/>).</summary>
    public static bool TrackAllocations => s_options.TrackAllocations;

    /// <summary>The audio thread records its "Mix" scope (<see cref="ProfilerOptions.AudioThread"/>).</summary>
    public static bool AudioThreadEnabled => s_options.AudioThread;

    /// <summary>
    /// A consumer starts recording (the first one allocates the history); returns the token to pass to
    /// <see cref="Release"/>. Returns 0 (and records nothing) when the profiler is not <see cref="IsAvailable"/>.
    /// </summary>
    public static int Acquire()
    {
        if (!IsAvailable)
            return 0;
        lock (s_gate)
        {
            s_history ??= new ProfilerHistory(s_options, MaxCounters, SpikeEventCapacity);
            var token = ++s_nextToken;
            s_tokens.Add(token);
            s_consumers++;
            UpdateRecording();
            return token;
        }
    }

    /// <summary>Releases a token from <see cref="Acquire"/> (unknown tokens are ignored). The history is kept for inspection.</summary>
    public static void Release(int token)
    {
        lock (s_gate)
        {
            if (!s_tokens.Remove(token))
                return;
            s_consumers--;
            UpdateRecording();
        }
    }

    private static void UpdateRecording() => Volatile.Write(ref s_recording, IsAvailable && s_consumers > 0 && !s_paused);

    /// <summary>Clears the recorded frames (the markers and counters stay registered).</summary>
    public static void Clear()
    {
        lock (s_gate)
        {
            if (s_history is { } history)
                s_history = new ProfilerHistory(history.Options, MaxCounters, SpikeEventCapacity);
            s_version++;
        }
    }

    // ── Spikes ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A frame whose CPU or GPU time exceeds this many milliseconds is kept as a spike; 0 = off.</summary>
    public static float SpikeThresholdMs { get; set; }

    /// <summary>Pauses (<see cref="Paused"/>) once a spike frame's GPU data has arrived.</summary>
    public static bool FreezeOnSpike { get; set; }

    /// <summary>Raised on the main thread with a spike frame's number.</summary>
    public static event Action<long>? SpikeCaptured;

    // ── Counters of the profiler itself ─────────────────────────────────────────────────────────────────────

    /// <summary>Scopes dropped because a thread's ring or depth stack was full (total).</summary>
    public static long DroppedEvents => Interlocked.Read(ref s_droppedEvents);

    /// <summary>Scopes ended out of order, or still open at their frame's end (total; each is closed, never thrown).</summary>
    public static long UnbalancedScopes => Interlocked.Read(ref s_unbalancedScopes);

    /// <summary>True between <see cref="BeginFrame"/> and <see cref="EndFrame"/> of a frame that is being recorded.</summary>
    internal static bool IsFrameRecording => s_frameOpen && s_frameRecording;

    /// <summary>The current (or last) frame's number (<see cref="BeginFrame"/> counts every frame, recorded or not).</summary>
    public static long FrameNumber => s_frameNumber;

    /// <summary>Changes whenever the history does (a frame recorded, GPU zones arrived, cleared): the UI's refresh key.</summary>
    public static int Version => Volatile.Read(ref s_version);

    /// <summary>Read access to the history, markers, counters and spikes (main thread).</summary>
    public static ProfilerSnapshot Snapshot { get; } = new();

    internal static ProfilerHistory? History => s_history;

    // ── Scope API ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A scope named by a string literal, registered on first use and looked up by reference afterwards (allocation-free
    /// once registered). Prefer a static <see cref="ProfilerMarker"/> in hot paths.
    /// </summary>
    public static ProfilerScope Scope(string literalName)
    {
        if (!IsAvailable || !s_recording)
            return default;
        if (!s_literalMarkers.TryGetValue(literalName, out var id))
        {
            id = RegisterMarker(literalName, ProfilerCategory.Game, ProfilerMarkerKind.Cpu, "", 0);
            s_literalMarkers.TryAdd(literalName, id);
        }

        return BeginScope(id);
    }

    /// <summary>
    /// Names this thread's lane (<c>"Loader"</c>, <c>"Audio"</c>) and lets it record: threads other than the main one
    /// record only once named. Null stops this thread recording.
    /// </summary>
    public static void SetThreadName(string? name)
    {
        t_threadName = name;
        if (t_buffer is { } buffer && buffer.Generation == s_generation)
        {
            if (name is null && buffer.Index != 0)
            {
                buffer.CloseAll(Stopwatch.GetTimestamp());
                buffer.MarkFree(); // a thread of the same name may reuse the lane
                t_buffer = null;
            }
            else if (name is not null && buffer.Name != name)
            {
                buffer.Name = name;
                s_threadNames[buffer.Index] = name;
            }
        }
    }

    /// <summary>The thread whose frames the profiler records (the engine's; set by the engine and the headless host).</summary>
    internal static void SetMainThread()
    {
        s_mainThreadId = Environment.CurrentManagedThreadId;
        if (t_buffer is { Index: not 0 })
            t_buffer = null; // a buffer this thread had as a background thread
    }

    internal static bool IsMainThread => s_mainThreadId == Environment.CurrentManagedThreadId;

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static ProfilerScope BeginScope(ushort marker)
    {
        var buffer = t_buffer;
        if (buffer is null || buffer.Generation != s_generation)
        {
            buffer = CreateBuffer();
            if (buffer is null)
                return default;
        }

        return buffer.Begin(marker, s_options.TrackAllocations);
    }

    internal static void EndMarker(ushort marker)
    {
        if (t_buffer is { } buffer && buffer.Generation == s_generation && buffer.Depth > 0)
            buffer.EndMarker(marker);
    }

    // One-time per thread (outside any steady-state window): the main thread, or a thread named by SetThreadName.
    private static ProfilerThreadBuffer? CreateBuffer()
    {
        var main = IsMainThread || s_mainThreadId == 0;
        var name = t_threadName;
        if (!main && name is null)
            return null;
        lock (s_gate)
        {
            if (s_mainThreadId == 0)
                s_mainThreadId = Environment.CurrentManagedThreadId;
            main = IsMainThread;
            byte index;
            if (main)
            {
                index = 0;
                if (s_buffers[0] is { } existing && existing.Generation == s_generation)
                    return t_buffer = existing; // the main thread's buffer survives a thread-static reset
            }
            else
            {
                for (var i = 1; i < s_bufferCount; i++)
                    if (s_buffers[i] is { IsFree: true } free && free.Generation == s_generation && free.Name == name && free.TryClaim())
                        return t_buffer = free;
                if (s_bufferCount == 0)
                    s_bufferCount = 1; // lane 0 is reserved for the main thread
                if (s_bufferCount >= MaxThreads)
                {
                    if (!s_threadsFullWarned)
                    {
                        s_threadsFullWarned = true;
                        Log.Warning($"[Profiler] More than {MaxThreads - 1} threads record; '{name}' is not recorded.");
                    }

                    return null;
                }

                index = (byte)s_bufferCount++;
            }

            var buffer = new ProfilerThreadBuffer(s_generation, index, main ? "Main" : name!, main ? MainBufferCapacity : ThreadBufferCapacity);
            s_buffers[index] = buffer;
            if (main && s_bufferCount == 0)
                s_bufferCount = 1;
            s_threadNames[index] = buffer.Name;
            return t_buffer = buffer;
        }
    }

    /// <summary>The name of thread lane <paramref name="thread"/> (null when unused).</summary>
    public static string? ThreadName(byte thread) => s_threadNames[thread];

    /// <summary>Thread lanes in use (lane 0 .. count - 1; the GPU lane is separate).</summary>
    public static int ThreadCount => Math.Max(1, Volatile.Read(ref s_bufferCount));

    // ── Registries ───────────────────────────────────────────────────────────────────────────────────────────

    internal static ushort RegisterMarker(string name, ProfilerCategory category, ProfilerMarkerKind kind, string file, int line)
    {
        lock (s_gate)
        {
            var key = (name, category, kind, file, line);
            if (s_markerIds.TryGetValue(key, out var existing))
                return existing;
            if (s_markerCount >= MaxMarkers)
            {
                if (!s_markersFullWarned)
                {
                    s_markersFullWarned = true;
                    Log.Warning($"[Profiler] More than {MaxMarkers - 1} markers; '{name}' records as '(unknown)'.");
                }

                return 0;
            }

            var id = (ushort)s_markerCount;
            s_markers[id] = new ProfilerMarkerInfo(id, name, category, kind, file, line);
            s_markerIds[key] = id;
            Volatile.Write(ref s_markerCount, id + 1); // published after the entry: readers on other threads see it whole
            return id;
        }
    }

    /// <summary>Markers registered so far (ids 0 .. count - 1).</summary>
    public static int MarkerCount => Volatile.Read(ref s_markerCount);

    /// <summary>Marker <paramref name="id"/> (the "unknown" marker for ids not registered).</summary>
    public static ProfilerMarkerInfo MarkerAt(ushort id) => id < MarkerCount ? s_markers[id] : s_markers[0];

    internal static string MarkerName(ushort id) => MarkerAt(id).Name;

    internal static ushort RegisterCounter(string name, ProfilerCounterUnit unit)
    {
        lock (s_gate)
        {
            for (var i = 0; i < s_counterCount; i++)
                if (s_counters[i].Name == name)
                    return (ushort)i;
            if (s_counterCount >= MaxCounters)
                return InvalidCounter;
            var id = (ushort)s_counterCount;
            s_counters[id] = new ProfilerCounterInfo(id, name, unit);
            Volatile.Write(ref s_counterCount, id + 1);
            return id;
        }
    }

    /// <summary>Counters registered so far.</summary>
    public static int CounterCount => Volatile.Read(ref s_counterCount);

    public static ProfilerCounterInfo CounterAt(ushort id) =>
        id < CounterCount ? s_counters[id] : new ProfilerCounterInfo(InvalidCounter, "(invalid)", ProfilerCounterUnit.Count);

    internal static long CounterValue(ushort id) => id < MaxCounters ? Volatile.Read(ref s_counterValues[id]) : 0;

    internal static void SetCounter(ushort id, long value)
    {
        if (id < MaxCounters)
            Volatile.Write(ref s_counterValues[id], value);
    }

    internal static void AddCounter(ushort id, long value)
    {
        if (id >= MaxCounters)
            return;
        Interlocked.Add(ref s_counterValues[id], value);
        s_counterResets[id] = true;
    }

    // ── Frames ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts a frame (the engine calls it at the start of its update; <see cref="HeadlessHost"/> around its step). Only the
    /// main thread's calls count. An open frame is ended first.
    /// </summary>
    internal static void BeginFrame()
    {
        if (!IsAvailable || (s_mainThreadId != 0 && !IsMainThread))
            return;
        if (s_mainThreadId == 0)
            s_mainThreadId = Environment.CurrentManagedThreadId;
        if (s_frameOpen)
            EndFrame(float.NaN, 0f);

        s_frameNumber++;
        for (var i = 0; i < s_counterResets.Length; i++)
            if (s_counterResets[i])
                Volatile.Write(ref s_counterValues[i], 0);

        s_frameOpen = true;
        s_frameRecording = s_recording && s_history is not null;
        if (!s_frameRecording)
            return;
        s_frameStart = Stopwatch.GetTimestamp();
        s_frameAllocStart = GC.GetAllocatedBytesForCurrentThread();
        for (var g = 0; g < 3; g++)
            s_gcStart[g] = GC.CollectionCount(g);
        s_gcPauseStart = GC.GetTotalPauseDuration();
        s_frameScopeBuffer = null;
        var buffer = t_buffer is { } existing && existing.Generation == s_generation ? existing : CreateBuffer();
        if (buffer is null)
            return;
        buffer.Begin(EngineMarkers.Frame.Id, s_options.TrackAllocations);
        s_frameScopeBuffer = buffer;
    }

    /// <summary>
    /// Ends the frame: closes scopes left open on the main thread (counted), drains every thread's events into the history,
    /// samples the GC counters and records the frame with <paramref name="cpuMs"/> (NaN: the frame's wall time) and
    /// <paramref name="waitMs"/>.
    /// </summary>
    internal static void EndFrame(float cpuMs, float waitMs)
    {
        if (!IsAvailable || !s_frameOpen || !IsMainThread)
            return;
        s_frameOpen = false;
        var history = s_history;
        var recording = s_frameRecording && history is not null;
        var end = Stopwatch.GetTimestamp();
        if (recording && s_frameScopeBuffer is { } mainBuffer && mainBuffer.Depth > 0)
        {
            mainBuffer.EndMarker(EngineMarkers.Frame.Id);
            mainBuffer.CloseAll(end);
        }

        s_frameScopeBuffer = null;
        lock (s_gate)
        {
            history = s_history; // Clear() may have replaced it
            if (!recording || history is null)
            {
                DrainBuffers(null);
                return;
            }

            var sequence = history.FramesWritten;
            ref var frame = ref history.FrameAt(sequence);
            frame = default;
            frame.Number = s_frameNumber;
            frame.Sequence = sequence;
            frame.StartTicks = s_frameStart;
            frame.EndTicks = end;
            frame.CpuMs = float.IsNaN(cpuMs) ? (float)((end - s_frameStart) * 1000.0 / Stopwatch.Frequency) : cpuMs;
            frame.WaitMs = waitMs;
            frame.GpuMs = float.NaN;
            frame.GpuPending = s_gpuExpectedFrame == s_frameNumber;
            frame.EventFirst = history.EventsWritten;
            DrainBuffers(history);
            frame.EventCount = (int)(history.EventsWritten - frame.EventFirst);
            frame.GpuFirst = history.GpuWritten;

            SampleGc();
            var counters = history.CountersOf(sequence);
            var count = Math.Min(CounterCount, counters.Length);
            for (var i = 0; i < count; i++)
                counters[i] = Volatile.Read(ref s_counterValues[i]);
            counters[count..].Clear();
            history.FramesWritten = sequence + 1;

            var threshold = SpikeThresholdMs;
            if (threshold > 0f && frame.CpuMs > threshold)
                CaptureSpike(history, sequence);
            s_version++;
            CheckFreeze(history);
        }

        RaiseSpike();
    }

    private static void DrainBuffers(ProfilerHistory? history)
    {
        var count = Volatile.Read(ref s_bufferCount);
        long dropped = 0, unbalanced = 0;
        for (var i = 0; i < Math.Max(1, count); i++)
        {
            if (s_buffers[i] is not { } buffer || buffer.Generation != s_generation)
                continue;
            var published = buffer.Peek(out var first, out var second);
            if (history is not null)
            {
                history.AppendEvents(first);
                history.AppendEvents(second);
            }

            buffer.CompleteDrain(published);
            dropped += buffer.Dropped;
            unbalanced += buffer.Unbalanced;
            if (buffer.LastUnbalancedMarker is var marker and >= 0 && !s_unbalancedWarned[marker])
            {
                s_unbalancedWarned[marker] = true;
                Log.Warning($"[Profiler] Scope '{MarkerName((ushort)marker)}' was ended out of order or left open at a frame end; it was closed there.");
            }
        }

        Interlocked.Exchange(ref s_droppedEvents, dropped);
        Interlocked.Exchange(ref s_unbalancedScopes, unbalanced);
    }

    private static void SampleGc()
    {
        EngineCounters.ManagedAllocated.Set(GC.GetAllocatedBytesForCurrentThread() - s_frameAllocStart);
        EngineCounters.Gen0.Set(GC.CollectionCount(0) - s_gcStart[0]);
        EngineCounters.Gen1.Set(GC.CollectionCount(1) - s_gcStart[1]);
        EngineCounters.Gen2.Set(GC.CollectionCount(2) - s_gcStart[2]);
        EngineCounters.GcPause.Set((long)Math.Round((GC.GetTotalPauseDuration() - s_gcPauseStart).TotalMilliseconds * 1000));
    }

    // ── GPU zones ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The GPU recorded zones for frame <paramref name="frameNumber"/> (its read-back follows a few frames later).</summary>
    internal static void ExpectGpu(long frameNumber) => s_gpuExpectedFrame = frameNumber;

    /// <summary>
    /// Hands in the GPU zones of frame <paramref name="frameNumber"/> (already aligned to the CPU clock, in begin order) and its
    /// GPU time. Main thread. Frames no longer in the history are ignored.
    /// </summary>
    internal static void SubmitGpu(long frameNumber, ReadOnlySpan<ProfilerEvent> zones, float gpuMs)
    {
        if (!IsAvailable || s_history is null)
            return;
        lock (s_gate)
        {
            if (s_history is not { } history)
                return;
            var sequence = history.FindSequence(frameNumber, 8);
            if (sequence < 0)
                return;
            ref var frame = ref history.FrameAt(sequence);
            frame.GpuFirst = history.GpuWritten;
            frame.GpuCount = zones.Length;
            history.AppendGpu(zones);
            frame.GpuMs = gpuMs;
            frame.GpuPending = false;
            var threshold = SpikeThresholdMs;
            if (threshold > 0f && (gpuMs > threshold || frame.IsSpike))
                CaptureSpike(history, sequence);
            s_version++;
            CheckFreeze(history);
        }

        RaiseSpike();
    }

    /// <summary>A frame's GPU data that never came (the frame was dropped by the renderer): stop waiting for it.</summary>
    internal static void CancelGpu(long frameNumber)
    {
        lock (s_gate)
        {
            if (s_history is not { } history)
                return;
            var sequence = history.FindSequence(frameNumber, 8);
            if (sequence < 0)
                return;
            history.FrameAt(sequence).GpuPending = false;
            CheckFreeze(history);
        }
    }

    // ── Spikes ───────────────────────────────────────────────────────────────────────────────────────────────

    private static void CaptureSpike(ProfilerHistory history, long sequence)
    {
        ref var frame = ref history.FrameAt(sequence);
        var newly = !frame.IsSpike;
        frame.IsSpike = true;
        if (history.Spikes.Length == 0)
            return;
        ProfilerSpikeSlot? slot = null;
        foreach (var candidate in history.Spikes)
            if (candidate.Used && candidate.Frame.Number == frame.Number)
                slot = candidate;
        if (slot is null)
        {
            // The oldest (or a free) slot.
            slot = history.Spikes[0];
            foreach (var candidate in history.Spikes)
            {
                if (!candidate.Used)
                {
                    slot = candidate;
                    break;
                }

                if (candidate.CapturedOrder < slot.CapturedOrder)
                    slot = candidate;
            }

            slot.Used = true;
            slot.CapturedOrder = ++history.SpikeOrder;
        }

        slot.Frame = frame;
        slot.EventCount = history.EventsOf(frame).CopyTo(slot.Events);
        slot.GpuCount = history.GpuOf(frame).CopyTo(slot.Gpu);
        history.CountersOf(sequence).CopyTo(slot.Counters);
        if (newly)
        {
            if (FreezeOnSpike && s_freezeFrame < 0)
            {
                s_freezeFrame = frame.Number;
                s_freezeWait = 0;
            }

            s_spikeToRaise = frame.Number; // raised by the caller once the gate is released
        }
    }

    private static long s_spikeToRaise = -1;

    private static void RaiseSpike()
    {
        var number = s_spikeToRaise;
        if (number < 0)
            return;
        s_spikeToRaise = -1;
        SpikeCaptured?.Invoke(number);
    }

    // FreezeOnSpike: pause once the spike frame's GPU data is in (or after a few frames without any).
    private static void CheckFreeze(ProfilerHistory history)
    {
        if (s_freezeFrame < 0)
            return;
        var sequence = history.FindSequence(s_freezeFrame, 16);
        if (sequence >= 0 && history.FrameAt(sequence).GpuPending && ++s_freezeWait < 8)
            return;
        s_freezeFrame = -1;
        s_paused = true;
        UpdateRecording();
    }

    // ── Tests ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Back to a fresh state for this thread as the main thread (consumers, history, buffers; markers stay).</summary>
    internal static void ResetForTests(ProfilerOptions? options = null)
    {
        lock (s_gate)
        {
            s_tokens.Clear();
            s_consumers = 0;
            s_paused = false;
            s_options = (options ?? new ProfilerOptions()).Validated();
            s_history = null;
            s_generation++;
            Array.Clear(s_buffers);
            s_bufferCount = 0;
            s_frameOpen = false;
            s_frameRecording = false;
            s_frameScopeBuffer = null;
            s_gpuExpectedFrame = -1;
            s_freezeFrame = -1;
            SpikeThresholdMs = 0f;
            FreezeOnSpike = false;
            SpikeCaptured = null;
            Interlocked.Exchange(ref s_droppedEvents, 0);
            Interlocked.Exchange(ref s_unbalancedScopes, 0);
            Array.Clear(s_counterValues);
            Array.Clear(s_counterResets);
            for (var i = 1; i < s_threadNames.Length; i++)
                if (i != ProfilerEvent.GpuThread)
                    s_threadNames[i] = null;
            UpdateRecording();
        }

        t_buffer = null;
        t_threadName = null;
        s_mainThreadId = Environment.CurrentManagedThreadId;
    }
}

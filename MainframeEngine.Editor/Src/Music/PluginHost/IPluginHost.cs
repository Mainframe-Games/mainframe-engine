namespace MainframeEngine.Editor.Music;

/// <summary>
/// The music editor's out-of-process helper (<c>mfplughost</c>, ADR 0146): today Vorbis encoding; the VST3 phase adds
/// plugin scanning, loading, state, latency, block processing and editor windows, the MIDI phase input devices.
/// <see cref="PluginHostClient"/> runs the real helper; tests use a fake. Requests are synchronous (renders and, later,
/// the render thread call them) and serialised; a request that fails because the helper died, timed out or was cancelled
/// throws, and the next request starts a new helper (<see cref="Restarted"/> tells owners to reload plugin state).
/// </summary>
public interface IPluginHost : IDisposable
{
    /// <summary>A helper is running and has answered the handshake.</summary>
    bool IsRunning { get; }

    /// <summary>The running helper's handshake, or null.</summary>
    PluginHostInfo? Info { get; }

    /// <summary>Raised (on the requesting thread) when a running helper stops unexpectedly: it crashed, the connection dropped, or a request timed out.</summary>
    event Action<PluginHostStop>? Stopped;

    /// <summary>Raised after a helper was started again following <see cref="Stopped"/>: the hook to reload plugin states.</summary>
    event Action<PluginHostInfo>? Restarted;

    /// <summary>Starts the helper if it is not running (no-op otherwise) and returns its handshake.</summary>
    PluginHostInfo Start();

    /// <summary>A round trip: proves the helper is alive and responsive within <paramref name="timeout"/>.</summary>
    void Ping(TimeSpan? timeout = null);

    /// <summary>
    /// Encodes a WAV (16-bit PCM or 32-bit float, mono or stereo) to Ogg Vorbis at VBR <paramref name="quality"/> (0–10).
    /// The helper writes a temporary file next to <paramref name="oggPath"/> and renames it over the output, so a failure
    /// leaves a previous file intact.
    /// </summary>
    PluginHostEncodeResult Encode(string wavPath, string oggPath, int quality, CancellationToken cancellation = default);

    // ── VST3 (ADR 0147). Instances are the helper's ids; audio and note events travel through the shared memory
    // (PluginSharedMemory) mapped by SetupSharedMemory. MIDI phase (delivery 4): devices and input events.

    /// <summary>Maps the editor's shared memory file in the helper (again after every restart).</summary>
    void SetupSharedMemory(string path, long size);

    /// <summary>Loads a VST3 class from a bundle into shared memory slot <paramref name="slot"/>, activated and processing.</summary>
    PluginLoadResult LoadPlugin(string bundlePath, string classId, int slot, int sampleRate, int maxBlock);

    void UnloadPlugin(uint instance);

    /// <summary>The plugin's state: <c>u32 length + component state, u32 length + controller state</c> (opaque to callers).</summary>
    byte[] GetPluginState(uint instance);

    /// <summary>Restores a state returned by <see cref="GetPluginState"/>.</summary>
    void SetPluginState(uint instance, ReadOnlySpan<byte> state);

    int GetPluginLatency(uint instance);

    /// <summary>Switches every instance between real-time and offline (render) processing.</summary>
    void SetPluginsOffline(bool offline);

    void OpenPluginEditor(uint instance, string title);

    void ClosePluginEditor(uint instance);

    /// <summary>
    /// Render thread: one <c>PluginProcess</c> round trip (<see cref="PluginHostMessage.PluginProcess"/> payload). Never
    /// starts the helper and never allocates. A reply later than <paramref name="deadline"/> is
    /// <see cref="PluginProcessResult.Missed"/> (the late reply is consumed by the next call, which misses until it came).
    /// </summary>
    PluginProcessResult ProcessPlugins(ReadOnlySpan<byte> payload, TimeSpan deadline);

    /// <summary>UI thread: an instance whose editor window the user closed (helper notification), if any.</summary>
    bool TryTakeClosedEditor(out uint instance);
}

/// <summary>What <see cref="IPluginHost.LoadPlugin"/> loaded.</summary>
public readonly record struct PluginLoadResult(uint Instance, int LatencyFrames, int InputChannels, int OutputChannels, bool HasEditor, string Name);

/// <summary>How a block's plugin round trip went.</summary>
public enum PluginProcessResult
{
    /// <summary>Processed: the outputs in shared memory are this block's.</summary>
    Ok,

    /// <summary>No reply within the deadline (or the host was busy): the block is silence, an xrun.</summary>
    Missed,

    /// <summary>The helper is not running, stopped, or answered with an error.</summary>
    Failed,
}

/// <summary>A helper's handshake.</summary>
/// <param name="ProtocolVersion">The control protocol version it speaks (<see cref="PluginHostProtocol.Version"/>).</param>
/// <param name="HelperVersion">Its build version.</param>
/// <param name="Capabilities">What it was built with.</param>
/// <param name="ProcessId">Its process id.</param>
public sealed record PluginHostInfo(uint ProtocolVersion, string HelperVersion, PluginHostCapabilities Capabilities, int ProcessId);

/// <summary>Features a helper was built with.</summary>
[Flags]
public enum PluginHostCapabilities : uint
{
    None = 0,
    Encode = 1 << 0,
    Vst3 = 1 << 1,
    Midi = 1 << 2,
}

/// <summary>What an encode wrote.</summary>
public readonly record struct PluginHostEncodeResult(long Frames, int SampleRate, int Channels);

/// <summary>Why a helper stopped.</summary>
/// <param name="Reason">A sentence for the Output panel ("Plugin host stopped (exit code 139)").</param>
/// <param name="Request">The request in flight, if any ("encode").</param>
public sealed record PluginHostStop(string Reason, string? Request);

/// <summary>The helper is missing, could not start, failed a request or stopped during one.</summary>
public class PluginHostException : Exception
{
    public PluginHostException()
    {
    }

    public PluginHostException(string message)
        : base(message)
    {
    }

    public PluginHostException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The helper did not answer within the request's timeout (it is stopped; the next request restarts it).</summary>
public sealed class PluginHostTimeoutException : PluginHostException
{
    public PluginHostTimeoutException()
    {
    }

    public PluginHostTimeoutException(string message)
        : base(message)
    {
    }

    public PluginHostTimeoutException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

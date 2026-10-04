using System.Diagnostics;
using SoundFlow.Abstracts;
using SoundFlow.Abstracts.Devices;
using SoundFlow.Backends.MiniAudio;
using SoundFlow.Backends.MiniAudio.Devices;
using SoundFlow.Enums;
using SoundFlow.Structs;

namespace MainframeEngine.Audio;

/// <summary>
/// Where the mix goes: a real playback device through SoundFlow/miniaudio, or the null device. Owns the SoundFlow
/// <see cref="AudioEngine"/> every graph component is created with.
/// </summary>
internal abstract class AudioOutput : IDisposable
{
    protected AudioOutput(AudioEngine engine, AudioFormat format, string name)
    {
        Engine = engine;
        Format = format;
        Name = name;
    }

    public AudioEngine Engine { get; }

    /// <summary>The mix format: 32-bit float stereo at the device rate.</summary>
    public AudioFormat Format { get; }

    public string Name { get; }

    public abstract bool IsNull { get; }

    /// <summary>Connects <paramref name="root"/> and starts pulling audio from it.</summary>
    public abstract void Start(AudioMixRoot root);

    public abstract void Dispose();

    public static AudioFormat StereoFloat(int sampleRate) => new()
    {
        Format = SampleFormat.F32,
        Channels = 2,
        Layout = ChannelLayout.Stereo,
        SampleRate = sampleRate,
    };
}

/// <summary>The default playback device through SoundFlow's miniaudio backend (follows OS default-device changes).</summary>
internal sealed class SoundFlowOutput : AudioOutput
{
    private readonly MiniAudioEngine _engine;
    private readonly AudioPlaybackDevice _device;

    private SoundFlowOutput(MiniAudioEngine engine, AudioPlaybackDevice device, AudioFormat format, string name)
        : base(engine, format, name)
    {
        _engine = engine;
        _device = device;
    }

    public override bool IsNull => false;

    /// <summary>Opens the default playback device; null (with the reason) when there is none or it fails.</summary>
    public static SoundFlowOutput? TryOpen(int sampleRate, int bufferMilliseconds, out string? failure)
    {
        MiniAudioEngine? engine = null;
        try
        {
            engine = new MiniAudioEngine();
            engine.UpdateAudioDevicesInfo();
            if (engine.PlaybackDevices.Length == 0)
            {
                failure = $"no playback device ({engine.ActiveBackend} backend)";
                engine.Dispose();
                return null;
            }

            var format = StereoFloat(sampleRate);
            var config = new MiniAudioDeviceConfig
            {
                PeriodSizeInMilliseconds = (uint)Math.Clamp(bufferMilliseconds, 2, 100),
                Playback = new DeviceSubConfig(),
                Capture = new DeviceSubConfig(),
            };
            var device = engine.InitializePlaybackDevice(null, format, config);
            var info = device.Info;
            var name = $"{info?.Name ?? "default device"} ({engine.ActiveBackend})";
            failure = null;
            return new SoundFlowOutput(engine, device, format, name);
        }
        catch (Exception e)
        {
            // DllNotFoundException (no miniaudio for this platform), InvalidOperationException (no device / init
            // failure), BackendException, ... — never fatal: the server falls back to the null device.
            failure = $"{e.GetType().Name}: {e.Message}";
            try
            {
                engine?.Dispose();
            }
            catch (Exception disposeError)
            {
                Log.Warning($"[Audio] Disposing the failed audio engine threw: {disposeError.Message}");
            }

            return null;
        }
    }

    public override void Start(AudioMixRoot root)
    {
        _device.MasterMixer.Volume = AudioGraph.UnityVolume;
        _device.MasterMixer.AddComponent(root);
        _device.Start();
    }

    public override void Dispose()
    {
        try
        {
            _device.Stop();
            _device.Dispose();
        }
        finally
        {
            _engine.Dispose();
        }
    }
}

/// <summary>
/// The silent device used when no audio device exists (CI, servers) or audio is disabled for tests. In
/// <see cref="NullAudioMode.Realtime"/> a background thread pulls blocks at real-time pace and discards them, so
/// playback positions advance and <c>Finished</c> still fires; in <see cref="NullAudioMode.Manual"/> nothing runs
/// until <see cref="Render"/> is called (deterministic unit tests). No native code is involved.
/// </summary>
internal sealed class NullOutput : AudioOutput
{
    private readonly NullAudioMode _mode;
    private readonly int _blockFrames;
    private AudioMixRoot? _root;
    private float[] _buffer = [];
    private Thread? _thread;
    private volatile bool _stop;

    public NullOutput(int sampleRate, int bufferMilliseconds, NullAudioMode mode, string reason)
        : base(new NullAudioEngine(), StereoFloat(sampleRate), "Null device (" + reason + ")")
    {
        _mode = mode;
        _blockFrames = Math.Max(32, sampleRate * Math.Clamp(bufferMilliseconds, 1, 100) / 1000);
    }

    public override bool IsNull => true;

    public override void Start(AudioMixRoot root)
    {
        _root = root;
        _buffer = new float[_blockFrames * 2];
        if (_mode != NullAudioMode.Realtime)
            return;
        _thread = new Thread(RunRealtime) { Name = "Mainframe null audio device", IsBackground = true };
        _thread.Start();
    }

    /// <summary>Manual mode: renders <paramref name="frames"/> frames on the calling thread (in device-sized blocks).</summary>
    public void Render(int frames, Span<float> capture = default)
    {
        if (_mode != NullAudioMode.Manual)
            throw new InvalidOperationException("Render is only available on a manual null device.");
        var root = _root ?? throw new InvalidOperationException("The null device has not been started.");
        var offset = 0;
        while (frames > 0)
        {
            var block = Math.Min(frames, _blockFrames);
            var span = _buffer.AsSpan(0, block * 2);
            span.Clear();
            root.Process(span, 2);
            if (!capture.IsEmpty)
            {
                var take = Math.Min(span.Length, capture.Length - offset);
                if (take > 0)
                    span[..take].CopyTo(capture[offset..]);
                offset += span.Length;
            }

            frames -= block;
        }
    }

    private void RunRealtime()
    {
        var root = _root!;
        var period = TimeSpan.FromSeconds((double)_blockFrames / Format.SampleRate);
        var clock = Stopwatch.StartNew();
        var due = TimeSpan.Zero;
        while (!_stop)
        {
            var span = _buffer.AsSpan();
            span.Clear();
            root.Process(span, 2);
            due += period;
            var wait = due - clock.Elapsed;
            if (wait > TimeSpan.Zero)
                Thread.Sleep(wait);
            else if (wait < -10 * period)
                due = clock.Elapsed; // fell far behind (debugger, suspended): resynchronise instead of bursting
        }
    }

    public override void Dispose()
    {
        _stop = true;
        _thread?.Join(TimeSpan.FromSeconds(2));
        _thread = null;
        Engine.Dispose();
    }
}

/// <summary>How the null audio device advances time.</summary>
public enum NullAudioMode
{
    /// <summary>A background thread renders (and discards) audio in real time.</summary>
    Realtime,

    /// <summary>Nothing renders until the test calls <c>AudioServer.RenderNullDevice</c>.</summary>
    Manual,
}

/// <summary>
/// A SoundFlow engine with no backend: SoundFlow components (mixers, players) need an engine instance, and this one
/// lets the whole graph run without miniaudio — the null device and unit tests.
/// </summary>
internal sealed class NullAudioEngine : AudioEngine
{
    protected override void CleanupBackend()
    {
    }

    public override AudioPlaybackDevice InitializePlaybackDevice(DeviceInfo? deviceInfo, AudioFormat format, DeviceConfig? config = null) =>
        throw new NotSupportedException("The null audio engine has no devices.");

    public override AudioCaptureDevice InitializeCaptureDevice(DeviceInfo? deviceInfo, AudioFormat format, DeviceConfig? config = null) =>
        throw new NotSupportedException("The null audio engine has no devices.");

    public override FullDuplexDevice InitializeFullDuplexDevice(DeviceInfo? playbackDeviceInfo, DeviceInfo? captureDeviceInfo,
        AudioFormat format, DeviceConfig? config = null) =>
        throw new NotSupportedException("The null audio engine has no devices.");

    public override AudioCaptureDevice InitializeLoopbackDevice(AudioFormat format, DeviceConfig? config = null) =>
        throw new NotSupportedException("The null audio engine has no devices.");

    public override AudioPlaybackDevice SwitchDevice(AudioPlaybackDevice oldDevice, DeviceInfo newDeviceInfo, DeviceConfig? config = null) =>
        throw new NotSupportedException("The null audio engine has no devices.");

    public override AudioCaptureDevice SwitchDevice(AudioCaptureDevice oldDevice, DeviceInfo newDeviceInfo, DeviceConfig? config = null) =>
        throw new NotSupportedException("The null audio engine has no devices.");

    public override FullDuplexDevice SwitchDevice(FullDuplexDevice oldDevice, DeviceInfo? newPlaybackInfo, DeviceInfo? newCaptureInfo,
        DeviceConfig? config = null) =>
        throw new NotSupportedException("The null audio engine has no devices.");

    public override void UpdateAudioDevicesInfo()
    {
    }
}

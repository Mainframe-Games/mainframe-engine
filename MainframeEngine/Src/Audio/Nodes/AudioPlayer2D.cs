using System.Numerics;
using MainframeEngine.Audio;

namespace MainframeEngine;

/// <summary>
/// Plays an <see cref="AudioStream"/> positioned in 2D (Godot's <c>AudioStreamPlayer2D</c>): attenuated by distance
/// from the 2D listener (the active <see cref="Camera2D"/>, else the origin) and panned by its horizontal offset.
/// </summary>
/// <remarks>Gain is <c>(1 − d / MaxDistance)^Attenuation</c>; pan is the horizontal offset over
/// <see cref="AudioServer.PanDistance2D"/>, scaled by <see cref="PanningStrength"/>.</remarks>
[EditorIcon("volume", Family = EditorIconFamily.Audio)]
public class AudioPlayer2D : Node2D, IAudioVoiceOwner
{
    private readonly AudioPlayback _playback = new();
    private float _volumeDb;
    private float _pitchScale = 1f;
    private long _cachedFrame = -1;
    private VoiceParams _cached;

    [Export]
    public AudioStream? Stream { get; set; }

    [Export]
    public string Bus { get; set; } = AudioBusLayout.MasterBus;

    [Export(Range = "-80,24,0.1")]
    public float VolumeDb
    {
        get => _volumeDb;
        set => _volumeDb = float.IsNaN(value) ? 0f : Math.Clamp(value, AudioMath.SilenceDb, 24f);
    }

    [Export(Range = "0.01,4,0.01")]
    public float PitchScale
    {
        get => _pitchScale;
        set => _pitchScale = float.IsNaN(value) ? 1f : Math.Clamp(value, 0.01f, 16f);
    }

    [Export]
    public bool Autoplay { get; set; }

    [Export]
    public bool Loop { get; set; }

    [Export(Range = "1,64,1")]
    public int MaxPolyphony { get; set; } = 1;

    [Export(Range = "-100,100,1")]
    public int Priority { get; set; }

    /// <summary>Distance (2D units) at which the sound becomes silent.</summary>
    [ExportGroup("Attenuation")]
    [Export(Range = "1,100000,1")]
    public float MaxDistance { get; set; } = 2000f;

    /// <summary>Curve exponent: 1 is linear, higher falls off faster near the source.</summary>
    [Export(Range = "0.01,16,0.01")]
    public float Attenuation { get; set; } = 1f;

    /// <summary>Scales the left/right pan (0 = centred).</summary>
    [Export(Range = "0,4,0.01")]
    public float PanningStrength { get; set; } = 1f;

    public bool StreamPaused { get; set; }

    public bool Playing => _playback.IsPlaying;

    [Signal]
    public event Action? Finished;

    public void Play(float fromSeconds = 0f)
    {
        if (_playback.Server is { } server)
            _playback.Play(this, Stream, Bus, Priority, fromSeconds, Loop, positional: true, MaxPolyphony, Compute(server));
    }

    public void Stop() => _playback.StopAll();

    public void Seek(float seconds) => _playback.Seek(seconds);

    public double GetPlaybackPosition() => _playback.GetPlaybackPosition();

    /// <summary>Gain and pan for an emitter at <paramref name="offset"/> from the listener (exposed for tests).</summary>
    public static void Spatialize(Vector2 offset, float maxDistance, float attenuation, float panningStrength, float panDistance,
        out float gain, out float pan)
    {
        var distance = offset.Length();
        var max = Math.Max(maxDistance, 1e-3f);
        gain = distance >= max ? 0f : MathF.Pow(1f - distance / max, Math.Max(attenuation, 0.01f));
        pan = Math.Clamp(offset.X / Math.Max(panDistance, 1e-3f) * panningStrength, -1f, 1f);
    }

    protected override void OnEnterTree()
    {
        base.OnEnterTree();
        _playback.Attach(this, Stream);
        _cachedFrame = -1;
    }

    protected override void OnReady()
    {
        base.OnReady();
        if (Autoplay)
            Play();
    }

    protected override void OnExitTree()
    {
        _playback.Detach();
        base.OnExitTree();
    }

    private VoiceParams Compute(AudioServer server)
    {
        if (_cachedFrame == server.FrameIndex)
            return _cached;
        Spatialize(GlobalPosition - server.Listener2D, MaxDistance, Attenuation, PanningStrength, server.PanDistance2D,
            out var gain, out var pan);
        var parameters = VoiceParams.Default;
        parameters.Gain = AudioMath.DbToLinear(_volumeDb) * gain;
        parameters.Pitch = _pitchScale;
        AudioMath.PanGains(pan, out parameters.PanLeft, out parameters.PanRight);
        _cached = parameters;
        _cachedFrame = server.FrameIndex;
        return parameters;
    }

    bool IAudioVoiceOwner.VoicesActive => !StreamPaused && CanProcess();

    void IAudioVoiceOwner.UpdateVoice(AudioServer server, ref VoiceParams parameters) => parameters = Compute(server);

    void IAudioVoiceOwner.OnVoiceEnded(AudioVoiceHandle handle, bool finished)
    {
        if (_playback.OnVoiceEnded(handle) && finished)
            Finished?.Invoke();
    }
}

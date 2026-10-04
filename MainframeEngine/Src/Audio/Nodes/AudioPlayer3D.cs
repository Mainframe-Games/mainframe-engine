using System.Numerics;
using MainframeEngine.Audio;

namespace MainframeEngine;

/// <summary>
/// Plays an <see cref="AudioStream"/> positioned in 3D (Godot's <c>AudioStreamPlayer3D</c>). Each frame the emitter is
/// projected into the listener's space (the current <see cref="AudioListener3D"/>, else the active
/// <see cref="Camera3D"/>): the full 3D distance drives the engine attenuation curve (and the optional distance
/// low-pass), the horizontal direction drives the stereo pan. Elevation does not pan. Optional doppler.
/// </summary>
/// <remarks>
/// Stereo or wider sources are folded to mono before panning. Changes are smoothed on the audio thread
/// (<c>SpatialSmoother</c>), so moving emitters do not zipper.
/// </remarks>
public class AudioPlayer3D : Node3D, IAudioVoiceOwner
{
    private readonly AudioPlayback _playback = new();
    private float _volumeDb;
    private float _pitchScale = 1f;
    private long _cachedFrame = -1;
    private VoiceParams _cached;
    private Vector3 _lastPosition;
    private long _lastPositionFrame = -1;

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

    [ExportGroup("Attenuation")]
    [Export]
    public AttenuationModel AttenuationModel { get; set; } = AttenuationModel.Inverse;

    /// <summary>Distance within which the sound plays at full volume (world units).</summary>
    [Export(Range = "0.01,1000,0.01")]
    public float UnitSize { get; set; } = 10f;

    /// <summary>Beyond this distance the sound is silent; 0 = no limit.</summary>
    [Export(Range = "0,100000,0.1")]
    public float MaxDistance { get; set; }

    /// <summary>Steepness of the Inverse, InverseSquare, Linear and Exponential models.</summary>
    [Export(Range = "0,16,0.01")]
    public float RolloffFactor { get; set; } = 1f;

    /// <summary>Gains (0..1) sampled evenly from distance 0 to the max distance, for <see cref="AttenuationModel.Custom"/>.</summary>
    [Export]
    public float[]? CustomAttenuationCurve { get; set; }

    /// <summary>
    /// Cutoff (Hz) of a low-pass that closes from fully open at the unit size to this value at the max distance
    /// (100 × unit size when unlimited), muffling far sounds; 0 = off.
    /// </summary>
    [Export(Range = "0,20000,1")]
    public float LowPassAtMaxDistance { get; set; }

    [ExportGroup("Panning")]
    [Export(Range = "0,4,0.01")]
    public float PanningStrength { get; set; } = 1f;

    /// <summary>Shift pitch with the emitter's and listener's velocities (see <see cref="AudioServer.SpeedOfSound"/>).</summary>
    [Export]
    public bool DopplerTracking { get; set; }

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

    protected override void OnEnterTree()
    {
        base.OnEnterTree();
        _playback.Attach(this, Stream);
        _cachedFrame = -1;
        _lastPositionFrame = -1;
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

        var position = GlobalPosition;
        var listener = server.Listener3D;
        AudioMath.ProjectToListener(listener, position, PanningStrength, out var distance, out var pan);

        var parameters = VoiceParams.Default;
        var attenuation = AudioMath.Attenuation(AttenuationModel, distance, UnitSize, MaxDistance, RolloffFactor, CustomAttenuationCurve);
        parameters.Gain = AudioMath.DbToLinear(_volumeDb) * attenuation;
        AudioMath.PanGains(pan, out parameters.PanLeft, out parameters.PanRight);

        if (LowPassAtMaxDistance > 0f)
        {
            var unit = Math.Max(UnitSize, 1e-3f);
            var range = MaxDistance > unit ? MaxDistance : 100f * unit;
            var t = Math.Clamp((distance - unit) / (range - unit), 0f, 1f);
            parameters.LowPassHz = 20000f + (Math.Min(LowPassAtMaxDistance, 20000f) - 20000f) * t;
        }

        var pitch = _pitchScale;
        if (DopplerTracking && server.DopplerScale > 0f)
        {
            var velocity = _lastPositionFrame >= 0 && server.FrameDelta > 0f
                ? (position - _lastPosition) / server.FrameDelta
                : Vector3.Zero;
            pitch *= AudioMath.DopplerFactor(listener.Origin, server.ListenerVelocity, position, velocity, server.SpeedOfSound,
                server.DopplerScale);
        }

        _lastPosition = position;
        _lastPositionFrame = server.FrameIndex;
        parameters.Pitch = pitch;
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

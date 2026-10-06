using System.Numerics;
using MainframeEngine.Audio;

namespace MainframeEngine;

/// <summary>
/// Plays an <see cref="AudioStream"/> positioned in 2D (Godot's <c>AudioStreamPlayer2D</c>): attenuated by distance
/// from the 2D listener (the centre of the root viewport's view) and panned by its horizontal place on screen.
/// </summary>
/// <remarks>Godot's <c>_update_panning</c> without an <c>AudioListener2D</c>: gain is <c>(1 − d / MaxDistance)^Attenuation</c>
/// (d in canvas units from the view centre, silent beyond <see cref="MaxDistance"/>); pan is the screen x offset from the
/// centre over the visible width, clamped to ±1, times <see cref="PanningStrength"/> ×
/// <see cref="AudioServer.PanningStrength2D"/> × 0.5, around 0.5; left = 1 − pan, right = pan (linear: 0.5 each at the
/// centre).</remarks>
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

    /// <summary>
    /// Godot's 2D gain and per-channel pan gains for an emitter at <paramref name="emitter"/> (canvas units), heard from
    /// <paramref name="listener"/> (the view centre), with the view's <paramref name="canvasTransform"/> and visible
    /// <paramref name="screenSize"/> (exposed for tests).
    /// </summary>
    public static void Spatialize(Vector2 emitter, Vector2 listener, in Transform2D canvasTransform, Vector2 screenSize, float maxDistance,
        float attenuation, float panningStrength, float globalPanningStrength, out float gain, out float left, out float right)
    {
        var distance = Vector2.Distance(emitter, listener);
        if (distance > maxDistance || maxDistance <= 0f)
        {
            gain = left = right = 0f; // Godot: this viewport cannot hear it
            return;
        }

        gain = MathF.Pow(1f - distance / maxDistance, attenuation);
        var relative = canvasTransform.TransformPoint(emitter) - screenSize * 0.5f;
        var pan = Math.Clamp(screenSize.X > 0f ? relative.X / screenSize.X : 0f, -1f, 1f);
        pan = Math.Clamp(pan * panningStrength * globalPanningStrength * 0.5f + 0.5f, 0f, 1f);
        left = 1f - pan;
        right = pan;
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
        Spatialize(GlobalPosition, server.Listener2D, server.CanvasTransform2D, server.ScreenSize2D, MaxDistance, Attenuation, PanningStrength,
            server.PanningStrength2D, out var gain, out var left, out var right);
        var parameters = VoiceParams.Default;
        parameters.Gain = AudioMath.DbToLinear(_volumeDb) * gain;
        parameters.Pitch = _pitchScale;
        parameters.PanLeft = left;
        parameters.PanRight = right;
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

using MainframeEngine.Audio;

namespace MainframeEngine;

/// <summary>
/// Plays an <see cref="AudioStream"/> without position — music, UI, ambience beds (Godot's <c>AudioStreamPlayer</c>).
/// Pauses with the tree according to its <see cref="Node.ProcessMode"/>; stops when it leaves the tree.
/// </summary>
[EditorIcon("volume", Family = EditorIconFamily.Audio)]
public class AudioPlayer : Node, IAudioVoiceOwner
{
    private readonly AudioPlayback _playback = new();
    private float _volumeDb;
    private float _pitchScale = 1f;

    /// <summary>The sound to play.</summary>
    [Export]
    public AudioStream? Stream { get; set; }

    /// <summary>Bus the voices play on (falls back to Master when no such bus exists).</summary>
    [Export]
    public string Bus { get; set; } = AudioBusLayout.MasterBus;

    [Export(Range = "-80,24,0.1")]
    public float VolumeDb
    {
        get => _volumeDb;
        set => _volumeDb = float.IsNaN(value) ? 0f : Math.Clamp(value, AudioMath.SilenceDb, 24f);
    }

    /// <summary>Playback rate (pitch and speed together); 2 is an octave up.</summary>
    [Export(Range = "0.01,4,0.01")]
    public float PitchScale
    {
        get => _pitchScale;
        set => _pitchScale = float.IsNaN(value) ? 1f : Math.Clamp(value, 0.01f, 16f);
    }

    /// <summary>Starts playing when the node is ready.</summary>
    [Export]
    public bool Autoplay { get; set; }

    /// <summary>Loops (in addition to <see cref="AudioStream.Loop"/>).</summary>
    [Export]
    public bool Loop { get; set; }

    /// <summary>Voices this node may play at once; playing again restarts the oldest.</summary>
    [Export(Range = "1,64,1")]
    public int MaxPolyphony { get; set; } = 1;

    /// <summary>Voice-stealing priority: a full bus steals lower priorities first and never steals higher ones.</summary>
    [Export(Range = "-100,100,1")]
    public int Priority { get; set; }

    /// <summary>Pauses the node's voices (in addition to tree pause).</summary>
    public bool StreamPaused { get; set; }

    /// <summary>True while at least one voice plays (or is paused).</summary>
    public bool Playing => _playback.IsPlaying;

    /// <summary>Emitted when a voice plays to its end (not on <see cref="Stop"/>, stealing or looping).</summary>
    [Signal]
    public event Action? Finished;

    /// <summary>Plays from <paramref name="fromSeconds"/>.</summary>
    public void Play(float fromSeconds = 0f) =>
        _playback.Play(this, Stream, Bus, Priority, fromSeconds, Loop, positional: false, MaxPolyphony, CurrentParams());

    public void Stop() => _playback.StopAll();

    public void Seek(float seconds) => _playback.Seek(seconds);

    /// <summary>Position of the newest voice in seconds.</summary>
    public double GetPlaybackPosition() => _playback.GetPlaybackPosition();

    protected override void OnEnterTree()
    {
        base.OnEnterTree();
        _playback.Attach(this, Stream);
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

    private VoiceParams CurrentParams()
    {
        var parameters = VoiceParams.Default;
        parameters.Gain = AudioMath.DbToLinear(_volumeDb);
        parameters.Pitch = _pitchScale;
        return parameters;
    }

    bool IAudioVoiceOwner.VoicesActive => !StreamPaused && CanProcess();

    void IAudioVoiceOwner.UpdateVoice(AudioServer server, ref VoiceParams parameters) => parameters = CurrentParams();

    void IAudioVoiceOwner.OnVoiceEnded(AudioVoiceHandle handle, bool finished)
    {
        if (_playback.OnVoiceEnded(handle) && finished)
            Finished?.Invoke();
    }
}

using MainframeEngine.Audio;
using MainframeEngine.Audio.Music;

namespace MainframeEngine;

/// <summary>
/// Plays baked <see cref="AdaptiveMusic"/> (<c>.mmusic</c>): sections, layers that follow parameters, beat-quantized
/// transitions and stingers, all from a few parameter values set by game code (FMOD-style). A thin node over an
/// <see cref="AdaptiveMusicInstance"/>.
/// </summary>
/// <remarks>
/// Like <see cref="AudioPlayer"/> it stops when it leaves the tree, follows its <see cref="Node.ProcessMode"/> against
/// <see cref="SceneTree.Paused"/> (the music clock stops while paused), and is silent in the editor's edit mode. It
/// preloads its stems when it enters the tree. See docs/design/future/adaptive-music.md.
/// </remarks>
[EditorIcon("music", Family = EditorIconFamily.Audio)]
public class AdaptiveMusicPlayer : Node, IAdaptiveMusicOwner
{
    private AdaptiveMusicInstance? _instance;
    private AdaptiveMusic? _music;
    private string _bus = "Music";
    private float _volumeDb;
    private float _scheduleAhead = 0.1f;

    /// <summary>The music to play.</summary>
    [Export]
    public AdaptiveMusic? Music
    {
        get => _music;
        set
        {
            _music = value;
            if (_instance is not null)
                _instance.Music = value;
        }
    }

    /// <summary>The bus to play on; it needs a free music voice (<see cref="AudioBusInfo.MusicVoices"/>; 2 on Music by default).</summary>
    [Export]
    public string Bus
    {
        get => _bus;
        set
        {
            _bus = value;
            if (_instance is not null)
                _instance.Bus = value;
        }
    }

    [Export(Range = "-80,24,0.1")]
    public float VolumeDb
    {
        get => _volumeDb;
        set
        {
            _volumeDb = float.IsNaN(value) ? 0f : Math.Clamp(value, AudioMath.SilenceDb, 24f);
            if (_instance is not null)
                _instance.VolumeDb = _volumeDb;
        }
    }

    /// <summary>Starts playing when the node is ready.</summary>
    [Export]
    public bool Autoplay { get; set; }

    /// <summary>The section <see cref="Autoplay"/> and <see cref="Play"/> start at ("" = the music's start section).</summary>
    [Export]
    public string StartSection { get; set; } = "";

    /// <summary>How far ahead transitions are scheduled, in seconds (a boundary closer than this moves to the next quantum).</summary>
    [Export(Range = "0,1,0.01")]
    public float ScheduleAhead
    {
        get => _scheduleAhead;
        set
        {
            _scheduleAhead = float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 0.1f;
            if (_instance is not null)
                _instance.ScheduleAhead = _scheduleAhead;
        }
    }

    /// <summary>True from <see cref="Play"/> until the music has ended or stopped.</summary>
    public bool Playing => _instance?.Playing ?? false;

    /// <summary>The section playing, or null.</summary>
    public string? CurrentSection => _instance?.CurrentSection;

    /// <summary>The destination of a scheduled transition, or null.</summary>
    public string? PendingTransition => _instance?.PendingTransition;

    /// <summary>The instance behind the node while it is in the tree (null in the editor's edit mode and outside the tree).</summary>
    public AdaptiveMusicInstance? Instance => _instance;

    /// <summary>A section started (bars and beats restart at 1).</summary>
    [Signal]
    public event Action<string>? SectionStarted;

    /// <summary>A beat: the bar and the beat in it, both 1-based within the section.</summary>
    [Signal]
    public event Action<int, int>? Beat;

    /// <summary>A bar (1-based within the section), just before its first beat.</summary>
    [Signal]
    public event Action<int>? Bar;

    /// <summary>The music crossed a marker.</summary>
    [Signal]
    public event Action<string>? Marker;

    /// <summary>A transition was scheduled: its destination and the seconds until it happens.</summary>
    [Signal]
    public event Action<string, float>? TransitionScheduled;

    /// <summary>The music ended by its authored flow (not on <see cref="Stop"/>).</summary>
    [Signal]
    public event Action? Finished;

    /// <summary>Starts at a section's downbeat (null: <see cref="StartSection"/>, else the music's start).</summary>
    public void Play(string? section = null) => _instance?.Play(string.IsNullOrEmpty(section) ? StartSection : section);

    /// <summary>Stops at the next <paramref name="quantize"/> boundary, fading over <paramref name="fadeBeats"/>; tails ring out.</summary>
    public void Stop(MusicQuantize quantize = MusicQuantize.Immediate, float fadeBeats = 0.5f) => _instance?.Stop(quantize, fadeBeats);

    /// <inheritdoc cref="AdaptiveMusicInstance.SetParameter(string, float)"/>
    public void SetParameter(string name, float value) => _instance?.SetParameter(name, value);

    /// <inheritdoc cref="AdaptiveMusicInstance.SetParameter(string, string)"/>
    public void SetParameter(string name, string label) => _instance?.SetParameter(name, label);

    /// <inheritdoc cref="AdaptiveMusicInstance.SetParameter(int, float)"/>
    public void SetParameter(int index, float value) => _instance?.SetParameter(index, value);

    /// <inheritdoc cref="AdaptiveMusicInstance.SetParameterImmediate(string, float)"/>
    public void SetParameterImmediate(string name, float value) => _instance?.SetParameterImmediate(name, value);

    /// <inheritdoc cref="AdaptiveMusicInstance.SetParameterImmediate(string, string)"/>
    public void SetParameterImmediate(string name, string label) => _instance?.SetParameterImmediate(name, label);

    /// <inheritdoc cref="AdaptiveMusicInstance.SetParameterImmediate(int, float)"/>
    public void SetParameterImmediate(int index, float value) => _instance?.SetParameterImmediate(index, value);

    /// <inheritdoc cref="AdaptiveMusicInstance.GetParameter(string)"/>
    public float GetParameter(string name) => _instance?.GetParameter(name) ?? 0f;

    /// <inheritdoc cref="AdaptiveMusicInstance.GetParameterIndex"/>
    public int GetParameterIndex(string name) => _instance?.GetParameterIndex(name) ?? -1;

    /// <inheritdoc cref="AdaptiveMusicInstance.TriggerStinger"/>
    public void TriggerStinger(string name, MusicQuantize? quantize = null) => _instance?.TriggerStinger(name, quantize);

    /// <inheritdoc cref="AdaptiveMusicInstance.GoToSection"/>
    public void GoToSection(string name, MusicQuantize quantize = MusicQuantize.Bar, float fadeOutBeats = 0.5f) =>
        _instance?.GoToSection(name, quantize, fadeOutBeats);

    /// <inheritdoc cref="AdaptiveMusicInstance.GetPosition"/>
    public MusicPosition GetPosition() => _instance?.GetPosition() ?? default;

    protected override void OnEnterTree()
    {
        base.OnEnterTree();
        // Edit mode (the editor) is silent: no server, so Play and Autoplay do nothing.
        if (Tree is not { EditMode: false } tree || tree.Servers.Get<AudioServer>() is not { } server)
            return;
        _music?.Preload();
        _instance = new AdaptiveMusicInstance(server, _music)
        {
            Owner = this,
            Bus = _bus,
            VolumeDb = _volumeDb,
            ScheduleAhead = _scheduleAhead,
        };
        _instance.SectionStarted += OnSectionStarted;
        _instance.Beat += OnBeat;
        _instance.Bar += OnBar;
        _instance.Marker += OnMarker;
        _instance.TransitionScheduled += OnTransitionScheduled;
        _instance.Finished += OnFinished;
    }

    protected override void OnReady()
    {
        base.OnReady();
        if (Autoplay)
            Play();
    }

    protected override void OnExitTree()
    {
        if (_instance is { } instance)
        {
            instance.Dispose();
            _instance = null;
        }

        base.OnExitTree();
    }

    bool IAdaptiveMusicOwner.MusicActive => CanProcess();

    private void OnSectionStarted(string section) => SectionStarted?.Invoke(section);

    private void OnBeat(int bar, int beat) => Beat?.Invoke(bar, beat);

    private void OnBar(int bar) => Bar?.Invoke(bar);

    private void OnMarker(string name) => Marker?.Invoke(name);

    private void OnTransitionScheduled(string to, float seconds) => TransitionScheduled?.Invoke(to, seconds);

    private void OnFinished() => Finished?.Invoke();
}

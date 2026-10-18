using MainframeEngine.Audio;
using MainframeEngine.Audio.Music;

namespace MainframeEngine;

/// <summary>Where adaptive music is: the section, its 1-based bar and beat, the fraction into the beat, and seconds into the section.</summary>
public readonly record struct MusicPosition(string? Section, int Bar, int Beat, float BeatFraction, double Seconds);

/// <summary>What an <see cref="AdaptiveMusicInstance"/> asks of the node that owns it.</summary>
internal interface IAdaptiveMusicOwner
{
    /// <summary>Whether the music may play now (process mode against the tree's pause).</summary>
    bool MusicActive { get; }
}

/// <summary>
/// One playing piece of <see cref="AdaptiveMusic"/> on an <see cref="AudioServer"/>, without a scene tree (the
/// <see cref="AdaptiveMusicPlayer"/> node is a thin wrapper over it; the editor's Baked audition uses it directly).
/// </summary>
/// <remarks>
/// <para><b>The game thread decides, the audio thread executes.</b> Every frame (<see cref="AudioServer.Process"/>) the
/// instance applies what the audio thread reported (sections started, misses, the end), advances its
/// <see cref="MusicDirector"/> to the reported music clock and turns the director's actions into commands for its
/// music voice, scheduled at least <see cref="ScheduleAhead"/> ahead (plus the stream prefill when bodies stream). A
/// transition the audio thread could not carry out (a streamed lane not ready, no lane group free) is a miss: the
/// director schedules it again at the next quantum, and <see cref="AudioServerStats.LateMusicTransitions"/> counts it.</para>
/// <para><b>Signals</b> come from the reported clock and the audio thread's events: <see cref="SectionStarted"/>,
/// <see cref="Bar"/>, <see cref="Beat"/> and <see cref="Marker"/> for every grid line or marker crossed since the last
/// frame, in order (bars and beats 1-based within the section). Their arguments are the manifest's own strings.</para>
/// <para>Game thread only. Steady playback, parameter changes, transitions, stingers, misses and pause/resume allocate
/// nothing; <see cref="Play"/> loads the stems the first time.</para>
/// </remarks>
public sealed class AdaptiveMusicInstance : IDisposable
{
    private const int HeldCapacity = 8;
    private const int InboxCapacity = 64;
    private const float PrefillSeconds = 0.05f;
    private const float MissMarginSeconds = 0.02f;

    private readonly AudioServer _server;
    private readonly MusicAction[] _actions = new MusicAction[MusicDirector.MaxActionsPerUpdate];
    private readonly MusicAction[] _held = new MusicAction[HeldCapacity];
    private readonly AudioEvent[] _inbox = new AudioEvent[InboxCapacity];
    private int _heldCount;
    private int _inboxCount;

    private AdaptiveMusic? _music;
    private MusicDirector? _director;
    private MusicRuntime? _runtime;
    private float[] _sentGain = [];
    private float[] _sentCutoff = [];
    private int _sentLayerVersion;
    private float _volumeDb;
    private float _sentVolume;
    private bool _paused;

    private int _voice = -1;
    private int _generation;
    private int _prefetchAction;
    private int _prefetchSection = -1;
    private long _prefetchFrame;
    private int _nextPrefetchId = -1;
    private int _lastPendingSection = -1;
    private long _lastPendingFrame = -1;

    // What the audio thread reported: the section playing, its pass, and how far signals have been raised.
    private int _section = -1;
    private long _origin;
    private long _signalFrame = -1;
    private long _clock;
    private float _sinceClock;
    private HashSet<string>? _warned;

    public AdaptiveMusicInstance(AudioServer server, AdaptiveMusic? music = null)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));
        Music = music;
    }

    /// <summary>The music to play; changing it stops the current one.</summary>
    public AdaptiveMusic? Music
    {
        get => _music;
        set
        {
            if (ReferenceEquals(_music, value))
                return;
            StopImmediately();
            _music = value;
            _director = value is null ? null : new MusicDirector(value.Plan);
            var layers = value?.Plan.Layers.Length ?? 0;
            _sentGain = new float[layers];
            _sentCutoff = new float[layers];
        }
    }

    /// <summary>The bus the music plays on (it needs a free music voice there, <see cref="AudioBusInfo.MusicVoices"/>).</summary>
    public string Bus { get; set; } = "Music";

    public float VolumeDb
    {
        get => _volumeDb;
        set => _volumeDb = float.IsNaN(value) ? 0f : Math.Clamp(value, AudioMath.SilenceDb, 24f);
    }

    /// <summary>
    /// How far ahead transitions are scheduled, in seconds (0.1). The audio thread must get the command before the
    /// frame; a boundary closer than this moves to the next quantum.
    /// </summary>
    public float ScheduleAhead { get; set; } = 0.1f;

    /// <summary>Holds the music (its clock stops; parameter seeks wait too).</summary>
    public bool Paused { get; set; }

    /// <summary>True from <see cref="Play"/> until the music has ended or stopped and every tail has rung out.</summary>
    public bool Playing => _voice >= 0;

    /// <summary>The section playing (as reported by the audio thread), or null.</summary>
    public string? CurrentSection => _section >= 0 && _director is { } d ? d.Plan.Sections[_section].Id : null;

    /// <summary>The destination of a scheduled transition, or null.</summary>
    public string? PendingTransition => _director is { PendingSection: >= 0 } d && Playing ? d.Plan.Sections[d.PendingSection].Id : null;

    /// <summary>The logic (parameters, layer values, the scheduled transition); null without music.</summary>
    public MusicDirector? Director => _director;

    internal IAdaptiveMusicOwner? Owner { get; set; }

    /// <summary>The music voice and its play (tests); −1 while not playing.</summary>
    internal int VoiceIndex => _voice;

    internal int VoiceGeneration => _generation;

    /// <summary>A section started (not on a loop of the same section).</summary>
    public event Action<string>? SectionStarted;

    /// <summary>A beat: the bar and the beat in it, both 1-based within the section.</summary>
    public event Action<int, int>? Beat;

    /// <summary>A bar (1-based within the section), raised just before its first <see cref="Beat"/>.</summary>
    public event Action<int>? Bar;

    /// <summary>The music crossed a marker.</summary>
    public event Action<string>? Marker;

    /// <summary>A transition was scheduled: its destination and the seconds until it happens.</summary>
    public event Action<string, float>? TransitionScheduled;

    /// <summary>The music ended by its authored flow (a section without continuation) and its tails rang out. Not on stop.</summary>
    public event Action? Finished;

    // ---------------------------------------------------------------------------------------------
    // Control
    // ---------------------------------------------------------------------------------------------

    /// <summary>Starts at <paramref name="section"/>'s downbeat (null or empty: the music's start section).</summary>
    public void Play(string? section = null)
    {
        if (_music is null || _director is not { } director)
        {
            Warn("play", "[Audio] AdaptiveMusicInstance.Play: no music set.");
            return;
        }

        StopImmediately();
        var plan = director.Plan;
        var index = plan.StartSection;
        if (!string.IsNullOrEmpty(section))
        {
            index = plan.FindSection(section);
            if (index < 0 || plan.Sections[index].Kind != MusicSectionKind.Section)
            {
                Warn(section, $"[Audio] Adaptive music: '{section}' is not a playable section; Play ignored.");
                return;
            }
        }

        if (index < 0)
        {
            Warn("start", "[Audio] Adaptive music has no playable section.");
            return;
        }

        var runtime = _music.GetRuntime();
        var voice = _server.AcquireMusicVoice(this, Bus, runtime);
        if (voice < 0)
            return;

        _voice = voice;
        _generation = _server.MusicGeneration(voice);
        _runtime = runtime;
        _heldCount = 0;
        _inboxCount = 0;
        (_prefetchAction, _prefetchSection, _prefetchFrame) = (0, -1, 0);
        (_lastPendingSection, _lastPendingFrame) = (-1, -1);
        (_section, _origin, _signalFrame, _clock, _sinceClock) = (-1, 0, -1, 0, 0);
        _paused = !Active;
        _sentVolume = AudioMath.DbToLinear(_volumeDb);
        var play = new AudioCommand
        {
            Type = AudioCommandType.MusicPlay,
            Index = voice,
            Generation = _generation,
            Ref = runtime,
            StartPaused = _paused,
        };
        play.Params.Gain = _sentVolume;
        _server.EnqueueMusic(play);

        director.Play(index);
        SendLayers(snap: true);
        Pump(0);
    }

    /// <summary>Stops at the next <paramref name="quantize"/> boundary, fading over <paramref name="fadeBeats"/>; tails ring out.</summary>
    public void Stop(MusicQuantize quantize = MusicQuantize.Immediate, float fadeBeats = 0.5f)
    {
        if (Playing && _director is { IsPlaying: true } director)
            director.Stop(quantize, fadeBeats);
    }

    /// <summary>Stops now (a few milliseconds' fade), without <see cref="Finished"/>.</summary>
    public void StopImmediately()
    {
        if (_voice < 0)
            return;
        _server.ReleaseMusicVoice(this, _voice, _generation, halt: true);
        EndPlay();
    }

    /// <summary>Sets a parameter (clamped; continuous parameters seek at their speed). Unknown names are ignored with one warning.</summary>
    public void SetParameter(string name, float value) => SetByName(name, value, immediate: false);

    /// <summary>Sets a labeled parameter to one of its labels.</summary>
    public void SetParameter(string name, string label) => SetLabel(name, label, immediate: false);

    /// <summary>Sets a parameter by index (<see cref="GetParameterIndex"/>), skipping the name lookup.</summary>
    public void SetParameter(int index, float value) => SetByIndex(index, value, immediate: false);

    /// <summary><see cref="SetParameter(string, float)"/> without the seek.</summary>
    public void SetParameterImmediate(string name, float value) => SetByName(name, value, immediate: true);

    /// <summary><see cref="SetParameter(string, string)"/> without the seek.</summary>
    public void SetParameterImmediate(string name, string label) => SetLabel(name, label, immediate: true);

    /// <summary><see cref="SetParameter(int, float)"/> without the seek.</summary>
    public void SetParameterImmediate(int index, float value) => SetByIndex(index, value, immediate: true);

    /// <summary>The parameter's current (smoothed) value; 0 for an unknown name.</summary>
    public float GetParameter(string name)
    {
        var index = GetParameterIndex(name);
        return index >= 0 ? _director!.Parameters.GetValue(index) : 0f;
    }

    /// <summary>The parameter's current (smoothed) value by index.</summary>
    public float GetParameter(int index) =>
        _director is { } d && (uint)index < (uint)d.Parameters.Count ? d.Parameters.GetValue(index) : 0f;

    /// <summary>The parameter's index, or −1.</summary>
    public int GetParameterIndex(string name) => _director?.Parameters.IndexOf(name) ?? -1;

    /// <summary>Fires a stinger at its quantum (or <paramref name="quantize"/>), its pickup first.</summary>
    public void TriggerStinger(string name, MusicQuantize? quantize = null)
    {
        if (_director is not { } d)
            return;
        var index = d.Plan.FindSection(name);
        if (index < 0 || d.Plan.Sections[index].Kind != MusicSectionKind.Stinger)
        {
            Warn(name, $"[Audio] Adaptive music: no stinger '{name}'; TriggerStinger ignored.");
            return;
        }

        if (Playing)
            d.TriggerStinger(index, quantize);
    }

    /// <summary>Goes to a section at the next <paramref name="quantize"/> boundary; replaces a scheduled transition.</summary>
    public void GoToSection(string name, MusicQuantize quantize = MusicQuantize.Bar, float fadeOutBeats = 0.5f)
    {
        if (_director is not { } d)
            return;
        var index = d.Plan.FindSection(name);
        if (index < 0 || d.Plan.Sections[index].Kind != MusicSectionKind.Section)
        {
            Warn(name, $"[Audio] Adaptive music: '{name}' is not a playable section; GoToSection ignored.");
            return;
        }

        if (Playing)
            d.GoToSection(index, quantize, fadeOutBeats);
    }

    /// <summary>
    /// The position for visual sync: the last reported clock moved on by the time since it was reported, minus the
    /// output latency estimate.
    /// </summary>
    public MusicPosition GetPosition()
    {
        if (_section < 0 || _director is not { } d)
            return default;
        var plan = d.Plan;
        var section = plan.Sections[_section];
        var rate = plan.Grid.SampleRate;
        var moving = !_paused ? Math.Min(_sinceClock, 0.1f) : 0f;
        var local = _clock + (moving - _server.OutputLatencySeconds) * (double)rate - _origin;
        if (section.Loops && section.LengthFrames > 0)
            local = ((local % section.LengthFrames) + section.LengthFrames) % section.LengthFrames;
        local = Math.Max(0, local);
        var beats = local / plan.Grid.FramesPerBeat;
        var beat = (long)Math.Floor(beats);
        var bpb = plan.Grid.BeatsPerBar;
        return new MusicPosition(section.Id, (int)(beat / bpb) + 1, (int)(beat % bpb) + 1, (float)(beats - beat), local / rate);
    }

    public void Dispose()
    {
        StopImmediately();
        Owner = null;
    }

    // ---------------------------------------------------------------------------------------------
    // Frame (the server)
    // ---------------------------------------------------------------------------------------------

    private bool Active => !Paused && (Owner?.MusicActive ?? true);

    /// <summary>Server: an event from this instance's voice (queued; applied in order at <see cref="Update"/>).</summary>
    internal void OnMusicEvent(in AudioEvent e)
    {
        if (_inboxCount < InboxCapacity)
            _inbox[_inboxCount++] = e;
    }

    /// <summary>Server: the voice went away (a bus layout swap, the server's disposal). No signals.</summary>
    internal void OnVoiceLost() => EndPlay();

    /// <summary>Server, once per frame.</summary>
    internal void Update(float delta)
    {
        if (_voice < 0 || _director is not { } director)
            return;
        var clock = _server.MusicClock(_voice);
        if (clock != _clock)
        {
            _clock = clock;
            _sinceClock = 0;
        }
        else if (!_paused)
        {
            _sinceClock += delta;
        }

        ApplyEvents();
        if (_voice < 0)
            return;

        var active = Active;
        if (active == _paused)
        {
            _paused = !active;
            _server.EnqueueMusic(new AudioCommand
            {
                Type = active ? AudioCommandType.MusicResume : AudioCommandType.MusicPause,
                Index = _voice,
                Generation = _generation,
            });
        }

        RaiseCrossings(clock);
        if (_voice < 0)
            return;

        var volume = AudioMath.DbToLinear(_volumeDb);
        if (volume != _sentVolume)
        {
            _sentVolume = volume;
            var command = new AudioCommand { Type = AudioCommandType.MusicSetVolume, Index = _voice, Generation = _generation };
            command.Params.Gain = volume;
            _server.EnqueueMusic(command);
        }

        if (director.IsPlaying)
            Pump(clock);
    }

    private void ApplyEvents()
    {
        for (var i = 0; i < _inboxCount && _voice >= 0; i++)
        {
            var e = _inbox[i];
            _inbox[i] = default;
            switch (e.Type)
            {
                case AudioEventType.MusicSectionStarted:
                    RaiseCrossings(e.Frame - 1);
                    (_section, _origin, _signalFrame) = (e.Section, e.Origin, e.Frame - 1);
                    SectionStarted?.Invoke(_director!.Plan.Sections[e.Section].Id);
                    break;
                case AudioEventType.MusicMissed:
                    _director!.OnMissed(e.Action);
                    if (e.Action > 0)
                        _server.CountLateMusicTransition();
                    break;
                case AudioEventType.MusicGroupFreed:
                    if (_prefetchAction != 0 && e.Action == _prefetchAction)
                        _prefetchAction = 0; // dropped as stale; prefetched again if still wanted
                    break;
                case AudioEventType.MusicFinished:
                    RaiseCrossings(e.Frame);
                    _server.ReleaseMusicVoice(this, _voice, _generation, halt: false);
                    EndPlay();
                    Finished?.Invoke();
                    break;
                case AudioEventType.MusicStopped:
                    _server.ReleaseMusicVoice(this, _voice, _generation, halt: false);
                    EndPlay();
                    break;
            }
        }

        _inboxCount = 0;
    }

    private void EndPlay()
    {
        for (var i = 0; i < _inboxCount; i++)
            _inbox[i] = default;
        _voice = -1;
        _inboxCount = 0;
        _heldCount = 0;
        _section = -1;
        _prefetchAction = 0;
        _runtime = null;
    }

    // The director at music frame `now`: its actions become commands, held starts are retried, layers and the
    // continuation prefetch follow.
    private void Pump(long now)
    {
        var director = _director!;
        var runtime = _runtime!;
        var rate = runtime.SampleRate;
        var earliest = now + (long)(Math.Max(0f, ScheduleAhead) * rate) + (runtime.HasStreams ? (long)(PrefillSeconds * rate) : 0);
        RetryHeld(now);
        var count = director.Update(now, earliest, _actions);
        for (var i = 0; i < count && _voice >= 0; i++)
            Handle(_actions[i]);
        if (_voice < 0)
            return;
        if (director.LayerVersion != _sentLayerVersion)
            SendLayers(snap: false);
        Prefetch(now);

        var pending = director.PendingSection;
        var frame = director.PendingFrame;
        if (pending != _lastPendingSection || (pending >= 0 && frame != _lastPendingFrame))
        {
            (_lastPendingSection, _lastPendingFrame) = (pending, frame);
            if (pending >= 0)
                TransitionScheduled?.Invoke(director.Plan.Sections[pending].Id, (float)((frame - now) / (double)rate));
        }
    }

    private void Handle(in MusicAction action)
    {
        switch (action.Kind)
        {
            case MusicActionKind.StartSection:
                if (_heldCount > 0 || !TrySendStart(action))
                    Hold(action);
                break;
            case MusicActionKind.Stinger:
                Send(AudioCommandType.MusicStinger, action.Frame, new MusicCommand { Action = action.Id, Section = action.Section });
                break;
            case MusicActionKind.Stop:
                _heldCount = 0;
                Send(AudioCommandType.MusicStop, action.Frame, new MusicCommand { Action = action.Id, FadeOutFrames = action.FadeOutFrames, Flag = action.Tails });
                break;
            case MusicActionKind.Cancel:
                if (!Unhold(action.CancelId))
                    Send(AudioCommandType.MusicCancel, 0, new MusicCommand { Action = action.CancelId });
                break;
        }
    }

    private bool TrySendStart(in MusicAction action)
    {
        var group = _server.AllocateMusicGroup(_voice);
        if (group < 0)
            return false;
        var runtime = _runtime!;
        var length = runtime.Plan.Sections[action.Section].LengthFrames;
        var generation = _server.RequestMusicGroup(_voice, group, runtime.Sections[action.Section], runtime.LayerCount, action.EntryOffset,
            action.Then == action.Section, length);
        Send(AudioCommandType.MusicSchedule, action.Frame, new MusicCommand
        {
            Action = action.Id,
            Section = action.Section,
            Then = action.Then,
            Group = group,
            EntryOffset = action.EntryOffset,
            FadeOutFrames = action.FadeOutFrames,
            FadeInFrames = action.FadeInFrames,
            Flag = action.Tails,
        }, generation);
        return true;
    }

    // No lane group is free yet (the outgoing section is still fading): the start waits, and misses if it cannot be
    // sent in time.
    private void Hold(in MusicAction action)
    {
        if (_heldCount == HeldCapacity)
        {
            Miss(action.Id);
            return;
        }

        _held[_heldCount++] = action;
    }

    private bool Unhold(int id)
    {
        for (var i = 0; i < _heldCount; i++)
        {
            if (_held[i].Id != id)
                continue;
            Array.Copy(_held, i + 1, _held, i, _heldCount - i - 1);
            _heldCount--;
            return true;
        }

        return false;
    }

    private void RetryHeld(long now)
    {
        var margin = (long)(MissMarginSeconds * _runtime!.SampleRate);
        while (_heldCount > 0)
        {
            var action = _held[0];
            if (action.Frame <= now + margin)
            {
                Unhold(action.Id);
                Miss(action.Id);
                continue;
            }

            if (!TrySendStart(action))
                return;
            Unhold(action.Id);
        }
    }

    private void Miss(int id)
    {
        _director!.OnMissed(id);
        _server.CountLateMusicTransition();
    }

    // The current pass's authored continuation is streamed into a free group ahead of the section's end, unless a
    // transition is scheduled before it.
    private void Prefetch(long now)
    {
        var director = _director!;
        var plan = director.Plan;
        var section = director.CurrentSection;
        var then = director.CurrentThen;
        var want = director.IsPlaying && section >= 0 && then >= 0 && then != section;
        var frame = want ? director.PassStart + plan.Sections[section].LengthFrames : 0;
        if (want && director.PendingFrame >= 0 && director.PendingFrame <= frame)
            want = false;

        if (_prefetchAction != 0 && (!want || _prefetchSection != then || _prefetchFrame != frame))
        {
            // A prefetch at or before `now` is committed: the audio thread handles frame `now` at its next block and
            // frees the group itself if the continuation does not use it.
            if (_prefetchFrame > now)
                Send(AudioCommandType.MusicCancel, 0, new MusicCommand { Action = _prefetchAction });
            _prefetchAction = 0;
        }

        if (!want || _prefetchAction != 0 || frame <= now)
            return;
        var group = _server.AllocateMusicGroup(_voice);
        if (group < 0)
            return;
        var runtime = _runtime!;
        var generation = _server.RequestMusicGroup(_voice, group, runtime.Sections[then], runtime.LayerCount, 0,
            plan.Sections[then].Then == then, plan.Sections[then].LengthFrames);
        _prefetchAction = _nextPrefetchId--;
        (_prefetchSection, _prefetchFrame) = (then, frame);
        Send(AudioCommandType.MusicPrefetch, frame, new MusicCommand { Action = _prefetchAction, Section = then, Then = plan.Sections[then].Then, Group = group },
            generation);
    }

    private void SendLayers(bool snap)
    {
        var director = _director!;
        _sentLayerVersion = director.LayerVersion;
        for (var l = 0; l < _sentGain.Length; l++)
        {
            var gain = AudioMath.DbToLinear(director.LayerGainDb(l));
            var cutoff = director.LayerCutoffHz(l);
            if (!snap && gain == _sentGain[l] && cutoff == _sentCutoff[l])
                continue;
            (_sentGain[l], _sentCutoff[l]) = (gain, cutoff);
            Send(AudioCommandType.MusicSetLayer, 0, new MusicCommand { Layer = l, Gain = gain, CutoffHz = cutoff, Flag = snap });
        }
    }

    private void Send(AudioCommandType type, long frame, in MusicCommand music, int streamGeneration = 0) =>
        _server.EnqueueMusic(new AudioCommand
        {
            Type = type,
            Index = _voice,
            Generation = _generation,
            Frame = frame,
            StreamGeneration = streamGeneration,
            Music = music,
        });

    // Bar, Beat and Marker for every grid line and marker in (_signalFrame, to], following loops of the section.
    private void RaiseCrossings(long to)
    {
        if (_section < 0 || to <= _signalFrame)
        {
            _signalFrame = Math.Max(_signalFrame, to);
            return;
        }

        var section = _director!.Plan.Sections[_section];
        var current = _section;
        var generation = _generation;
        while (_signalFrame < to)
        {
            var passEnd = _origin + section.LengthFrames;
            var upto = Math.Min(to, passEnd - 1);
            if (upto > _signalFrame)
            {
                RaiseRange(section, _signalFrame + 1 - _origin, upto - _origin);
                if (_section != current || _generation != generation || _voice < 0)
                    return; // a handler changed the music
            }

            if (to >= passEnd && section.Loops && section.LengthFrames > 0)
            {
                _signalFrame = passEnd - 1;
                _origin = passEnd;
                continue;
            }

            _signalFrame = to;
        }
    }

    private void RaiseRange(MusicPlanSection section, long from, long to)
    {
        var grid = _director!.Plan.Grid;
        var perBar = grid.BeatsPerBar;
        var beat = grid.BeatAtOrAfter(from);
        var markers = section.MarkerFrames;
        var m = 0;
        while (m < markers.Length && markers[m] < from)
            m++;
        var current = _section;
        while (_section == current && _voice >= 0)
        {
            var beatFrame = grid.FrameOfBeat(beat);
            var markerFrame = m < markers.Length ? markers[m] : long.MaxValue;
            if (beatFrame > to && markerFrame > to)
                return;
            if (beatFrame <= markerFrame)
            {
                var bar = (int)(beat / perBar) + 1;
                if (beat % perBar == 0)
                    Bar?.Invoke(bar);
                Beat?.Invoke(bar, (int)(beat % perBar) + 1);
                beat++;
            }
            else
            {
                Marker?.Invoke(section.MarkerNames[m]);
                m++;
            }
        }
    }

    private void SetByName(string name, float value, bool immediate)
    {
        if (_director is not { } d)
            return;
        if (!d.Parameters.SetTarget(name, value, immediate))
            Warn(name, $"[Audio] Adaptive music has no parameter '{name}'; SetParameter ignored.");
    }

    private void SetByIndex(int index, float value, bool immediate)
    {
        if (_director is { } d && (uint)index < (uint)d.Parameters.Count)
            d.Parameters.SetTarget(index, value, immediate);
    }

    private void SetLabel(string name, string label, bool immediate)
    {
        if (_director is not { } d)
            return;
        if (!d.Parameters.SetLabel(name, label, immediate))
            Warn(name + "=" + label, $"[Audio] Adaptive music: '{label}' is not a label of parameter '{name}'; SetParameter ignored.");
    }

    private void Warn(string key, string message)
    {
        if ((_warned ??= new HashSet<string>(StringComparer.Ordinal)).Add(key))
            Log.Warning(message);
    }
}

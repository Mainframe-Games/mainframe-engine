using MainframeEngine.Audio.Dsp;
using MainframeEngine.Audio.Music;
using SoundFlow.Abstracts;
using SoundFlow.Structs;

namespace MainframeEngine.Audio;

/// <summary>
/// One adaptive-music player on the audio thread: a SoundFlow component created up front in its bus's mixer (like the
/// pooled voices, <see cref="AudioBusInfo.MusicVoices"/>) that carries out what the game thread's
/// <see cref="MusicDirector"/> schedules, sample-accurately on the <b>music clock</b> (frames at the music's rate since
/// <c>Play</c>, stopped while paused).
/// </summary>
/// <remarks>
/// <para><b>Lanes.</b> <see cref="Groups"/> lane groups of <see cref="Lanes"/> lanes: one group plays the current section
/// (one lane per layer), another holds the incoming one (a scheduled transition, or the prefetched continuation), and a
/// third lets the outgoing section fade while the next transition is prepared. Every lane of a group shares one
/// fractional read position and step, so layers resampled to the device rate stay sample-aligned. A lane reads a memory
/// clip or its own <see cref="AudioStreamChannel"/>; a streamed lane that runs dry outputs silence and skips the frames
/// it missed once data arrives, so it never falls behind the others. Plus <see cref="OneShots"/> one-shot lanes for
/// tails and stingers.</para>
/// <para><b>Actions</b> (section starts, stingers, stop) wait in a queue sorted by frame; each block is split at every
/// action frame and section end so each happens on its exact sample. At a section end with nothing scheduled the voice
/// follows the authored continuation by itself (loop, then, end), so the music keeps going if the game thread stalls.
/// Whenever a section ends at its end, its tails play over whatever comes next.</para>
/// <para><b>Per lane:</b> layer gain (ramped per sample over ~10 ms) × the group's fade envelope, then the layer's
/// filter (the shared <see cref="Svf"/>, 12 dB/oct). The voice's volume ramps the same way; its bus applies the fader.</para>
/// <para>Audio thread only after construction, except the stream channels and the published clock. No locks, no
/// allocation.</para>
/// </remarks>
internal sealed class MusicVoice : SoundComponent
{
    /// <summary>Lane groups per voice: current, incoming, outgoing fade, and one spare for a prefetch.</summary>
    public const int Groups = 4;

    /// <summary>Lanes per group: one per layer.</summary>
    public const int Lanes = MusicPlan.MaxLayers;

    /// <summary>Tails and stingers playing at once (the oldest is replaced when they are all busy).</summary>
    public const int OneShots = 8;

    /// <summary>Scheduled actions the voice can hold.</summary>
    public const int QueueCapacity = 32;

    private const int Chunk = 256;
    private const int ControlFrames = 32;
    private const int ReadyFrames = 256;
    private const int EventCapacity = 64;
    private const float HaltSeconds = 0.005f;
    private const float StartTimeoutSeconds = 0.5f;

    private readonly int _deviceRate;
    private readonly float _ramp;
    private readonly LaneGroup[] _groups = new LaneGroup[Groups];
    private readonly OneShot[] _oneShots = new OneShot[OneShots];
    private readonly Scheduled[] _queue = new Scheduled[QueueCapacity];
    private readonly AudioEvent[] _events = new AudioEvent[EventCapacity];
    private readonly float[] _mix = new float[Chunk * 2];
    private readonly float[] _lane = new float[Chunk * 2];
    private readonly float[] _envelope = new float[Chunk];
    private readonly float[] _layerGains = new float[Lanes * Chunk];
    private readonly float[] _gain = new float[Lanes];
    private readonly float[] _gainTarget = new float[Lanes];
    private readonly float[] _cutoff = new float[Lanes]; // ln(Hz)
    private readonly float[] _cutoffTarget = new float[Lanes];
    private readonly Svf[] _filterTemplate = new Svf[Lanes];

    private MusicRuntime? _runtime;
    private VoiceState _state;
    private bool _paused;
    private bool _forceStart;
    private int _waited;
    private double _clock;
    private double _ratio = 1;
    private int _queued;
    private int _eventHead;
    private int _eventCount;
    private float _volume = 1f;
    private float _volumeTarget = 1f;
    private float _haltGain = 1f;
    private float _haltStep;

    // The section playing (−1: none) and its current pass.
    private int _section = -1;
    private int _currentGroup = -1;
    private long _origin;
    private long _endFrame;
    private int _then = -1;
    private bool _stopping;

    public MusicVoice(AudioEngine engine, AudioFormat format, int index, int busIndex)
        : base(engine, format)
    {
        Index = index;
        BusIndex = busIndex;
        _deviceRate = format.SampleRate;
        _ramp = 1f - MathF.Exp(-1f / (SpatialSmoother.RampSeconds * format.SampleRate));
        for (var g = 0; g < Groups; g++)
            _groups[g] = new LaneGroup();
        Name = "Music " + index;
        Volume = AudioGraph.UnityVolume;
        Enabled = false;
    }

    public int Index { get; }

    public int BusIndex { get; }

    /// <summary>
    /// The stream pipes, by <c>group × <see cref="Lanes"/> + lane</c>; created by the game thread on first use, before
    /// the command that needs them is published (so the audio thread always sees them).
    /// </summary>
    public AudioStreamChannel?[] StreamChannels { get; } = new AudioStreamChannel?[Groups * Lanes];

    /// <summary>The play this voice is running (matches the game thread's slot).</summary>
    public int Generation { get; private set; }

    /// <summary>Streamed lanes that found their ring empty (total).</summary>
    public int Underruns { get; private set; }

    /// <summary>Audio thread: <see cref="Underruns"/> already added to the root's total.</summary>
    public int ReportedUnderruns { get; set; }

    /// <summary>The music clock after the last block (frames at the music's rate), for the game thread.</summary>
    public long PublishedClock;

    /// <summary>The <see cref="Generation"/> <see cref="PublishedClock"/> belongs to.</summary>
    public int PublishedGeneration;

    // ---------------------------------------------------------------------------------------------
    // Commands (audio thread, before the mix)
    // ---------------------------------------------------------------------------------------------

    public void Apply(in AudioCommand command)
    {
        if (command.Type == AudioCommandType.MusicPlay)
        {
            Play(command);
            return;
        }

        if (command.Generation != Generation || _state == VoiceState.Idle)
            return; // for a play this voice no longer runs

        ref readonly var m = ref command.Music;
        switch (command.Type)
        {
            case AudioCommandType.MusicSchedule:
                Schedule(ActionKind.Start, command, m);
                break;
            case AudioCommandType.MusicStinger:
                Schedule(ActionKind.Stinger, command, m);
                break;
            case AudioCommandType.MusicStop:
                Schedule(ActionKind.Stop, command, m);
                break;
            case AudioCommandType.MusicPrefetch:
                Prefetch(command, m);
                break;
            case AudioCommandType.MusicCancel:
                Cancel(m.Action);
                break;
            case AudioCommandType.MusicSetLayer:
                SetLayer(m.Layer, m.Gain, m.CutoffHz, m.Flag);
                break;
            case AudioCommandType.MusicSetVolume:
                _volumeTarget = Sanitize(command.Params.Gain);
                break;
            case AudioCommandType.MusicPause:
                _paused = true;
                break;
            case AudioCommandType.MusicResume:
                _paused = false;
                break;
            case AudioCommandType.MusicHalt:
                Halt();
                break;
        }
    }

    private void Play(in AudioCommand command)
    {
        // Whatever the previous play still held goes (its stream channels already belong to the new play).
        for (var g = 0; g < Groups; g++)
            _groups[g].Reset();
        for (var o = 0; o < OneShots; o++)
            _oneShots[o].Active = false;
        _queued = 0;
        _eventCount = 0;
        _eventHead = 0;

        Generation = command.Generation;
        _runtime = (MusicRuntime?)command.Ref;
        _ratio = _runtime is null ? 1 : (double)_runtime.SampleRate / _deviceRate;
        _clock = 0;
        _waited = 0;
        _forceStart = false;
        _paused = command.StartPaused;
        _volume = _volumeTarget = Sanitize(command.Params.Gain);
        _haltGain = 1f;
        _haltStep = 0;
        (_section, _currentGroup, _origin, _endFrame, _then, _stopping) = (-1, -1, 0, 0, -1, false);
        for (var l = 0; l < Lanes; l++)
        {
            _gain[l] = _gainTarget[l] = 1f;
            _cutoff[l] = _cutoffTarget[l] = MathF.Log(MusicCurve.MaxCutoffHz);
        }

        _state = _runtime is null ? VoiceState.Idle : VoiceState.Starting;
        Publish();
        Enabled = _state != VoiceState.Idle;
    }

    private void Schedule(ActionKind kind, in AudioCommand command, in MusicCommand m)
    {
        var frame = (long)command.Frame;
        var late = frame < (long)Math.Ceiling(_clock - 1e-9) && !(_state == VoiceState.Starting && frame == 0);
        if (kind == ActionKind.Stop)
        {
            frame = Math.Max(frame, (long)Math.Ceiling(_clock - 1e-9)); // a stop is never missed: late means now
            late = false;
        }

        if (late || _queued == QueueCapacity || (kind == ActionKind.Start && (uint)m.Group >= Groups))
        {
            Post(AudioEventType.MusicMissed, frame, 0, m.Section, m.Action, -1);
            if (kind == ActionKind.Start && (uint)m.Group < Groups)
                FreeGroup(m.Group, m.Action);
            return;
        }

        var item = new Scheduled
        {
            Kind = kind,
            Action = m.Action,
            Frame = frame,
            Section = m.Section,
            Then = m.Then,
            Group = m.Group,
            EntryOffset = Math.Max(0, m.EntryOffset),
            FadeOutFrames = Math.Max(0, m.FadeOutFrames),
            FadeInFrames = Math.Max(0, m.FadeInFrames),
            Tails = m.Flag,
            StreamGeneration = command.StreamGeneration,
        };
        if (kind == ActionKind.Start)
        {
            var group = _groups[m.Group];
            group.Reset();
            group.State = GroupState.Waiting;
            group.Action = m.Action;
            group.Section = m.Section;
            group.StreamGeneration = command.StreamGeneration;
            group.StartFrame = frame;
        }

        // Insert sorted by frame (stable: equal frames keep their order).
        var i = _queued;
        while (i > 0 && _queue[i - 1].Frame > frame)
        {
            _queue[i] = _queue[i - 1];
            i--;
        }

        _queue[i] = item;
        _queued++;
    }

    private void Prefetch(in AudioCommand command, in MusicCommand m)
    {
        if ((uint)m.Group >= Groups)
            return;
        var group = _groups[m.Group];
        group.Reset();
        group.State = GroupState.Prefetched;
        group.Action = m.Action;
        group.Section = m.Section;
        group.StreamGeneration = command.StreamGeneration;
        group.StartFrame = (long)command.Frame;
        if (group.StartFrame < (long)Math.Ceiling(_clock - 1e-9) || (_section >= 0 && (group.StartFrame != _endFrame || m.Section != _then)))
            FreeGroup(m.Group, m.Action); // already stale (before the first section starts, SetSection checks it)
    }

    private void Cancel(int action)
    {
        for (var i = 0; i < _queued; i++)
        {
            if (_queue[i].Action != action)
                continue;
            if (_queue[i].Kind == ActionKind.Start)
                FreeGroup(_queue[i].Group, action);
            Array.Copy(_queue, i + 1, _queue, i, _queued - i - 1);
            _queued--;
            return;
        }

        for (var g = 0; g < Groups; g++)
        {
            if (_groups[g].State == GroupState.Prefetched && _groups[g].Action == action)
                FreeGroup(g, action);
        }
    }

    private void SetLayer(int layer, float gain, float cutoffHz, bool snap)
    {
        if ((uint)layer >= Lanes)
            return;
        _gainTarget[layer] = Sanitize(gain);
        _cutoffTarget[layer] = MathF.Log(float.IsFinite(cutoffHz) ? Math.Clamp(cutoffHz, MusicCurve.MinCutoffHz, MusicCurve.MaxCutoffHz) : MusicCurve.MaxCutoffHz);
        if (!snap)
            return;
        _gain[layer] = _gainTarget[layer];
        _cutoff[layer] = _cutoffTarget[layer];
        UpdateFilter(layer);
    }

    private void Halt()
    {
        if (_state is VoiceState.Idle or VoiceState.Halting)
            return;
        _state = VoiceState.Halting;
        _haltStep = 1f / Math.Max(1f, HaltSeconds * _deviceRate);
        _queued = 0;
        _section = -1; // no more continuations; the lanes keep playing under the fade
        if (_paused)
            Finish(AudioEventType.MusicStopped); // nothing is sounding: no fade needed
    }

    // ---------------------------------------------------------------------------------------------
    // Mixing (audio thread)
    // ---------------------------------------------------------------------------------------------

    protected override void GenerateAudio(Span<float> buffer, int channels)
    {
        if (_state == VoiceState.Idle || _paused || _runtime is null)
            return;
        var frames = buffer.Length / Math.Max(channels, 1);

        if (_state == VoiceState.Starting)
        {
            // Play waits (clock held at 0) until the first section's streamed lanes have data, up to a timeout.
            if (_queued > 0 && _queue[0].Kind == ActionKind.Start && !IsReady(_queue[0]) && _waited < StartTimeoutSeconds * _deviceRate)
            {
                _waited += frames;
                return;
            }

            _state = VoiceState.Playing;
            _forceStart = true;
        }

        var done = 0;
        while (done < frames && _state != VoiceState.Idle)
        {
            ProcessDue();
            if (_state == VoiceState.Idle)
                break;

            var n = Math.Min(frames - done, Chunk);
            var next = NextEventFrame();
            if (next != long.MaxValue)
                n = (int)Math.Clamp(Math.Ceiling((next - _clock) / _ratio - 1e-9), 1, n);
            if (CutoffMoving())
                n = Math.Min(n, ControlFrames);

            RenderChunk(n);
            WriteOut(buffer, done, n, channels);
            _clock += n * _ratio;
            done += n;
            AfterChunk();
        }
    }

    /// <summary>After the mix: the clock and the events for the game thread.</summary>
    public void Publish()
    {
        Volatile.Write(ref PublishedGeneration, Generation);
        Volatile.Write(ref PublishedClock, (long)Math.Floor(_clock + 1e-9));
    }

    /// <summary>The oldest event not yet handed to the game thread.</summary>
    public bool TryPeekEvent(out AudioEvent e)
    {
        if (_eventCount == 0)
        {
            e = default;
            return false;
        }

        e = _events[_eventHead];
        return true;
    }

    public void PopEvent()
    {
        _events[_eventHead] = default;
        _eventHead = (_eventHead + 1) % EventCapacity;
        _eventCount--;
    }

    private bool Due(long frame) => frame <= _clock + 1e-9;

    // Everything due at the current clock, in order: scheduled actions at or before the section end, then the end.
    private void ProcessDue()
    {
        while (_state != VoiceState.Idle)
        {
            var hasAction = _queued > 0 && Due(_queue[0].Frame);
            var endDue = _section >= 0 && Due(_endFrame);
            if (hasAction && (!endDue || _queue[0].Frame <= _endFrame))
            {
                var action = _queue[0];
                Array.Copy(_queue, 1, _queue, 0, _queued - 1);
                _queued--;
                Execute(action);
            }
            else if (endDue)
            {
                Continue();
            }
            else
            {
                return;
            }
        }
    }

    private long NextEventFrame()
    {
        var next = long.MaxValue;
        if (_queued > 0)
            next = _queue[0].Frame;
        if (_section >= 0)
            next = Math.Min(next, _endFrame);
        return next;
    }

    private void Execute(in Scheduled action)
    {
        switch (action.Kind)
        {
            case ActionKind.Start:
                Start(action);
                break;
            case ActionKind.Stinger:
                if ((uint)action.Section < (uint)_runtime!.Sections.Length && _runtime.Sections[action.Section].Mix is { } mix)
                    StartOneShot(mix, -1, action.Frame);
                break;
            case ActionKind.Stop:
                if (_state is VoiceState.Halting)
                    break;
                if (action.Tails)
                    FireTails(_section, action.Frame);
                EndCurrent(action.FadeOutFrames);
                _section = -1;
                _stopping = true;
                FreeAllWaiting();
                _queued = 0;
                _state = VoiceState.Ending;
                break;
        }
    }

    private void Start(in Scheduled action)
    {
        var runtime = _runtime!;
        var group = _groups[action.Group];
        var force = _forceStart;
        _forceStart = false;
        if (_state is VoiceState.Halting or VoiceState.Ending || (uint)action.Section >= (uint)runtime.Sections.Length ||
            group.Action != action.Action || (!force && !IsReady(action)))
        {
            Post(AudioEventType.MusicMissed, action.Frame, 0, action.Section, action.Action, -1);
            if (group.Action == action.Action)
                FreeGroup(action.Group, action.Action);
            return;
        }

        if (action.Tails && _section >= 0)
            FireTails(_section, action.Frame);
        EndCurrent(action.FadeOutFrames);

        var length = runtime.Plan.Sections[action.Section].LengthFrames;
        var entry = length > 0 ? action.EntryOffset % length : 0;
        BeginGroup(action.Group, action.Section, action.Then == action.Section, entry, action.Frame, action.FadeInFrames);
        SetSection(action.Section, action.Frame - entry, action.Then, action.Group);
        Post(AudioEventType.MusicSectionStarted, action.Frame, _origin, action.Section, action.Action, action.Group);
    }

    // The current section reaches its end with nothing scheduled there: loop, continue, or end.
    private void Continue()
    {
        var runtime = _runtime!;
        var end = _endFrame;
        var section = _section;
        FireTails(section, end);
        if (_then == section)
        {
            _origin = end;
            _endFrame = end + runtime.Plan.Sections[section].LengthFrames;
            return;
        }

        if (_currentGroup >= 0)
            FreeGroup(_currentGroup, _groups[_currentGroup].Action);
        _currentGroup = -1;

        if (_then < 0)
        {
            _section = -1;
            FreeAllWaiting();
            _state = VoiceState.Ending;
            return;
        }

        var next = _then;
        var nextThen = runtime.Plan.Sections[next].Then;
        var prefetched = -1;
        for (var g = 0; g < Groups; g++)
        {
            if (_groups[g].State == GroupState.Prefetched && _groups[g].Section == next && _groups[g].StartFrame == end)
                prefetched = g;
        }

        var action = 0;
        if (prefetched >= 0)
        {
            action = _groups[prefetched].Action;
            BeginGroup(prefetched, next, nextThen == next, 0, end, 0); // not checked for readiness: authored flow never waits
        }

        SetSection(next, end, nextThen, prefetched);
        Post(AudioEventType.MusicSectionStarted, end, end, next, action, prefetched);
    }

    private void SetSection(int section, long origin, int then, int group)
    {
        _section = section;
        _origin = origin;
        _endFrame = origin + _runtime!.Plan.Sections[section].LengthFrames;
        _then = then;
        _currentGroup = group;

        // Prefetches for anything but this pass's continuation are stale now.
        for (var g = 0; g < Groups; g++)
        {
            var other = _groups[g];
            if (other.State == GroupState.Prefetched && (other.Section != _then || other.StartFrame != _endFrame || _then == _section))
                FreeGroup(g, other.Action);
        }
    }

    // The current group stops at the clock (or fades out over fadeFrames music frames while the next section starts).
    private void EndCurrent(long fadeFrames)
    {
        var index = _currentGroup;
        if (index < 0)
            return;
        var group = _groups[index];
        _currentGroup = -1;
        if (fadeFrames <= 0)
        {
            FreeGroup(index, group.Action);
            return;
        }

        group.State = GroupState.Fading;
        group.EnvelopeStep = -(float)(group.Envelope * _ratio / fadeFrames);
        group.FadeIn = false;
    }

    private void BeginGroup(int index, int section, bool loop, long entry, long frame, long fadeInFrames)
    {
        var runtime = _runtime!;
        var group = _groups[index];
        var stems = runtime.Sections[section];
        group.State = GroupState.Playing;
        group.Section = section;
        group.Loop = loop;
        group.Length = runtime.Plan.Sections[section].LengthFrames;
        group.Fraction = Math.Max(0, _clock - frame);
        group.Envelope = fadeInFrames > 0 ? 0f : 1f;
        group.EnvelopeStep = fadeInFrames > 0 ? (float)(_ratio / fadeInFrames) : 0f;
        group.FadeIn = fadeInFrames > 0;
        for (var l = 0; l < Lanes; l++)
        {
            ref var lane = ref group.Lanes[l];
            lane = default;
            if (l >= runtime.LayerCount)
                continue;
            switch (stems.Bodies[l])
            {
                case AudioClipData clip:
                    lane.Clip = clip;
                    break;
                case AudioStreamSource:
                    lane.Channel = StreamChannels[index * Lanes + l];
                    lane.Channels = lane.Channel?.TrySync(group.StreamGeneration) ?? 0;
                    lane.Synced = lane.Channels != 0;
                    break;
                default:
                    continue;
            }

            lane.Active = lane.Clip is not null || lane.Channel is not null;
            lane.Next = entry;
            lane.FilterL.CopyCoefficients(_filterTemplate[l]);
            lane.FilterR.CopyCoefficients(_filterTemplate[l]);
            if (!lane.Active)
                continue;
            if (lane.Channel is not null)
                lane.Limit = lane.Channel.ReadLimit(group.StreamGeneration);
            Fetch(group, ref lane, out lane.S0L, out lane.S0R);
            Fetch(group, ref lane, out lane.S1L, out lane.S1R);
        }

        // Sub-frame alignment with the music clock (resampled output): skip whole source frames already passed.
        while (group.Fraction >= 1)
        {
            group.Fraction -= 1;
            for (var l = 0; l < runtime.LayerCount; l++)
            {
                ref var lane = ref group.Lanes[l];
                if (lane.Active)
                    Shift(group, ref lane);
            }
        }
    }

    // Every streamed lane of a scheduled start has data (at least a block, or the whole short body).
    private bool IsReady(in Scheduled action)
    {
        var runtime = _runtime!;
        if ((uint)action.Section >= (uint)runtime.Sections.Length || (uint)action.Group >= Groups)
            return false;
        var stems = runtime.Sections[action.Section];
        if (!stems.Streamed)
            return true;
        var length = runtime.Plan.Sections[action.Section].LengthFrames;
        var needed = (int)Math.Clamp(length - action.EntryOffset, 1, ReadyFrames);
        for (var l = 0; l < runtime.LayerCount; l++)
        {
            if (stems.Bodies[l] is not AudioStreamSource)
                continue;
            if (StreamChannels[action.Group * Lanes + l] is not { } channel)
                return false;
            var channels = channel.TrySync(action.StreamGeneration);
            if (channels == 0)
                return false;
            if (channel.EndGeneration != action.StreamGeneration &&
                channel.Readable(channel.ReadLimit(action.StreamGeneration)) < needed * channels)
                return false;
        }

        return true;
    }

    private void FireTails(int section, long frame)
    {
        var runtime = _runtime!;
        if ((uint)section >= (uint)runtime.Sections.Length)
            return;
        var tails = runtime.Sections[section].Tails;
        for (var l = 0; l < tails.Length; l++)
        {
            if (tails[l] is { } tail)
                StartOneShot(tail, l, frame);
        }
    }

    private void StartOneShot(AudioClipData clip, int layer, long frame)
    {
        var slot = -1;
        var furthest = -1.0;
        for (var i = 0; i < OneShots; i++)
        {
            if (!_oneShots[i].Active)
            {
                slot = i;
                break;
            }

            var progress = _oneShots[i].Position / Math.Max(1, _oneShots[i].Clip!.Frames);
            if (progress > furthest)
                (furthest, slot) = (progress, i);
        }

        ref var shot = ref _oneShots[slot];
        shot = default;
        shot.Active = true;
        shot.Clip = clip;
        shot.Layer = layer;
        shot.Position = Math.Max(0, _clock - frame);
        if (layer >= 0 && _runtime!.Filters[layer] != MusicLayerFilter.None)
        {
            shot.FilterL.CopyCoefficients(_filterTemplate[layer]);
            shot.FilterR.CopyCoefficients(_filterTemplate[layer]);
        }
    }

    private void RenderChunk(int n)
    {
        var runtime = _runtime!;
        var mix = _mix.AsSpan(0, n * 2);
        mix.Clear();
        PrepareLayerGains(n, runtime.LayerCount);

        foreach (var group in _groups)
        {
            if (group.State is not (GroupState.Playing or GroupState.Fading))
                continue;
            var envelope = PrepareEnvelope(group, n);
            var endFraction = -1.0;
            for (var l = 0; l < runtime.LayerCount; l++)
            {
                ref var lane = ref group.Lanes[l];
                if (!lane.Active)
                    continue;
                if (lane.Channel is not null)
                    lane.Limit = lane.Channel.ReadLimit(group.StreamGeneration);
                endFraction = RenderLane(group, ref lane, runtime.Filters[l], _layerGains.AsSpan(l * Chunk, n), envelope, mix, n);
            }

            group.Fraction = endFraction >= 0 ? endFraction : AdvanceFraction(group, n); // every lane ends on the same fraction
        }

        for (var i = 0; i < OneShots; i++)
        {
            ref var shot = ref _oneShots[i];
            if (shot.Active)
                RenderOneShot(ref shot, runtime, n, mix);
        }
    }

    // Three tight passes per lane: read (resampled) into a scratch buffer, filter it in place, then weight and sum it.
    private double RenderLane(LaneGroup group, ref Lane lane, MusicLayerFilter filter, ReadOnlySpan<float> gains, ReadOnlySpan<float> envelope,
        Span<float> mix, int n)
    {
        var scratch = _lane.AsSpan(0, n * 2);
        var f = lane.Clip is { } clip && _ratio == 1.0 && group.Fraction == 0.0
            ? ReadClipUnit(group, ref lane, clip, scratch, n)
            : ReadResampled(group, ref lane, scratch, n);

        if (filter == MusicLayerFilter.LowPass)
        {
            for (var i = 0; i < n; i++)
            {
                scratch[2 * i] = lane.FilterL.Process(scratch[2 * i], out _, out _);
                scratch[2 * i + 1] = lane.FilterR.Process(scratch[2 * i + 1], out _, out _);
            }
        }
        else if (filter == MusicLayerFilter.HighPass)
        {
            for (var i = 0; i < n; i++)
            {
                lane.FilterL.Process(scratch[2 * i], out _, out scratch[2 * i]);
                lane.FilterR.Process(scratch[2 * i + 1], out _, out scratch[2 * i + 1]);
            }
        }

        if (filter != MusicLayerFilter.None)
        {
            lane.FilterL.Flush();
            lane.FilterR.Flush();
        }

        for (var i = 0; i < n; i++)
        {
            var w = gains[i] * envelope[i];
            mix[2 * i] += scratch[2 * i] * w;
            mix[2 * i + 1] += scratch[2 * i + 1] * w;
        }

        return f;
    }

    // Linear interpolation at any step (the music's rate differs from the device's, or a start between device frames).
    private double ReadResampled(LaneGroup group, ref Lane lane, Span<float> scratch, int n)
    {
        var f = group.Fraction;
        var step = _ratio;
        for (var i = 0; i < n; i++)
        {
            var t = (float)f;
            scratch[2 * i] = lane.S0L + (lane.S1L - lane.S0L) * t;
            scratch[2 * i + 1] = lane.S0R + (lane.S1R - lane.S0R) * t;
            f += step;
            while (f >= 1.0)
            {
                f -= 1.0;
                Shift(group, ref lane);
            }
        }

        return f;
    }

    // The common case: a memory clip at the device's rate, aligned on whole frames: frame i is S0, then the next frame.
    private static double ReadClipUnit(LaneGroup group, ref Lane lane, AudioClipData clip, Span<float> scratch, int n)
    {
        var samples = clip.Samples;
        var stereo = clip.Channels == 2;
        var frames = clip.Frames;
        var length = group.Length;
        var loop = group.Loop && length > 0;
        var (s0L, s0R, s1L, s1R, next) = (lane.S0L, lane.S0R, lane.S1L, lane.S1R, lane.Next);
        for (var i = 0; i < n; i++)
        {
            scratch[2 * i] = s0L;
            scratch[2 * i + 1] = s0R;
            (s0L, s0R) = (s1L, s1R);
            if (next >= length && loop)
                next -= length;
            if (next < length && next < frames)
            {
                if (stereo)
                    (s1L, s1R) = (samples[2 * next], samples[2 * next + 1]);
                else
                    s1L = s1R = samples[next];
            }
            else
            {
                s1L = s1R = 0;
            }

            if (next < length)
                next++;
        }

        (lane.S0L, lane.S0R, lane.S1L, lane.S1R, lane.Next) = (s0L, s0R, s1L, s1R, next);
        return 0.0;
    }

    private double AdvanceFraction(LaneGroup group, int n)
    {
        var f = group.Fraction + n * _ratio;
        return f - Math.Floor(f);
    }

    private void Shift(LaneGroup group, ref Lane lane)
    {
        lane.S0L = lane.S1L;
        lane.S0R = lane.S1R;
        Fetch(group, ref lane, out lane.S1L, out lane.S1R);
    }

    // The lane's next source frame: a clip frame, or the next frame of its stream (silence when it ran dry, counted so
    // the frames it missed are skipped once data arrives); silence past a non-looping body's end.
    private void Fetch(LaneGroup group, ref Lane lane, out float left, out float right)
    {
        left = right = 0;
        if (lane.Next >= group.Length)
        {
            if (!group.Loop || group.Length <= 0)
                return;
            lane.Next -= group.Length;
        }

        var index = lane.Next++;
        if (lane.Clip is { } clip)
        {
            if (index >= clip.Frames)
                return;
            var samples = clip.Samples;
            if (clip.Channels == 2)
            {
                left = samples[2 * index];
                right = samples[2 * index + 1];
            }
            else
            {
                left = right = samples[index];
            }

            return;
        }

        if (lane.Channel is not { } channel)
            return;
        if (!lane.Synced)
        {
            lane.Channels = channel.TrySync(group.StreamGeneration);
            lane.Synced = lane.Channels != 0;
            if (!lane.Synced)
            {
                if (lane.Debt++ == 0)
                    Underruns++;
                return;
            }

            lane.Limit = channel.ReadLimit(group.StreamGeneration);
        }

        var channels = lane.Channels;
        while (lane.Debt > 0 && channel.Readable(lane.Limit) >= channels)
        {
            channel.Advance(channels);
            lane.Debt--;
        }

        if (channel.Readable(lane.Limit) < channels)
        {
            if (channel.EndGeneration != group.StreamGeneration)
            {
                if (lane.Debt++ == 0)
                    Underruns++; // once per dry spell, like a voice's once per block
            }

            return;
        }

        left = channel.Peek(0);
        right = channels == 2 ? channel.Peek(1) : left;
        channel.Advance(channels);
    }

    private void RenderOneShot(ref OneShot shot, MusicRuntime runtime, int n, Span<float> mix)
    {
        var clip = shot.Clip!;
        var samples = clip.Samples;
        var stereo = clip.Channels == 2;
        var frames = clip.Frames;
        var layer = shot.Layer;
        var filter = layer >= 0 ? runtime.Filters[layer] : MusicLayerFilter.None;
        var position = shot.Position;
        for (var i = 0; i < n; i++)
        {
            var i0 = (long)position;
            if (i0 >= frames)
            {
                shot.Active = false;
                return;
            }

            var t = (float)(position - i0);
            var i1 = i0 + 1 < frames ? i0 + 1 : -1;
            float l0, r0, l1 = 0, r1 = 0;
            if (stereo)
            {
                (l0, r0) = (samples[2 * i0], samples[2 * i0 + 1]);
                if (i1 >= 0)
                    (l1, r1) = (samples[2 * i1], samples[2 * i1 + 1]);
            }
            else
            {
                l0 = r0 = samples[i0];
                if (i1 >= 0)
                    l1 = r1 = samples[i1];
            }

            var left = l0 + (l1 - l0) * t;
            var right = r0 + (r1 - r0) * t;
            switch (filter)
            {
                case MusicLayerFilter.LowPass:
                    left = shot.FilterL.Process(left, out _, out _);
                    right = shot.FilterR.Process(right, out _, out _);
                    break;
                case MusicLayerFilter.HighPass:
                    shot.FilterL.Process(left, out _, out left);
                    shot.FilterR.Process(right, out _, out right);
                    break;
            }

            var w = layer >= 0 ? _layerGains[layer * Chunk + i] : 1f;
            mix[2 * i] += left * w;
            mix[2 * i + 1] += right * w;
            position += _ratio;
        }

        shot.Position = position;
    }

    // Per-sample layer gains for the chunk (one-pole towards the targets, ~10 ms) and the filter cutoffs (per chunk).
    private void PrepareLayerGains(int n, int layers)
    {
        for (var l = 0; l < layers; l++)
        {
            var gains = _layerGains.AsSpan(l * Chunk, n);
            var gain = _gain[l];
            var target = _gainTarget[l];
            if (gain == target)
            {
                gains.Fill(gain);
            }
            else
            {
                for (var i = 0; i < n; i++)
                {
                    gain += (target - gain) * _ramp;
                    gains[i] = gain;
                }

                if (MathF.Abs(gain - target) < 1e-5f)
                    gain = target;
                _gain[l] = gain;
            }

            if (_cutoff[l] != _cutoffTarget[l])
            {
                var k = 1f - MathF.Pow(1f - _ramp, n);
                var c = _cutoff[l] + (_cutoffTarget[l] - _cutoff[l]) * k;
                _cutoff[l] = MathF.Abs(c - _cutoffTarget[l]) < 1e-3f ? _cutoffTarget[l] : c;
                UpdateFilter(l);
            }
        }
    }

    private bool CutoffMoving()
    {
        var layers = _runtime?.LayerCount ?? 0;
        for (var l = 0; l < layers; l++)
        {
            if (_cutoff[l] != _cutoffTarget[l])
                return true;
        }

        return false;
    }

    private void UpdateFilter(int layer)
    {
        _filterTemplate[layer].Set(MathF.Exp(_cutoff[layer]), Svf.ButterworthQ, _deviceRate);
        foreach (var group in _groups)
        {
            group.Lanes[layer].FilterL.CopyCoefficients(_filterTemplate[layer]);
            group.Lanes[layer].FilterR.CopyCoefficients(_filterTemplate[layer]);
        }

        for (var i = 0; i < OneShots; i++)
        {
            if (_oneShots[i].Layer != layer)
                continue;
            _oneShots[i].FilterL.CopyCoefficients(_filterTemplate[layer]);
            _oneShots[i].FilterR.CopyCoefficients(_filterTemplate[layer]);
        }
    }

    // The group's fade envelope over the chunk (linear in music frames).
    private ReadOnlySpan<float> PrepareEnvelope(LaneGroup group, int n)
    {
        var envelope = _envelope.AsSpan(0, n);
        if (group.EnvelopeStep == 0)
        {
            envelope.Fill(group.Envelope);
            return envelope;
        }

        var e = group.Envelope;
        for (var i = 0; i < n; i++)
        {
            e += group.EnvelopeStep;
            if (group.FadeIn ? e >= 1f : e <= 0f)
            {
                e = group.FadeIn ? 1f : 0f;
                group.EnvelopeStep = 0;
                envelope[i..].Fill(e);
                break;
            }

            envelope[i] = e;
        }

        group.Envelope = e;
        return envelope;
    }

    private void WriteOut(Span<float> buffer, int offset, int n, int channels)
    {
        var mix = _mix;
        for (var i = 0; i < n; i++)
        {
            if (_volume != _volumeTarget)
            {
                _volume += (_volumeTarget - _volume) * _ramp;
                if (MathF.Abs(_volume - _volumeTarget) < 1e-5f)
                    _volume = _volumeTarget;
            }

            var w = _volume;
            if (_state == VoiceState.Halting)
            {
                _haltGain = Math.Max(0f, _haltGain - _haltStep);
                w *= _haltGain;
            }

            var o = (offset + i) * channels;
            buffer[o] += mix[2 * i] * w;
            if (channels > 1)
                buffer[o + 1] += mix[2 * i + 1] * w;
        }
    }

    private void AfterChunk()
    {
        if (_state == VoiceState.Halting)
        {
            if (_haltGain <= 0f)
                Finish(AudioEventType.MusicStopped);
            return;
        }

        for (var g = 0; g < Groups; g++)
        {
            var group = _groups[g];
            if (group.State == GroupState.Fading && group.Envelope <= 0f)
                FreeGroup(g, group.Action);
        }

        if (_state != VoiceState.Ending || _queued > 0)
            return;
        for (var g = 0; g < Groups; g++)
        {
            if (_groups[g].State is GroupState.Playing or GroupState.Fading)
                return;
        }

        for (var i = 0; i < OneShots; i++)
        {
            if (_oneShots[i].Active)
                return;
        }

        Finish(_stopping ? AudioEventType.MusicStopped : AudioEventType.MusicFinished);
    }

    private void Finish(AudioEventType type)
    {
        for (var g = 0; g < Groups; g++)
        {
            if (_groups[g].State != GroupState.Free)
                FreeGroup(g, _groups[g].Action);
        }

        for (var i = 0; i < OneShots; i++)
            _oneShots[i].Active = false;
        _queued = 0;
        _section = -1;
        _currentGroup = -1;
        Post(type, (long)_clock, 0, -1, 0, -1);
        _state = VoiceState.Idle;
        Enabled = false;
    }

    private void FreeAllWaiting()
    {
        for (var i = 0; i < _queued; i++)
        {
            if (_queue[i].Kind == ActionKind.Start)
                Post(AudioEventType.MusicMissed, _queue[i].Frame, 0, _queue[i].Section, _queue[i].Action, -1);
        }

        for (var g = 0; g < Groups; g++)
        {
            if (_groups[g].State is GroupState.Waiting or GroupState.Prefetched)
                FreeGroup(g, _groups[g].Action);
        }
    }

    private void FreeGroup(int index, int action)
    {
        if ((uint)index >= Groups)
            return;
        var group = _groups[index];
        if (index == _currentGroup)
            _currentGroup = -1;
        group.Reset();
        Post(AudioEventType.MusicGroupFreed, (long)_clock, 0, -1, action, index);
    }

    private void Post(AudioEventType type, long frame, long origin, int section, int action, int group)
    {
        if (_eventCount == EventCapacity)
            return; // the game thread is far behind; events are informational beyond this point
        _events[(_eventHead + _eventCount) % EventCapacity] = new AudioEvent
        {
            Type = type,
            Index = Index,
            Generation = Generation,
            Frame = frame,
            Origin = origin,
            Section = section,
            Action = action,
            Group = group,
        };
        _eventCount++;
    }

    private static float Sanitize(float gain) => float.IsFinite(gain) ? Math.Clamp(gain, 0f, 16f) : 0f;

    private enum VoiceState : byte
    {
        Idle,
        /// <summary>Waiting (clock held at 0) for the first section's lanes.</summary>
        Starting,
        Playing,
        /// <summary>No section any more; fades and tails ring out.</summary>
        Ending,
        /// <summary>A few milliseconds' fade, then idle.</summary>
        Halting,
    }

    private enum GroupState : byte
    {
        Free,
        /// <summary>Holds a scheduled start.</summary>
        Waiting,
        /// <summary>Holds the current pass's continuation.</summary>
        Prefetched,
        Playing,
        Fading,
    }

    private enum ActionKind : byte
    {
        Start,
        Stinger,
        Stop,
    }

    private struct Scheduled
    {
        public ActionKind Kind;
        public int Action;
        public long Frame;
        public int Section;
        public int Then;
        public int Group;
        public long EntryOffset;
        public long FadeOutFrames;
        public long FadeInFrames;
        public bool Tails;
        public int StreamGeneration;
    }

    private sealed class LaneGroup
    {
        public readonly Lane[] Lanes = new Lane[MusicVoice.Lanes];
        public GroupState State;
        public int Action;
        public int Section = -1;
        public int StreamGeneration;
        public long StartFrame;
        public long Length;
        public bool Loop;
        public double Fraction;
        public float Envelope = 1f;
        public float EnvelopeStep;
        public bool FadeIn;

        public void Reset()
        {
            State = GroupState.Free;
            Action = 0;
            Section = -1;
            Envelope = 1f;
            EnvelopeStep = 0;
            Fraction = 0;
            for (var l = 0; l < Lanes.Length; l++)
                Lanes[l] = default;
        }
    }

    private struct Lane
    {
        public bool Active;
        public AudioClipData? Clip;
        public AudioStreamChannel? Channel;
        public int Channels;
        public bool Synced;
        public long Limit;
        /// <summary>The next body frame to read (wraps at the length when the section loops).</summary>
        public long Next;
        /// <summary>Stream frames output as silence while the ring was dry, skipped once data arrives.</summary>
        public long Debt;
        public float S0L, S0R, S1L, S1R;
        public Svf FilterL, FilterR;
    }

    private struct OneShot
    {
        public bool Active;
        public AudioClipData? Clip;
        public int Layer;
        public double Position;
        public Svf FilterL, FilterR;
    }
}

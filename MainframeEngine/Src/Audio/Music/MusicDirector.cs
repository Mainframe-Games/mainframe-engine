namespace MainframeEngine.Audio.Music;

/// <summary>What a <see cref="MusicAction"/> asks the player to do.</summary>
public enum MusicActionKind : byte
{
    /// <summary>Start <see cref="MusicAction.Section"/> at the frame (the outgoing section fades or ends there).</summary>
    StartSection,

    /// <summary>Play stinger <see cref="MusicAction.Section"/> from the frame.</summary>
    Stinger,

    /// <summary>Stop at the frame (fading over <see cref="MusicAction.FadeOutFrames"/>).</summary>
    Stop,

    /// <summary>Drop the scheduled action <see cref="MusicAction.CancelId"/> (ignored once it has happened).</summary>
    Cancel,
}

/// <summary>
/// One thing to schedule at a frame of the music clock (frames at the plan's sample rate since <c>Play</c>).
/// </summary>
/// <param name="Kind">What to do.</param>
/// <param name="Id">The action's id, for <see cref="MusicDirector.OnMissed"/> and <see cref="MusicActionKind.Cancel"/>.</param>
/// <param name="Frame">When, on the music clock.</param>
/// <param name="Section">The section or stinger to start.</param>
/// <param name="EntryOffset">Frames into <see cref="Section"/> to start from.</param>
/// <param name="Then">After <see cref="Section"/> ends: its own index (loop), another section, or −1 (end).</param>
/// <param name="FadeOutFrames">The outgoing section's fade after the boundary (0: it stops there).</param>
/// <param name="FadeInFrames">The incoming section's fade-in.</param>
/// <param name="Tails">The outgoing section ends at its end: play its tails over what comes next.</param>
/// <param name="CancelId">For <see cref="MusicActionKind.Cancel"/>: the action to drop.</param>
public readonly record struct MusicAction(
    MusicActionKind Kind,
    int Id,
    long Frame,
    int Section = -1,
    long EntryOffset = 0,
    int Then = -1,
    long FadeOutFrames = 0,
    long FadeInFrames = 0,
    bool Tails = false,
    int CancelId = 0);

/// <summary>
/// The adaptive-music state machine of one playing instance: parameters and their seek, layer gains and cutoffs, rule
/// selection and quantization. It keeps a prediction of what is playing (section, pass start, continuation) and hands
/// out <see cref="MusicAction"/>s for frames at or after the caller's earliest schedulable frame. The editor's live
/// preview runs it on its render thread with no lookahead; a game runs it on the game thread with a lookahead, and the
/// audio thread carries the actions out. Both make the same decisions, so a transition lands on the same frame.
/// </summary>
/// <remarks>
/// <para>Rules are evaluated on every <see cref="Update"/>: the first matching rule (in list order) is the wanted one.
/// A scheduled rule transition is cancelled when its rule no longer wins, unless its frame is before the earliest
/// schedulable frame (it is committed: the audio thread may already be carrying it out). A <see cref="GoToSection"/>
/// is never overridden by rules.</para>
/// <para>Nothing allocates after construction.</para>
/// </remarks>
public sealed class MusicDirector
{
    /// <summary>The most actions one <see cref="Update"/> writes; pass a span at least this long.</summary>
    public const int MaxActionsPerUpdate = 8;

    private readonly MusicAction[] _queue = new MusicAction[MaxActionsPerUpdate];
    private readonly float[] _gainDb;
    private readonly float[] _cutoffHz;
    private int _queued;
    private int _nextId = 1;
    private long _lastNow;
    private int _parameterVersion = -1;

    private int _section = -1;
    private long _passStart;
    private int _then = -1;

    private Scheduled _pending;
    private Snapshot _before;

    private Request _manual;
    private Request _stop;
    private Request _stinger;
    private int _stingerId;

    public MusicDirector(MusicPlan plan)
    {
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        Parameters = plan.CreateParameters();
        _gainDb = new float[plan.Layers.Length];
        _cutoffHz = new float[plan.Layers.Length];
        plan.EvaluateLayers(Parameters, _gainDb, _cutoffHz);
    }

    public MusicPlan Plan { get; }

    /// <summary>This instance's parameters; set targets here (they apply at the next <see cref="Update"/>).</summary>
    public MusicParameterSet Parameters { get; }

    public bool IsPlaying { get; private set; }

    /// <summary>The music ended (a section without continuation, or <see cref="Stop"/>).</summary>
    public bool IsFinished { get; private set; }

    /// <summary>The section playing as of the last <see cref="Update"/>, or −1.</summary>
    public int CurrentSection => _section;

    /// <summary>Music frame where the current pass of <see cref="CurrentSection"/> began (its frame 0).</summary>
    public long PassStart => _passStart;

    /// <summary>What follows the current section: itself (loop), another section, or −1.</summary>
    public int CurrentThen => _then;

    /// <summary>The destination of a scheduled transition, or −1.</summary>
    public int PendingSection => _pending.Active && !_pending.IsStop ? _pending.Destination : -1;

    /// <summary>The music frame of the scheduled transition or stop, or −1.</summary>
    public long PendingFrame => _pending.Active ? _pending.Frame : -1;

    /// <summary>The rule behind the scheduled transition, or −1 (none, or <see cref="GoToSection"/>).</summary>
    public int PendingRule => _pending.Active ? _pending.Rule : -1;

    /// <summary>Bumped when a layer gain or cutoff changes.</summary>
    public int LayerVersion { get; private set; }

    /// <summary>Layer gain in dB (−80: silent).</summary>
    public float LayerGainDb(int layer) => _gainDb[layer];

    /// <summary>Layer filter cutoff in Hz.</summary>
    public float LayerCutoffHz(int layer) => _cutoffHz[layer];

    /// <summary>Starts at <paramref name="section"/> (−1: the plan's start) at music frame <paramref name="atFrame"/>.</summary>
    public void Play(int section = -1, long atFrame = 0)
    {
        if (section < 0)
            section = Plan.StartSection;
        if ((uint)section >= (uint)Plan.Sections.Length || Plan.Sections[section].Kind != MusicSectionKind.Section)
            throw new ArgumentOutOfRangeException(nameof(section), section, "Not a playable section.");
        (_pending, _before, _manual, _stop, _stinger) = (default, default, default, default, default);
        _queued = 0;
        IsPlaying = true;
        IsFinished = false;
        _section = section;
        _passStart = atFrame;
        _then = Plan.Sections[section].Then;
        _lastNow = atFrame;
        Enqueue(new MusicAction(MusicActionKind.StartSection, NextId(), atFrame, section, Then: _then));
    }

    /// <summary>Goes to <paramref name="section"/> at the next <paramref name="quantize"/> boundary (replaces a scheduled transition).</summary>
    public void GoToSection(int section, MusicQuantize quantize = MusicQuantize.Bar, float fadeOutBeats = 0.5f)
    {
        if ((uint)section >= (uint)Plan.Sections.Length || Plan.Sections[section].Kind != MusicSectionKind.Section)
            throw new ArgumentOutOfRangeException(nameof(section), section, "Not a playable section.");
        _manual = new Request(true, section, quantize, fadeOutBeats);
    }

    /// <summary>Fires stinger <paramref name="stinger"/> at its quantum (or <paramref name="quantize"/>), pickup first.</summary>
    public void TriggerStinger(int stinger, MusicQuantize? quantize = null)
    {
        if ((uint)stinger >= (uint)Plan.Sections.Length || Plan.Sections[stinger].Kind != MusicSectionKind.Stinger)
            throw new ArgumentOutOfRangeException(nameof(stinger), stinger, "Not a stinger.");
        var q = quantize ?? Plan.Sections[stinger].Quantize;
        _stinger = new Request(true, stinger, q is MusicQuantize.SectionEnd or MusicQuantize.ExitMarker ? MusicQuantize.Beat : q, 0);
    }

    /// <summary>Stops at the next <paramref name="quantize"/> boundary, fading over <paramref name="fadeBeats"/>.</summary>
    public void Stop(MusicQuantize quantize = MusicQuantize.Immediate, float fadeBeats = 0.5f) =>
        _stop = new Request(true, -1, quantize, fadeBeats);

    /// <summary>
    /// The player could not carry out action <paramref name="id"/> at its frame (a streamed lane was not ready). A
    /// transition goes back to the state before it and is scheduled again at the next eligible boundary.
    /// </summary>
    public void OnMissed(int id)
    {
        if (id == 0)
            return;
        if (_pending.Active && _pending.Id == id)
        {
            Requeue(_pending);
            CancelStingerOf(_pending);
            _pending = default;
        }
        else if (_before.Valid && _before.AppliedId == id)
        {
            (_section, _passStart, _then, IsPlaying, IsFinished) = (_before.Section, _before.PassStart, _before.Then, _before.Playing, _before.Finished);
            Requeue(_before.Applied);
            _before = default;
        }
        else if (id == _stingerId && _stingerId != 0)
        {
            _stingerId = 0;
        }
    }

    /// <summary>
    /// Moves to music frame <paramref name="now"/>: applies scheduled actions and authored continuations that are due,
    /// advances parameter seeks, evaluates layers and rules, and writes the actions to schedule (each at or after
    /// <paramref name="earliest"/>) into <paramref name="actions"/>. Returns how many it wrote.
    /// </summary>
    public int Update(long now, long earliest, Span<MusicAction> actions)
    {
        if (actions.Length < MaxActionsPerUpdate)
            throw new ArgumentException($"Pass room for {MaxActionsPerUpdate} actions.", nameof(actions));
        earliest = Math.Max(earliest, now);
        if (now > _lastNow)
        {
            Parameters.Advance((now - _lastNow) / (double)Plan.Grid.SampleRate);
            _lastNow = now;
        }

        if (Parameters.Version != _parameterVersion)
        {
            _parameterVersion = Parameters.Version;
            EvaluateLayers();
        }

        if (IsPlaying)
        {
            Advance(now);
            if (IsPlaying)
            {
                if (_stop.Active)
                    ScheduleStop(earliest);
                else if (_manual.Active)
                    ScheduleManual(earliest);
                else
                    EvaluateRules(earliest);
                if (_stinger.Active)
                    ScheduleStinger(earliest);
            }
        }

        var count = _queued;
        _queue.AsSpan(0, count).CopyTo(actions);
        _queued = 0;
        return count;
    }

    /// <summary>Frames from the current pass's start to <paramref name="now"/>.</summary>
    public long LocalFrame(long now) => now - _passStart;

    // --- timeline --------------------------------------------------------------------------------

    private void Advance(long now)
    {
        while (IsPlaying)
        {
            var end = _passStart + Plan.Sections[_section].LengthFrames;
            var pendingDue = _pending.Active && _pending.Frame <= now;
            var endDue = end <= now;
            if (pendingDue && (!endDue || _pending.Frame <= end))
            {
                Apply(_pending);
                _pending = default;
            }
            else if (endDue)
            {
                if (_then == _section)
                {
                    _passStart = end;
                }
                else if (_then >= 0)
                {
                    _section = _then;
                    _passStart = end;
                    _then = Plan.Sections[_section].Then;
                }
                else
                {
                    IsPlaying = false;
                    IsFinished = true;
                }
            }
            else
            {
                break;
            }
        }
    }

    private void Apply(in Scheduled s)
    {
        _before = new Snapshot(true, s.Id, s, _section, _passStart, _then, IsPlaying, IsFinished);
        if (s.IsStop)
        {
            IsPlaying = false;
            IsFinished = true;
            return;
        }

        _section = s.StartSection;
        _passStart = s.Frame - s.EntryOffset;
        _then = s.Then;
    }

    // --- scheduling ------------------------------------------------------------------------------

    private void EvaluateRules(long earliest)
    {
        if (_pending.Active && (_pending.Manual || _pending.IsStop))
            return;
        var wanted = Plan.FirstMatchingRule(_section, Parameters);
        var wantedIndex = wanted?.Index ?? -1;
        if (_pending.Active)
        {
            if (_pending.Rule == wantedIndex || _pending.Frame < earliest)
                return; // still wanted, or committed
            Cancel(_pending);
        }

        if (wanted is not null)
            ScheduleRule(wanted, earliest);
    }

    private void ScheduleRule(MusicRule rule, long earliest)
    {
        var pickup = rule.Stinger >= 0 ? Plan.Grid.FramesForBeats(Plan.Sections[rule.Stinger].PickupBeats) : 0;
        var boundary = FindBoundary(earliest + pickup, rule.Quantize, rule.Every, out var atEnd);
        if (boundary < 0)
            return; // past this section's end: rules run again from the next section
        var destination = rule.To;
        var fadeOut = rule.FadeOutBeats ?? (rule.Quantize is MusicQuantize.SectionEnd or MusicQuantize.ExitMarker ? 0f : 0.5f);
        var entry = rule.Entry == MusicEntry.Sync && rule.Segment < 0 ? (boundary - _passStart) % Plan.Sections[destination].LengthFrames : 0;
        var scheduled = Schedule(boundary, destination, rule.Segment, entry, fadeOut, rule.FadeInBeats, atEnd, rule.Index, manual: false);
        if (rule.Stinger >= 0)
        {
            scheduled.StingerId = NextId();
            Enqueue(new MusicAction(MusicActionKind.Stinger, scheduled.StingerId, boundary - pickup, rule.Stinger));
        }

        _pending = scheduled;
    }

    private void ScheduleManual(long earliest)
    {
        if (_pending.Active)
        {
            if (_pending.Frame < earliest)
                return; // committed: the request waits until it has happened
            Cancel(_pending);
        }

        var boundary = FindBoundary(earliest, _manual.Quantize, 1, out var atEnd);
        if (boundary < 0)
            return;
        _pending = Schedule(boundary, _manual.Section, -1, 0, _manual.FadeBeats, 0, atEnd, -1, manual: true);
        _manual = default;
    }

    private void ScheduleStop(long earliest)
    {
        if (_pending.Active)
        {
            if (_pending.Frame < earliest)
                return;
            Cancel(_pending);
        }

        var boundary = FindBoundary(earliest, _stop.Quantize, 1, out var atEnd);
        if (boundary < 0)
            return;
        var id = NextId();
        var fade = Plan.Grid.FramesForBeats(_stop.FadeBeats);
        Enqueue(new MusicAction(MusicActionKind.Stop, id, boundary, FadeOutFrames: fade, Tails: atEnd));
        _pending = new Scheduled { Active = true, Id = id, Frame = boundary, IsStop = true, Rule = -1, Destination = -1 };
        _stop = default;
    }

    private void ScheduleStinger(long earliest)
    {
        var stinger = _stinger.Section;
        var pickup = Plan.Grid.FramesForBeats(Plan.Sections[stinger].PickupBeats);
        var boundary = FindBoundary(earliest + pickup, _stinger.Quantize, 1, out _);
        if (boundary < 0)
            return;
        _stingerId = NextId();
        Enqueue(new MusicAction(MusicActionKind.Stinger, _stingerId, boundary - pickup, stinger));
        _stinger = default;
    }

    private Scheduled Schedule(long boundary, int destination, int segment, long entry, float fadeOutBeats, float fadeInBeats, bool atEnd,
        int rule, bool manual)
    {
        var start = segment >= 0 ? segment : destination;
        var then = segment >= 0 ? destination : Plan.Sections[destination].Then;
        var id = NextId();
        var fadeOut = Plan.Grid.FramesForBeats(fadeOutBeats);
        var fadeIn = Plan.Grid.FramesForBeats(fadeInBeats);
        Enqueue(new MusicAction(MusicActionKind.StartSection, id, boundary, start, entry, then, fadeOut, fadeIn, atEnd));
        return new Scheduled
        {
            Active = true,
            Id = id,
            Frame = boundary,
            Destination = destination,
            StartSection = start,
            Then = then,
            EntryOffset = entry,
            Rule = rule,
            Manual = manual,
            FadeOutBeats = fadeOutBeats,
        };
    }

    /// <summary>The first boundary at or after <paramref name="from"/> in the current pass, or the next pass of a loop; −1 if none.</summary>
    private long FindBoundary(long from, MusicQuantize quantize, int every, out bool atEnd)
    {
        atEnd = false;
        var section = Plan.Sections[_section];
        var length = section.LengthFrames;
        var passStart = _passStart;
        for (var pass = 0; pass < 2; pass++)
        {
            var local = from - passStart;
            var b = Plan.Grid.NextQuantum(local, length, quantize, every, section.ExitFrames);
            if (b >= 0)
            {
                atEnd = b == length;
                return passStart + b;
            }

            if (_then != _section)
                return -1;
            passStart += length;
        }

        return -1;
    }

    private void Cancel(in Scheduled s)
    {
        Enqueue(new MusicAction(MusicActionKind.Cancel, NextId(), s.Frame, CancelId: s.Id));
        CancelStingerOf(s);
        _pending = default;
    }

    private void CancelStingerOf(in Scheduled s)
    {
        if (s.StingerId != 0)
            Enqueue(new MusicAction(MusicActionKind.Cancel, NextId(), s.Frame, CancelId: s.StingerId));
    }

    private void Requeue(in Scheduled s)
    {
        if (s.IsStop)
            _stop = new Request(true, -1, MusicQuantize.Immediate, 0);
        else if (s.Manual)
            _manual = new Request(true, s.Destination, MusicQuantize.Bar, s.FadeOutBeats);
        // A rule transition needs nothing: the rules are evaluated again at the next update.
    }

    private void EvaluateLayers()
    {
        Span<float> gain = stackalloc float[MusicPlan.MaxLayers];
        Span<float> cutoff = stackalloc float[MusicPlan.MaxLayers];
        Plan.EvaluateLayers(Parameters, gain, cutoff);
        var changed = false;
        for (var i = 0; i < _gainDb.Length; i++)
        {
            if (_gainDb[i] != gain[i] || _cutoffHz[i] != cutoff[i])
            {
                (_gainDb[i], _cutoffHz[i]) = (gain[i], cutoff[i]);
                changed = true;
            }
        }

        if (changed)
            LayerVersion++;
    }

    private void Enqueue(in MusicAction action)
    {
        if (_queued < _queue.Length)
            _queue[_queued++] = action;
    }

    private int NextId() => _nextId++;

    private struct Scheduled
    {
        public bool Active;
        public int Id;
        public int StingerId;
        public long Frame;
        public int Destination;
        public int StartSection;
        public int Then;
        public long EntryOffset;
        public int Rule;
        public bool Manual;
        public bool IsStop;
        public float FadeOutBeats;
    }

    private readonly record struct Snapshot(bool Valid, int AppliedId, Scheduled Applied, int Section, long PassStart, int Then, bool Playing, bool Finished);

    private readonly record struct Request(bool Active, int Section, MusicQuantize Quantize, float FadeBeats);
}

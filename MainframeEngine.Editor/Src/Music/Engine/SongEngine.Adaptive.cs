using MainframeEngine.Audio.Dsp;
using MainframeEngine.Audio.Music;

namespace MainframeEngine.Editor.Music;

// Adaptive playback (Shift+Space, docs/design/future/adaptive-music.md "Live preview"): the snapshot's MusicDirector runs
// on the render thread with no lookahead. Each block starts with its Update; the actions it returns are kept in a small
// queue sorted by music frame and carried out on their exact frame (the block is split there), as are the authored
// continuations at section ends (loop, then, end). The sequencer plays the current section's song-frame range; a second
// cursor plays a stinger's range over it. Tracks sum into one bus per layer, filtered and gained by the director's layer
// values (ramped per sample, faded in after a transition); master inserts are bypassed, as in the bake.
public sealed partial class SongEngine
{
    /// <summary>Actions waiting for their frame (more are dropped and counted).</summary>
    public const int MaxQueuedActions = 32;

    /// <summary>Layer buses (a plan's layers beyond it share the last).</summary>
    public const int MaxLayerBuses = 16;

    /// <summary>Parameters whose values are published to the UI.</summary>
    public const int MaxPublishedParameters = 32;

    private const int SectionLogSize = 64;

    private readonly MusicAction[] _updates = new MusicAction[MusicDirector.MaxActionsPerUpdate];
    private readonly MusicAction[] _actions = new MusicAction[MaxQueuedActions];
    private readonly float[][] _layerBuffers = CreateLayerBuffers();
    private readonly float[] _layerGain = CreateGains();
    private readonly Svf[] _filterL = new Svf[MaxLayerBuses];
    private readonly Svf[] _filterR = new Svf[MaxLayerBuses];
    private readonly int[] _pubGainBits = new int[MaxLayerBuses];
    private readonly int[] _pubParamBits = new int[MaxPublishedParameters];
    private readonly int[] _logSection = new int[SectionLogSize];
    private readonly long[] _logFrame = new long[SectionLogSize];
    private int _logCount;
    private int _actionCount;
    private int _droppedActions;
    private bool _adaptive;
    private AdaptiveSnapshot? _adaptiveOf;
    private long _clock;
    private long _blockClock;
    private int _section = -1;
    private long _passStart;
    private int _then = -1;
    private int _stinger = -1;
    private long _stingerPos;
    private long _fadeStart = long.MaxValue;
    private long _fadeFrames;
    private long _skipStartsBefore = long.MinValue;

    // Published after every adaptive block (UI thread reads).
    private bool _pubPlaying;
    private int _pubSection = -1;
    private int _pubPending = -1;
    private int _pubStinger = -1;
    private long _pubPendingFrames = -1;
    private long _pubLocal;
    private long _lastBlockClock;
    private long _lastBlockSong;

    private static float[][] CreateLayerBuffers()
    {
        var buffers = new float[MaxLayerBuses][];
        for (var i = 0; i < buffers.Length; i++)
            buffers[i] = new float[BlockFrames * 2];
        return buffers;
    }

    private static float[] CreateGains()
    {
        var gains = new float[MaxLayerBuses];
        Array.Fill(gains, float.NaN);
        return gains;
    }

    /// <summary>Adaptive playback runs (as of the last block).</summary>
    public bool IsAdaptive => Volatile.Read(ref _pubPlaying);

    /// <summary>The music clock (frames since adaptive playback started) at the start of the last block.</summary>
    public long LastBlockClock => Volatile.Read(ref _lastBlockClock);

    /// <summary>The song frame the last adaptive block started at.</summary>
    public long LastBlockSong => Volatile.Read(ref _lastBlockSong);

    /// <summary>Actions dropped because <see cref="MaxQueuedActions"/> were waiting.</summary>
    public int DroppedActions => Volatile.Read(ref _droppedActions);

    /// <summary>UI thread: what adaptive playback is doing (indices into the current snapshot's plan).</summary>
    public AdaptiveState AdaptiveState => new(Volatile.Read(ref _pubPlaying), Volatile.Read(ref _pubSection), Volatile.Read(ref _pubPending),
        Volatile.Read(ref _pubPendingFrames), Volatile.Read(ref _pubLocal), Volatile.Read(ref _pubStinger));

    /// <summary>UI thread: a layer's evaluated gain in dB while adaptive playback runs.</summary>
    public float LayerGainDb(int layer) =>
        (uint)layer < MaxLayerBuses ? BitConverter.Int32BitsToSingle(Volatile.Read(ref _pubGainBits[layer])) : -80f;

    /// <summary>UI thread: a parameter's smoothed value while adaptive playback runs.</summary>
    public float ParameterValue(int parameter) =>
        (uint)parameter < MaxPublishedParameters ? BitConverter.Int32BitsToSingle(Volatile.Read(ref _pubParamBits[parameter])) : 0f;

    /// <summary>Sections started since adaptive playback began, at most the last 64 (tests: the decisions the director made).</summary>
    public int SectionStartCount => Volatile.Read(ref _logCount);

    /// <summary>The <paramref name="index"/>th section start: its plan section and music frame.</summary>
    public (int Section, long Frame) SectionStartAt(int index) => (_logSection[index % SectionLogSize], _logFrame[index % SectionLogSize]);

    /// <summary>Starts adaptive playback at plan section <paramref name="section"/> (−1: the plan's start), from music frame 0.</summary>
    public void PlayAdaptive(int section = -1) => Enqueue(new Command(CommandType.AdaptivePlay, section, 0, 0, 0));

    /// <summary>Sets an adaptive parameter's target (it seeks while playing; jumps when stopped).</summary>
    public void SetParameter(string name, float value)
    {
        ArgumentNullException.ThrowIfNull(name);
        Enqueue(new Command(CommandType.AdaptiveParameter, BitConverter.SingleToInt32Bits(value), 0, 0, 0, name));
    }

    /// <summary>Fires a stinger at its quantum (adaptive playback only).</summary>
    public void TriggerStinger(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        Enqueue(new Command(CommandType.AdaptiveStinger, 0, 0, 0, 0, name));
    }

    /// <summary>Goes to a section at the next <paramref name="quantize"/> boundary (adaptive playback only).</summary>
    public void GoToSection(string name, MusicQuantize quantize = MusicQuantize.Bar)
    {
        ArgumentNullException.ThrowIfNull(name);
        Enqueue(new Command(CommandType.AdaptiveGoTo, (long)quantize, 0, 0, 0, name));
    }

    // --- commands (render thread) ----------------------------------------------------------------

    private void AdaptiveCommand(in Command c, SongSnapshot? song)
    {
        if (song?.Adaptive is not { } ad)
            return;
        var director = ad.Director;
        var plan = ad.Plan;
        switch (c.Type)
        {
            case CommandType.AdaptivePlay:
                StartAdaptive(song, ad, (int)c.Value);
                break;
            case CommandType.AdaptiveParameter when c.Name is { } name:
                director.Parameters.SetTarget(name, BitConverter.Int32BitsToSingle((int)c.Value), immediate: !_adaptive);
                break;
            case CommandType.AdaptiveStinger when _adaptive && c.Name is { } stinger:
                if (plan.FindSection(stinger) is >= 0 and var s && plan.Sections[s].Kind == MusicSectionKind.Stinger)
                    director.TriggerStinger(s);
                break;
            case CommandType.AdaptiveGoTo when _adaptive && c.Name is { } to:
                if (plan.FindSection(to) is >= 0 and var t && plan.Sections[t].Kind == MusicSectionKind.Section)
                    director.GoToSection(t, (MusicQuantize)c.Value);
                break;
        }
    }

    private void StartAdaptive(SongSnapshot song, AdaptiveSnapshot ad, int section)
    {
        var plan = ad.Plan;
        if ((uint)section >= (uint)plan.Sections.Length || plan.Sections[section].Kind != MusicSectionKind.Section)
            section = plan.StartSection;
        if ((uint)section >= (uint)plan.Sections.Length || plan.Sections[section].Kind != MusicSectionKind.Section)
            section = Array.FindIndex(plan.Sections, static s => s.Kind == MusicSectionKind.Section);
        if (section < 0)
            return;
        AllNotesOff(song);
        _actionCount = 0;
        _clock = 0;
        _section = -1;
        _then = -1;
        _stinger = -1;
        _fadeStart = long.MaxValue;
        _skipStartsBefore = long.MinValue;
        _logCount = 0;
        Array.Fill(_layerGain, float.NaN);
        ad.Director.Play(section);
        _adaptiveOf = ad;
        _adaptive = true;
        _playing = true;
    }

    private void EndAdaptive(SongSnapshot? song, int offset)
    {
        if (song is not null)
        {
            foreach (var track in song.Tracks)
            {
                var rt = _runtimes[track.Slot];
                Release(rt, rt.Held, ref rt.HeldCount, offset);
                Release(rt, rt.OverlayHeld, ref rt.OverlayCount, offset);
            }
        }

        _adaptive = false;
        _playing = false;
        _section = -1;
        _stinger = -1;
        _actionCount = 0;
        _adaptiveOf = null;
        Volatile.Write(ref _pubPlaying, false);
        Volatile.Write(ref _pubSection, -1);
        Volatile.Write(ref _pubPending, -1);
        Volatile.Write(ref _pubStinger, -1);
    }

    // A new snapshot while adaptive playback runs: its director takes over the section, the pass and the parameters.
    private void TransferAdaptive(SongSnapshot next)
    {
        var old = _adaptiveOf;
        var nu = next.Adaptive;
        if (!_adaptive || ReferenceEquals(old, nu))
            return;
        if (old is null || nu is null || !nu.CanPlay)
        {
            EndAdaptive(next, 0);
            return;
        }

        var from = old.Director.Parameters;
        var to = nu.Director.Parameters;
        for (var i = 0; i < from.Count; i++)
        {
            var j = to.IndexOf(from.NameOf(i));
            if (j < 0)
                continue;
            to.SetTarget(j, from.GetValue(i), immediate: true);
            to.SetTarget(j, from.GetTarget(i));
        }

        var plan = nu.Plan;
        var section = _section >= 0 ? plan.FindSection(old.Plan.Sections[_section].Id) : -1;
        if (section < 0 || plan.Sections[section].Kind != MusicSectionKind.Section)
        {
            section = plan.StartSection >= 0 && plan.Sections[plan.StartSection].Kind == MusicSectionKind.Section
                ? plan.StartSection
                : Array.FindIndex(plan.Sections, static s => s.Kind == MusicSectionKind.Section);
            _passStart = _clock;
        }
        else if (_clock - _passStart >= plan.Sections[section].LengthFrames)
        {
            _passStart = _clock; // the section got shorter than where it was: its pass starts again here
        }

        nu.Director.Play(section, _passStart);
        _skipStartsBefore = _clock; // the director's opening StartSection is the pass already playing
        _section = section;
        _then = plan.Sections[section].Then;
        _position = nu.SectionStarts[section] + (_clock - _passStart);
        if (_stinger >= 0)
        {
            var stinger = plan.FindSection(old.Plan.Sections[_stinger].Id);
            _stinger = stinger >= 0 && plan.Sections[stinger].Kind == MusicSectionKind.Stinger ? stinger : -1;
        }

        _actionCount = 0; // the old director's scheduled actions: the new one schedules what still applies
        _adaptiveOf = nu;
    }

    // --- sequencing ------------------------------------------------------------------------------

    private void SequenceAdaptive(SongSnapshot song, int frames)
    {
        if (song.Adaptive is not { } ad || !ReferenceEquals(ad, _adaptiveOf))
        {
            EndAdaptive(song, 0);
            return;
        }

        var director = ad.Director;
        var plan = ad.Plan;
        _blockClock = _clock;
        Volatile.Write(ref _lastBlockClock, _clock);
        Volatile.Write(ref _lastBlockSong, _position);
        var count = director.Update(_clock, _clock, _updates);
        for (var i = 0; i < count; i++)
        {
            var a = _updates[i];
            if (a.Kind == MusicActionKind.Cancel)
                RemoveAction(a.CancelId);
            else if (a.Kind != MusicActionKind.StartSection || a.Frame >= _skipStartsBefore)
                InsertAction(a);
        }

        _skipStartsBefore = long.MinValue;
        var offset = 0;
        while (offset < frames && _adaptive)
        {
            var untilAction = _actionCount > 0 ? _actions[0].Frame - _clock : long.MaxValue;
            if (untilAction <= 0)
            {
                var action = _actions[0];
                RemoveAt(0);
                Execute(song, ad, action, offset);
                continue;
            }

            var untilEnd = _section >= 0 ? plan.Sections[_section].LengthFrames - (_clock - _passStart) : long.MaxValue;
            if (untilEnd <= 0)
            {
                SectionEnd(song, ad, offset);
                continue;
            }

            var untilStinger = _stinger >= 0 ? plan.Sections[_stinger].LengthFrames - _stingerPos : long.MaxValue;
            if (untilStinger <= 0)
            {
                EndStinger(song, offset);
                continue;
            }

            var segment = (int)Math.Min(frames - offset, Math.Min(untilAction, Math.Min(untilEnd, untilStinger)));
            if (_section >= 0)
            {
                SequenceRange(song, _position, _position + segment, offset, ad.SectionEnds[_section], overlay: false);
                _position += segment;
            }

            if (_stinger >= 0)
            {
                var at = ad.SectionStarts[_stinger] + _stingerPos;
                SequenceRange(song, at, at + segment, offset, ad.SectionEnds[_stinger], overlay: true);
                _stingerPos += segment;
            }

            _clock += segment;
            offset += segment;
        }

        if (_adaptive)
            Publish(director);
        Volatile.Write(ref _position, _position);
    }

    // Notes starting at or after `limit` belong to the next section (the grid's rounding can put them a frame inside).
    private void SequenceRange(SongSnapshot song, long from, long to, int offset, long limit, bool overlay)
    {
        foreach (var track in song.Tracks)
        {
            var rt = _runtimes[track.Slot];
            if (overlay)
            {
                NoteOffs(rt, rt.OverlayHeld, ref rt.OverlayCount, from, to, offset);
                NoteOns(rt, rt.OverlayHeld, ref rt.OverlayCount, track.Notes, from, Math.Min(to, limit), offset);
            }
            else
            {
                NoteOffs(rt, rt.Held, ref rt.HeldCount, from, to, offset);
                NoteOns(rt, rt.Held, ref rt.HeldCount, track.Notes, from, Math.Min(to, limit), offset);
            }

            MixClips(rt, track.AudioClips, from, Math.Min(to, limit), offset);
        }
    }

    private void Execute(SongSnapshot song, AdaptiveSnapshot ad, in MusicAction action, int offset)
    {
        switch (action.Kind)
        {
            case MusicActionKind.StartSection when (uint)action.Section < (uint)ad.Plan.Sections.Length:
                Enter(song, ad, action.Section, action.EntryOffset, offset);
                _then = action.Then;
                if (action.FadeInFrames > 0)
                {
                    _fadeStart = _clock;
                    _fadeFrames = action.FadeInFrames;
                }

                break;
            case MusicActionKind.Stinger when (uint)action.Section < (uint)ad.Plan.Sections.Length:
                EndStinger(song, offset);
                _stinger = action.Section;
                _stingerPos = 0;
                break;
            case MusicActionKind.Stop:
                EndAdaptive(song, offset);
                break;
        }
    }

    private void Enter(SongSnapshot song, AdaptiveSnapshot ad, int section, long entry, int offset)
    {
        ReleaseMain(song, offset);
        entry = Math.Clamp(entry, 0, Math.Max(0, ad.Plan.Sections[section].LengthFrames - 1));
        _section = section;
        _passStart = _clock - entry;
        _then = ad.Plan.Sections[section].Then;
        _position = ad.SectionStarts[section] + entry;
        LogStart(section);
    }

    // The end of the current pass: the authored continuation (the StartSection that began it says which).
    private void SectionEnd(SongSnapshot song, AdaptiveSnapshot ad, int offset)
    {
        if (_then == _section)
        {
            ReleaseMain(song, offset);
            _passStart = _clock;
            _position = ad.SectionStarts[_section];
            LogStart(_section);
        }
        else if ((uint)_then < (uint)ad.Plan.Sections.Length)
        {
            Enter(song, ad, _then, 0, offset);
        }
        else
        {
            EndAdaptive(song, offset); // the music ends: what is still sounding rings out
        }
    }

    private void EndStinger(SongSnapshot song, int offset)
    {
        if (_stinger < 0)
            return;
        foreach (var track in song.Tracks)
        {
            var rt = _runtimes[track.Slot];
            Release(rt, rt.OverlayHeld, ref rt.OverlayCount, offset);
        }

        _stinger = -1;
    }

    private void ReleaseMain(SongSnapshot song, int offset)
    {
        foreach (var track in song.Tracks)
        {
            var rt = _runtimes[track.Slot];
            Release(rt, rt.Held, ref rt.HeldCount, offset);
        }
    }

    private void Release(TrackRuntime rt, HeldNote[] held, ref int count, int offset)
    {
        for (var i = 0; i < count; i++)
            AddEvent(rt, new NoteEvent(offset, held[i].Pitch, 0, false));
        count = 0;
    }

    private void InsertAction(in MusicAction action)
    {
        if (_actionCount == _actions.Length)
        {
            Interlocked.Increment(ref _droppedActions);
            return;
        }

        var at = _actionCount;
        while (at > 0 && _actions[at - 1].Frame > action.Frame)
        {
            _actions[at] = _actions[at - 1];
            at--;
        }

        _actions[at] = action;
        _actionCount++;
    }

    private void RemoveAction(int id)
    {
        for (var i = 0; i < _actionCount; i++)
        {
            if (_actions[i].Id != id)
                continue;
            RemoveAt(i);
            return;
        }
    }

    private void RemoveAt(int index)
    {
        Array.Copy(_actions, index + 1, _actions, index, _actionCount - index - 1);
        _actionCount--;
    }

    private void LogStart(int section)
    {
        var n = _logCount;
        _logSection[n % SectionLogSize] = section;
        _logFrame[n % SectionLogSize] = _clock;
        Volatile.Write(ref _logCount, n + 1);
    }

    private void Publish(MusicDirector director)
    {
        Volatile.Write(ref _pubPlaying, true);
        Volatile.Write(ref _pubSection, _section);
        Volatile.Write(ref _pubPending, director.PendingSection);
        Volatile.Write(ref _pubPendingFrames, director.PendingFrame >= 0 ? Math.Max(0, director.PendingFrame - _clock) : -1);
        Volatile.Write(ref _pubLocal, _clock - _passStart);
        Volatile.Write(ref _pubStinger, _stinger);
        var layers = Math.Min(director.Plan.Layers.Length, MaxLayerBuses);
        for (var l = 0; l < layers; l++)
            Volatile.Write(ref _pubGainBits[l], BitConverter.SingleToInt32Bits(director.LayerGainDb(l)));
        var parameters = Math.Min(director.Parameters.Count, MaxPublishedParameters);
        for (var p = 0; p < parameters; p++)
            Volatile.Write(ref _pubParamBits[p], BitConverter.SingleToInt32Bits(director.Parameters.GetValue(p)));
    }

    // --- layer buses -----------------------------------------------------------------------------

    private Span<float> LayerBuffer(AdaptiveSnapshot ad, TrackSnapshot track, int frames) =>
        _layerBuffers[Math.Min(ad.SlotLayers[track.Slot], MaxLayerBuses - 1)].AsSpan(0, frames * 2);

    private void ClearLayers(AdaptiveSnapshot ad, int frames)
    {
        var layers = Math.Min(Math.Max(1, ad.Plan.Layers.Length), MaxLayerBuses);
        for (var l = 0; l < layers; l++)
            _layerBuffers[l].AsSpan(0, frames * 2).Clear();
    }

    // Each layer bus: its filter (cutoff from the director), then its gain (ramped over the block) × the fade-in, into the mix.
    private void MixLayers(AdaptiveSnapshot ad, Span<float> output, int frames)
    {
        var director = ad.Director;
        var plan = ad.Plan;
        var layers = Math.Min(Math.Max(1, plan.Layers.Length), MaxLayerBuses);
        for (var l = 0; l < layers; l++)
        {
            var buffer = _layerBuffers[l].AsSpan(0, frames * 2);
            var filter = l < plan.Layers.Length ? plan.Layers[l].Filter : MusicLayerFilter.None;
            if (filter != MusicLayerFilter.None)
            {
                var cutoff = Math.Clamp(director.LayerCutoffHz(l), 20f, SampleRate * 0.45f);
                ref var left = ref _filterL[l];
                ref var right = ref _filterR[l];
                left.Set(cutoff, 0.7071f, SampleRate);
                right.Set(cutoff, 0.7071f, SampleRate);
                for (var f = 0; f < frames; f++)
                {
                    var lowL = left.Process(buffer[2 * f], out _, out var highL);
                    var lowR = right.Process(buffer[2 * f + 1], out _, out var highR);
                    buffer[2 * f] = filter == MusicLayerFilter.LowPass ? lowL : highL;
                    buffer[2 * f + 1] = filter == MusicLayerFilter.LowPass ? lowR : highR;
                }
            }

            var db = l < plan.Layers.Length ? director.LayerGainDb(l) : 0f;
            var target = db <= -80f ? 0f : AudioMath.DbToLinear(db);
            if (float.IsNaN(_layerGain[l]))
                _layerGain[l] = target;
            var gain = _layerGain[l];
            var step = (target - gain) / frames;
            for (var f = 0; f < frames; f++)
            {
                gain += step;
                var g = gain * Fade(_blockClock + f);
                output[2 * f] += buffer[2 * f] * g;
                output[2 * f + 1] += buffer[2 * f + 1] * g;
            }

            _layerGain[l] = target;
        }
    }

    private float Fade(long clock) =>
        clock < _fadeStart || _fadeFrames <= 0 ? 1f : Math.Min(1f, (clock - _fadeStart) / (float)_fadeFrames);
}

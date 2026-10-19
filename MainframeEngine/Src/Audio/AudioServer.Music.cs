using MainframeEngine.Audio;

namespace MainframeEngine;

// Adaptive music on the game thread: the music voice slots (AudioBusInfo.MusicVoices), their lane groups and stream
// channels, and the playing AdaptiveMusicInstances, which the server updates every frame. See MusicVoice for the audio
// thread and docs/design/future/adaptive-music.md.
public sealed partial class AudioServer
{
    private readonly List<AdaptiveMusicInstance> _musicInstances = new(8);
    private MusicSlot[] _musicSlots = [];
    private long _lateMusicTransitions;
    private HashSet<string>? _musicBusWarned;

    /// <summary>Music voices across every bus.</summary>
    public int TotalMusicVoices => _musicSlots.Length;

    /// <summary>The output's buffer length: how far behind the reported music clock the speakers are (an estimate).</summary>
    internal float OutputLatencySeconds => Math.Max(1, _options.BufferMilliseconds) / 1000f;

    /// <summary>
    /// Takes a free music voice of <paramref name="bus"/> (Master when there is no such bus) for <paramref name="owner"/>;
    /// −1 when every one is busy (counted in <see cref="AudioServerStats.Rejected"/>, warned once per bus).
    /// </summary>
    internal int AcquireMusicVoice(AdaptiveMusicInstance owner, string? bus, MusicRuntime runtime)
    {
        if (_disposed)
            return -1;
        var busIndex = FindBus(bus);
        if (busIndex < 0)
            busIndex = 0;
        var info = _buses[busIndex];
        var best = -1;
        for (var i = info.FirstMusicVoice; i < info.FirstMusicVoice + info.MusicVoices; i++)
        {
            if (!_musicSlots[i].Active && (best < 0 || _musicSlots[i].FreedSequence < _musicSlots[best].FreedSequence))
                best = i;
        }

        if (best < 0)
        {
            _rejected++;
            if ((_musicBusWarned ??= new HashSet<string>(StringComparer.Ordinal)).Add(info.Name))
                Log.Warning($"[Audio] No adaptive-music voice free on bus '{info.Name}' ({info.MusicVoices}); the music does not play. " +
                            "Raise the bus's MusicVoices in the bus layout.");
            return -1;
        }

        var slot = _musicSlots[best];
        slot.Generation++;
        if (slot.Generation == 0)
            slot.Generation = 1;
        slot.Active = true;
        slot.Owner = owner;
        slot.Bus = busIndex;
        slot.Clock = 0;
        slot.CloseAtFrame = 0;
        var voice = _graph.MusicVoices[best];
        for (var g = 0; g < MusicVoice.Groups; g++)
        {
            CloseMusicGroup(slot, voice, g); // the previous play's files; the audio thread drops its lanes at MusicPlay
            slot.GroupBusy[g] = false;
        }

        // Every group's channels for the layers that stream are created now, so playback never allocates them later.
        for (var g = 0; g < MusicVoice.Groups; g++)
        {
            for (var l = 0; l < MusicVoice.Lanes; l++)
            {
                if ((runtime.StreamedLayers & (1 << l)) != 0)
                    EnsureMusicChannel(slot, voice, g, l);
            }
        }

        if (!_musicInstances.Contains(owner))
            _musicInstances.Add(owner);
        return best;
    }

    /// <summary>Frees a music voice (with <paramref name="halt"/>, tells the audio thread to stop it, a few milliseconds' fade).</summary>
    internal void ReleaseMusicVoice(AdaptiveMusicInstance owner, int index, int generation, bool halt)
    {
        _musicInstances.Remove(owner);
        if ((uint)index >= (uint)_musicSlots.Length)
            return;
        var slot = _musicSlots[index];
        if (!slot.Active || slot.Generation != generation)
            return;
        if (halt)
            Enqueue(new AudioCommand { Type = AudioCommandType.MusicHalt, Index = index, Generation = generation });
        slot.Active = false;
        slot.Owner = null;
        slot.FreedSequence = ++_sequence;
        slot.CloseAtFrame = FrameIndex + 2; // after the halt fade: close its files on the streaming thread
    }

    internal int MusicGeneration(int index) => _musicSlots[index].Generation;

    /// <summary>The voice's music clock, as read at the start of this frame's <see cref="Process"/>.</summary>
    internal long MusicClock(int index) => _musicSlots[index].Clock;

    /// <summary>A lane group the audio thread is not using, marked busy until it reports it freed; −1 when none is free.</summary>
    internal int AllocateMusicGroup(int index)
    {
        var slot = _musicSlots[index];
        for (var g = 0; g < MusicVoice.Groups; g++)
        {
            if (!slot.GroupBusy[g])
            {
                slot.GroupBusy[g] = true;
                return g;
            }
        }

        return -1;
    }

    /// <summary>
    /// Asks the streaming thread for <paramref name="stems"/>' streamed bodies in lane group <paramref name="group"/>, from
    /// <paramref name="entry"/> (looping over <c>[0, length)</c> when <paramref name="loop"/>). Every channel of a group
    /// is requested together, so all of them are on the same stream generation, which the returned value names.
    /// </summary>
    internal int RequestMusicGroup(int index, int group, MusicSectionStems stems, int layers, long entry, bool loop, long length)
    {
        var slot = _musicSlots[index];
        var voice = _graph.MusicVoices[index];
        var any = false;
        for (var l = 0; l < MusicVoice.Lanes; l++)
        {
            var at = group * MusicVoice.Lanes + l;
            if (l < layers && stems.Bodies[l] is AudioStreamSource)
                EnsureMusicChannel(slot, voice, group, l);

            if (voice.StreamChannels[at] is not { } channel)
                continue;
            any = true;
            var source = l < layers ? stems.Bodies[l] as AudioStreamSource : null;
            channel.Request(source, entry, loop, 0, length);
        }

        if (!any)
            return slot.GroupGeneration[group];
        _streamer.Wake();
        return ++slot.GroupGeneration[group];
    }

    internal void EnqueueMusic(in AudioCommand command) => Enqueue(command);

    private void EnsureMusicChannel(MusicSlot slot, MusicVoice voice, int group, int lane)
    {
        var at = group * MusicVoice.Lanes + lane;
        if (voice.StreamChannels[at] is not null)
            return;
        var created = new AudioStreamChannel(-1 - voice.Index);
        while (created.RequestGeneration < slot.GroupGeneration[group])
            created.Request(null, 0, false, 0, 0); // join the group's generation
        voice.StreamChannels[at] = created; // published to the audio thread by the command that uses it
        _streamer.Register(created);
        created.Registered = true;
    }

    internal void CountLateMusicTransition() => _lateMusicTransitions++;

    /// <summary>Tests: holds the streaming thread (nothing decodes), to make streamed lanes miss their start.</summary>
    internal bool HoldStreaming
    {
        get => _streamer.Hold;
        set => _streamer.Hold = value;
    }

    // Read before the events are drained: the audio thread posts a block's events before publishing its clock, so every
    // event up to this clock is then in the ring.
    private void ReadMusicClocks()
    {
        var voices = _graph.MusicVoices;
        for (var i = 0; i < _musicSlots.Length; i++)
        {
            var slot = _musicSlots[i];
            if (!slot.Active)
            {
                if (slot.CloseAtFrame != 0 && FrameIndex >= slot.CloseAtFrame)
                {
                    slot.CloseAtFrame = 0;
                    for (var g = 0; g < MusicVoice.Groups; g++)
                        CloseMusicGroup(slot, voices[i], g);
                }

                continue;
            }

            if (Volatile.Read(ref voices[i].PublishedGeneration) == slot.Generation)
                slot.Clock = Volatile.Read(ref voices[i].PublishedClock);
        }
    }

    private void OnMusicEvent(in AudioEvent e)
    {
        if (!ReferenceEquals(e.Ref, _graph) || (uint)e.Index >= (uint)_musicSlots.Length)
            return;
        var slot = _musicSlots[e.Index];
        if (!slot.Active || slot.Generation != e.Generation)
            return;
        if (e.Type == AudioEventType.MusicGroupFreed && (uint)e.Group < MusicVoice.Groups)
        {
            CloseMusicGroup(slot, _graph.MusicVoices[e.Index], e.Group);
            slot.GroupBusy[e.Group] = false;
        }

        slot.Owner?.OnMusicEvent(e);
    }

    // The group's files close (each of its channels requests nothing, staying on one generation).
    private static void CloseMusicGroup(MusicSlot slot, MusicVoice voice, int group)
    {
        var any = false;
        for (var l = 0; l < MusicVoice.Lanes; l++)
        {
            if (voice.StreamChannels[group * MusicVoice.Lanes + l] is { } channel)
            {
                channel.Request(null, 0, false, 0, 0);
                any = true;
            }
        }

        if (any)
            slot.GroupGeneration[group]++;
    }

    private void UpdateMusic(float delta)
    {
        // Signals run game code, which may start or stop music: walk down so removals do not skip an instance.
        for (var i = _musicInstances.Count - 1; i >= 0; i--)
        {
            if (i < _musicInstances.Count)
                _musicInstances[i].Update(delta);
        }
    }

    // A layout swap replaces every music voice: the instances stop (no Finished), and the old voices' files close.
    private void ReleaseMusicForLayoutSwap()
    {
        for (var i = 0; i < _musicSlots.Length; i++)
        {
            var slot = _musicSlots[i];
            if (slot.Active)
                slot.Owner?.OnVoiceLost();
            slot.Active = false;
            slot.Owner = null;
        }

        _musicInstances.Clear();
        foreach (var voice in _graph.MusicVoices)
        {
            foreach (var channel in voice.StreamChannels)
            {
                if (channel is null)
                    continue;
                channel.Request(null, 0, false, 0, 0);
                _streamer.Unregister(channel);
            }
        }
    }

    private bool MusicStreamsReady(int frames)
    {
        var voices = _graph.MusicVoices;
        for (var i = 0; i < _musicSlots.Length; i++)
        {
            if (!_musicSlots[i].Active)
                continue;
            foreach (var channel in voices[i].StreamChannels)
            {
                if (channel is null)
                    continue;
                var buffered = channel.BufferedSamples(channel.RequestGeneration);
                if (buffered >= 0 && buffered < (long)frames * 2)
                    return false;
            }
        }

        return true;
    }

    private sealed class MusicSlot
    {
        public readonly bool[] GroupBusy = new bool[MusicVoice.Groups];
        public readonly int[] GroupGeneration = new int[MusicVoice.Groups];
        public bool Active;
        public int Generation;
        public AdaptiveMusicInstance? Owner;
        public int Bus;
        public long Clock;
        public long FreedSequence;
        public long CloseAtFrame;
    }
}

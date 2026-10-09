namespace MainframeEngine.Editor.Music;

/// <summary>
/// Live playback of a song in the editor: a <see cref="SongEngine"/> on its own render thread, pushing blocks into an
/// <see cref="AudioStreamGenerator"/> voice on the editor's <see cref="AudioServer"/> (Master bus), kept two blocks
/// ahead. UI-thread API: call <see cref="Update"/> every frame with the open document (rebuilds the snapshot when the
/// song changed and restarts the voice if it was stolen), then use the transport, previews and meters.
/// </summary>
public sealed class SongPlayer : IDisposable
{
    /// <summary>Blocks kept queued in the generator ahead of the device.</summary>
    public const int BlocksAhead = 2;

    private readonly AudioServer? _server;
    private readonly AudioStreamGenerator? _stream;
    private readonly Thread _thread;
    private readonly SongSnapshotBuilder _builder;
    private volatile bool _running = true;
    private AudioVoiceHandle _voice;
    private AudioStreamGeneratorPlayback? _playback;
    private SongDocument? _document;
    private int _version = -1;
    private int _pluginVersion = -1;
    private long _pushedEnd;    // song frame at the end of the last block pushed to the generator (render thread writes)
    private int _pushSequence;  // odd while a push is in flight: the generator's queue and _pushedEnd disagree
    private long _anchor;       // where the last Play/Seek started: the heard position never shows earlier than this
    private bool _anchorFromSeek;

    /// <param name="server">The editor's audio server; null renders nothing audible (no audio device).</param>
    /// <param name="instrumentFactory">See <see cref="SongSnapshotBuilder"/>.</param>
    /// <param name="clipLoader">See <see cref="SongSnapshotBuilder"/>.</param>
    public SongPlayer(AudioServer? server, Func<SongTrack, int, IInstrument?>? instrumentFactory = null,
        Func<string, AudioStream?>? clipLoader = null, Func<int, PluginRack?>? plugins = null)
    {
        _server = server;
        var rate = server?.SampleRate ?? 48000;
        Engine = new SongEngine(rate);
        Plugins = plugins?.Invoke(rate);
        _builder = new SongSnapshotBuilder(rate, instrumentFactory, clipLoader, Plugins);
        if (server is not null)
        {
            _stream = new AudioStreamGenerator { MixRate = rate, BufferSeconds = 0.1f, ResourceName = "Song" };
            EnsureVoice();
        }

        _thread = new Thread(RenderLoop) { IsBackground = true, Name = "Song render", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public SongEngine Engine { get; }

    /// <summary>The song's VST3 plugins (null: plugins are not played).</summary>
    public PluginRack? Plugins { get; }

    /// <summary>Blocks whose plugin round trip missed its deadline.</summary>
    public int PluginXruns => Plugins?.Xruns ?? 0;

    public bool IsPlaying => Engine.IsPlaying;

    /// <summary>
    /// The position being heard, in ticks. Stopped: the engine's position (steady — the silence the render thread keeps
    /// queueing is not part of the song). Playing: the end of the last block pushed minus what is still queued in the
    /// generator (the device buffer is not subtracted), read as a consistent pair (never while a block is being pushed,
    /// when the queue already holds it but the end does not yet) so the playhead cannot jump a block; never earlier than
    /// where playback started. Wraps with the loop.
    /// </summary>
    public double PositionTicks
    {
        get
        {
            if (Engine.Current is not { } song)
                return 0;
            if (!IsPlaying)
                return song.FramesToTicks(Engine.PositionFrames);

            var playback = Volatile.Read(ref _playback);
            long end, queued;
            int sequence;
            var spin = new SpinWait();
            while (true)
            {
                sequence = Volatile.Read(ref _pushSequence);
                end = Volatile.Read(ref _pushedEnd);
                queued = playback?.FramesQueued ?? 0;
                if ((sequence & 1) == 0 && sequence == Volatile.Read(ref _pushSequence))
                    break;
                spin.SpinOnce();
            }

            var frames = end - queued;
            if (song.LoopEnabled && frames < song.LoopStartFrame && end >= song.LoopStartFrame)
                frames += song.LoopEndFrame - song.LoopStartFrame; // just wrapped: still hearing the loop's end
            var anchor = Volatile.Read(ref _anchor);
            if (end >= anchor && frames < anchor)
                frames = anchor; // silence queued before Play (or before a seek) is still draining
            return song.FramesToTicks(Math.Max(0, frames));
        }
    }

    /// <summary>Generator underruns (the render thread fell behind).</summary>
    public int Underruns => Volatile.Read(ref _playback)?.Underruns ?? 0;

    /// <summary>The track status messages of the current snapshot ("missing plugin: …"), by track id.</summary>
    public string? GetTrackStatus(string trackId) => Engine.Current?.Find(trackId)?.Status;

    /// <summary>UI thread, every frame: follows <paramref name="document"/>'s edits (snapshot rebuilt when its version changed).</summary>
    public void Update(SongDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (Plugins is { } rack)
        {
            rack.SongName = document.Name;
            rack.Maintain(document.IsReadOnly ? null : document.SetPluginState);
        }

        if (!ReferenceEquals(document, _document) || document.Version != _version || (Plugins is not null && Plugins.Version != _pluginVersion))
        {
            _document = document;
            _version = document.Version;
            Engine.SetSnapshot(_builder.Build(document.Song));
            _pluginVersion = Plugins?.Version ?? 0; // after the build: loads during it are in this snapshot
        }

        if (_server is not null && !_server.IsPlaying(_voice))
            EnsureVoice();
    }

    public void Play()
    {
        if (!_anchorFromSeek)
            Volatile.Write(ref _anchor, Engine.PositionFrames);
        _anchorFromSeek = false;
        Engine.Play();
    }

    public void Stop() => Engine.Stop();

    public void TogglePlay()
    {
        if (IsPlaying)
            Stop();
        else
            Play();
    }

    /// <summary>Moves to <paramref name="tick"/> (all notes off). Needs a snapshot (<see cref="Update"/>).</summary>
    public void Seek(double tick)
    {
        var rate = Engine.Current?.FramesPerTick ?? (_document is { } d ? Engine.SampleRate * 60.0 / (d.Song.Tempo * d.Song.Ppq) : 0);
        var frame = (long)Math.Round(Math.Max(0, tick) * rate);
        Volatile.Write(ref _anchor, frame);
        _anchorFromSeek = !IsPlaying; // a seek while stopped is where the next Play starts
        Engine.SeekFrames(frame);
    }

    /// <summary>Plays <paramref name="pitch"/> on a track's instrument until <see cref="ReleaseNote"/> (piano-roll keys, note clicks).</summary>
    public void PreviewNote(SongTrack track, int pitch, int velocity = 100)
    {
        ArgumentNullException.ThrowIfNull(track);
        _builder.EnsurePitch(track, pitch);
        if (Engine.Current?.Find(track.Id) is { } snapshot)
            Engine.PreviewNote(snapshot, pitch, Math.Clamp(velocity, 1, 127));
    }

    public void ReleaseNote(SongTrack track, int pitch)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (Engine.Current?.Find(track.Id) is { } snapshot)
            Engine.PreviewNote(snapshot, pitch, 0);
    }

    /// <summary>Peak (linear) of a track since the last call; the UI meters call it once per frame.</summary>
    public float TakeTrackPeak(string trackId) => Engine.TakeTrackPeak(trackId);

    public float TakeMasterPeak() => Engine.TakeMasterPeak();

    /// <summary>Reads every plugin's state into the song (undoable "Plugin State" edits): before saving and rendering.</summary>
    public void CapturePluginStates(SongDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (Plugins is { } rack && !document.IsReadOnly)
            rack.CaptureStates(document.SetPluginState);
    }

    public void Dispose()
    {
        _running = false;
        _thread.Join();
        _builder.ReleasePlugins();
        Plugins?.Dispose();
        if (_server is not null && _server.IsPlaying(_voice))
            _server.Stop(_voice);
    }

    private void EnsureVoice()
    {
        _voice = _server!.PlayOneShot(_stream!, bus: AudioBusLayout.MasterBus, priority: int.MaxValue, processMode: ProcessMode.Always);
        Volatile.Write(ref _playback, _server.GetGeneratorPlayback(_voice));
    }

    private void RenderLoop()
    {
        var block = new float[SongEngine.BlockFrames * 2];
        while (_running)
        {
            var playback = Volatile.Read(ref _playback);
            if (playback is null)
            {
                Thread.Sleep(5);
                continue;
            }

            if (playback.FramesQueued >= BlocksAhead * SongEngine.BlockFrames || playback.FramesAvailable < SongEngine.BlockFrames)
            {
                Thread.Sleep(1);
                continue;
            }

            try
            {
                Engine.Process(block);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Log.Error($"[Music] Song engine block failed: {e.Message}");
                Array.Clear(block);
            }

            Volatile.Write(ref _pushSequence, _pushSequence + 1); // odd: PositionTicks waits out the push
            playback.PushFrames(block);
            Volatile.Write(ref _pushedEnd, Engine.PositionFrames); // after the push: the queue now ends here
            Volatile.Write(ref _pushSequence, _pushSequence + 1);
        }
    }
}

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using MainframeEngine.Video.Media.Preview;
using MainframeEngine.Video.Models;

namespace MainframeEngine.Editor.Video;

/// <summary>
/// The gapless preview of the edited result (docs/design/future/video-editor.md, "Preview and playback"). Each kept range
/// gets its own ffmpeg video decode of raw RGBA frames, started <see cref="VideoPlaybackPlan.PrefetchSeconds"/> before
/// the range before it ends, so a cut switches frames without a seek. One task decodes the audio range after range
/// into a single <see cref="AudioStreamGenerator"/> voice, and the frames that voice has played are the clock: the
/// video follows the audio, never the other way round. Without an audio server a stopwatch is the clock.
/// </summary>
/// <remarks>
/// <para><b>Threads.</b> Everything visible happens in <see cref="Update"/> on the main thread: the texture, the voice
/// (<see cref="AudioServer"/> is main-thread only) and every decoder this class kills. A video reader thread only fills
/// its stream's buffers and queues them; the audio task only pushes into its own voice's ring (the ring's one producer).</para>
/// <para><b>Buffers.</b> Each stream slot owns <see cref="FramesPerStream"/> frame buffers, allocated the first time the
/// slot is used and reused for every stream after it. A buffer is in exactly one place: the slot's free pool (counted by
/// its semaphore, which blocks the reader and so back-pressures ffmpeg when the pool is empty), the reader's hand, the
/// frame queue, or the main thread's <c>Next</c> (dequeued, not yet due). A slot is reused only after its reader has
/// finished, so a stopped reader never writes into a buffer a new stream is using.</para>
/// </remarks>
public sealed class VideoPreview : IDisposable
{
    /// <summary>Frame buffers per video stream: a fifth of a second at 30 fps.</summary>
    private const int FramesPerStream = 6;

    /// <summary>Stream slots: the current range, the prefetched ones and those whose readers are still stopping.</summary>
    private const int StreamSlots = 4;

    /// <summary>
    /// Video decodes running at once. Ranges shorter than the prefetch lead need more than two to keep a full second
    /// ahead; past this cap the next one starts as soon as an earlier range ends.
    /// </summary>
    private const int MaxLiveStreams = 3;

    /// <summary>Audio is pushed in chunks of this many stereo frames.</summary>
    private const int AudioChunkFrames = 4096;

    /// <summary>One interleaved stereo f32le frame.</summary>
    private const int AudioFrameBytes = 2 * sizeof(float);

    /// <summary>The voice's ring: a second of audio queued ahead, enough to cover the next range's decoder starting.</summary>
    private const float AudioBufferSeconds = 1.0f;

    /// <summary>How long a decode that produced nothing is watched for a failing exit before it counts as empty.</summary>
    private const int EmptyDecodeWaitMilliseconds = 500;

    /// <summary>How long <see cref="Dispose"/> waits for stopped readers before abandoning them.</summary>
    private static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(2);

    private readonly IVideoDecoderFactory _decoders;
    private readonly Func<AudioServer?> _audio;
    private readonly string _sourcePath;
    private readonly double _frameRate;
    private readonly int _frameBytes;
    private readonly Texture2D _texture;
    private readonly VideoStream[] _streams = new VideoStream[StreamSlots];
    private readonly List<AudioRun> _stoppedAudio = new(4);
    private readonly Stopwatch _stopwatch = new();

    private VideoPlaybackPlan _plan = VideoPlaybackPlan.Build([], 0);
    private IReadOnlyList<Segment>? _segments;
    private int _segmentsVersion;
    private int _liveDecoders;
    private bool _disposed;

    // The voice of the current play (main thread). _playback is null when the stopwatch is the clock.
    private AudioStreamGenerator? _audioStream;
    private AudioServer? _server;
    private AudioVoiceHandle _voice;
    private AudioStreamGeneratorPlayback? _playback;
    private AudioRun? _audioRun;
    private long _consumedAtStart;
    private int _sampleRate;

    // Scrubbing while paused: one single-frame decode at a time, the newest waiting request after it (NaN: none).
    private IVideoDecoder? _seekDecoder;
    private CancellationTokenSource? _seekCancel;
    private Task<bool>? _seekTask;
    private byte[]? _seekFrame;
    private double _pendingSeek = double.NaN;

    public VideoPreview(IVideoDecoderFactory decoders, Func<AudioServer?> audio, string sourcePath, int sourceWidth,
        int sourceHeight, double sourceFrameRate, int number)
    {
        ArgumentNullException.ThrowIfNull(decoders);
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);
        _decoders = decoders;
        _audio = audio;
        _sourcePath = sourcePath;
        (Width, Height) = PreviewDecoding.PreviewSize(sourceWidth, sourceHeight);
        _frameRate = PreviewDecoding.PreviewFrameRate(sourceFrameRate);
        _frameBytes = Width * Height * 4;

        var black = new byte[_frameBytes];
        for (var i = 3; i < black.Length; i += 4)
            black[i] = 255;
        _texture = Texture2D.FromPixels(Width, Height, black);
        Viewport = new SubViewport
        {
            Name = $"VideoPreview{number}",
            Disable3D = true,
            UpdateMode = SubViewportUpdateMode.Disabled,
            Width = Width,
            Height = Height,
            ClearColor = System.Drawing.Color.FromArgb(255, 0, 0, 0),
        };
        Viewport.AddChild(new Sprite2D { Texture = _texture, Centered = false });
        for (var i = 0; i < _streams.Length; i++)
            _streams[i] = new VideoStream();
    }

    /// <summary>The texture's only parent; the owner keeps it in the tree and publishes it to the UI.</summary>
    public SubViewport Viewport { get; }

    /// <summary>Preview frame width: at most <see cref="PreviewDecoding.MaxWidth"/>, the source's aspect.</summary>
    public int Width { get; }

    public int Height { get; }

    public bool IsPlaying { get; private set; }

    /// <summary>The playhead in source seconds: where playback is, or the last scrub target while paused.</summary>
    public double Position { get; private set; }

    /// <summary>Decoders started and not yet disposed (video, audio and scrub), for the tests' leak checks.</summary>
    internal int LiveDecoders => Volatile.Read(ref _liveDecoders);

    /// <summary>
    /// Plays the kept segments from <paramref name="from"/> (source seconds), restarting every decoder. Nothing is played
    /// when no kept footage follows <paramref name="from"/>.
    /// </summary>
    public void Play(IReadOnlyList<Segment> segments, double from, int segmentsVersion = 0)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ObjectDisposedException.ThrowIf(_disposed, this);
        StopAll();
        _segments = segments;
        _segmentsVersion = segmentsVersion;
        from = double.IsFinite(from) ? Math.Max(0, from) : 0;
        _plan = VideoPlaybackPlan.Build(segments, from);
        if (_plan.Ranges.Count == 0)
        {
            Position = from;
            return;
        }

        Position = _plan.SourceTimeAt(0);
        IsPlaying = true;
        StartAudio();
        EnsureStream(0);
    }

    /// <summary>Stops playback where it is: every decoder is killed and the voice stops.</summary>
    public void Pause()
    {
        if (!_disposed)
            StopAll();
    }

    /// <summary>
    /// Moves the playhead. Playing, playback restarts there. Paused, the frame at <paramref name="time"/> is decoded;
    /// while one decode runs only the newest request waits behind it.
    /// </summary>
    public void Seek(double time)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        time = double.IsFinite(time) ? Math.Max(0, time) : 0;
        if (IsPlaying && _segments is not null)
        {
            Play(_segments, time, _segmentsVersion);
            return;
        }

        Position = time;
        if (_seekTask is not null)
            _pendingSeek = time;
        else
            StartSeek(time);
    }

    /// <summary>
    /// Main thread, every frame: advances the clock and the playhead, starts and stops decoders, shows the frame due and
    /// restarts from the playhead when <paramref name="segmentsVersion"/> changed while playing. Allocation-free unless a
    /// decoder starts.
    /// </summary>
    public void Update(IReadOnlyList<Segment> segments, int segmentsVersion)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (_disposed)
            return;
        RecycleStopped();
        PollSeek();
        if (!IsPlaying)
            return;

        if (FindFailure() is { } failure)
        {
            Log.Warning($"[Video] Preview decode failed: {failure}");
            StopAll();
            return;
        }

        if (_server is not null && !_server.IsPlaying(_voice))
        {
            Log.Warning("[Video] Preview paused: its audio voice was stopped.");
            StopAll();
            return;
        }

        var clock = Clock();
        if (_plan.IsFinished(clock) || AudioEnded())
        {
            Position = _plan.Ranges[^1].End;
            StopAll();
            return;
        }

        Position = _plan.SourceTimeAt(clock);
        if (segmentsVersion != _segmentsVersion)
        {
            Play(segments, Position, segmentsVersion);
            return;
        }

        Advance(clock);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        StopAll();
        var tasks = new List<Task>();
        foreach (var stream in _streams)
            if (stream.Reader is { } reader)
                tasks.Add(reader);
        foreach (var run in _stoppedAudio)
            if (run.Task is { } task)
                tasks.Add(task);
        if (_seekTask is not null)
            tasks.Add(_seekTask);

        // The processes are dead, so the readers end at once; one that does not is abandoned with its own buffers.
        bool joined;
        try
        {
            joined = Task.WaitAll([.. tasks], JoinTimeout);
        }
        catch (AggregateException e)
        {
            Log.Warning($"[Video] A preview reader failed while stopping: {e.InnerException?.Message}");
            joined = true;
        }

        if (joined)
        {
            foreach (var stream in _streams)
                stream.Dispose();
            foreach (var run in _stoppedAudio)
                run.Dispose();
            _stoppedAudio.Clear();
            _seekCancel?.Dispose();
            _seekCancel = null;
        }
        else
        {
            Log.Warning($"[Video] A preview reader did not stop within {JoinTimeout.TotalSeconds:0} s; abandoning it.");
        }

        if (!Viewport.IsFreed)
            Viewport.Free();
    }

    // ── Clock ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Output seconds played: the voice's consumed frames, or the stopwatch without a voice.</summary>
    private double Clock() =>
        _playback is { } playback
            ? (playback.FramesConsumed - _consumedAtStart) / (double)_sampleRate
            : _stopwatch.Elapsed.TotalSeconds;

    /// <summary>
    /// True when every range's audio was pushed and the ring has drained. ffmpeg can produce a few frames less than a
    /// range's nominal length, so the audio clock may never reach <see cref="VideoPlaybackPlan.OutputDuration"/>.
    /// </summary>
    private bool AudioEnded() =>
        _audioRun is { Task.IsCompletedSuccessfully: true, Failure: null } && _playback is { FramesQueued: 0 };

    private void StartAudio()
    {
        _stopwatch.Restart();
        if (_audio() is not { } server)
            return;

        _sampleRate = server.SampleRate;
        if (_audioStream is null || _audioStream.MixRate != _sampleRate)
            _audioStream = new AudioStreamGenerator { MixRate = _sampleRate, BufferSeconds = AudioBufferSeconds, ResourceName = "VideoPreview" };
        var voice = server.PlayOneShot(_audioStream, AudioBusLayout.MasterBus, priority: int.MaxValue, processMode: ProcessMode.Always);
        if (server.GetGeneratorPlayback(voice) is not { } playback)
        {
            Log.Warning("[Video] Preview audio unavailable (no voice); playing without sound.");
            if (voice.IsValid)
                server.Stop(voice);
            return;
        }

        _server = server;
        _voice = voice;
        _playback = playback;
        _consumedAtStart = playback.FramesConsumed;
        var run = new AudioRun(this, _plan.Ranges, _sampleRate, playback);
        _audioRun = run;
        run.Task = Task.Factory.StartNew(static state => ((AudioRun)state!).Run(), run, CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    // ── Stopping ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Pause, Play and Dispose: cancel the audio task, every reader and the scrub; kill every decoder; stop the voice.</summary>
    private void StopAll()
    {
        IsPlaying = false;
        _stopwatch.Stop();
        if (_audioRun is { } run)
        {
            run.Stop();
            _stoppedAudio.Add(run);
            _audioRun = null;
        }

        if (_server is not null)
        {
            _server.Stop(_voice);
            // The stopped voice's ring is never played again (each play gets its own); clearing it drops the queued audio.
            _playback?.ClearBuffer();
        }

        _server = null;
        _voice = default;
        _playback = null;
        foreach (var stream in _streams)
            if (stream.State == StreamState.Live)
                Retire(stream);

        _pendingSeek = double.NaN;
        if (_seekDecoder is not null)
        {
            _seekCancel!.Cancel();
            Kill(_seekDecoder);
            _seekDecoder = null; // its reader finishes on its own; PollSeek then drops the result
        }
    }

    /// <summary>Returns stream slots whose readers have finished to the idle set, and forgets finished audio runs.</summary>
    private void RecycleStopped()
    {
        foreach (var stream in _streams)
            if (stream.State == StreamState.Stopping && stream.Reader!.IsCompleted)
                stream.Recycle();

        for (var i = _stoppedAudio.Count - 1; i >= 0; i--)
        {
            if (_stoppedAudio[i].Task?.IsCompleted != false)
            {
                _stoppedAudio[i].Dispose();
                _stoppedAudio.RemoveAt(i);
            }
        }
    }

    // ── Video ───────────────────────────────────────────────────────────────────────────────────────────────────

    private void Advance(double clock)
    {
        var current = _plan.RangeAt(clock);
        // The clock has passed these ranges' ends.
        foreach (var stream in _streams)
            if (stream.State == StreamState.Live && stream.Range < current)
                Retire(stream);

        // The current range, then each next one within the prefetch lead. Chaining lets ranges shorter than the lead
        // start the one after them as soon as they are prefetched themselves.
        var range = current;
        while (EnsureStream(range) && _plan.ShouldPrefetch(range, clock))
            range++;

        if (Find(current) is { } shown)
            ShowDueFrame(shown, clock);
    }

    /// <summary>Starts range <paramref name="range"/>'s video decode unless it runs; false when no slot is free yet.</summary>
    private bool EnsureStream(int range)
    {
        if (Find(range) is not null)
            return true;
        var live = 0;
        VideoStream? idle = null;
        foreach (var stream in _streams)
        {
            if (stream.State == StreamState.Live)
                live++;
            else if (stream.State == StreamState.Stopping && stream.Reader!.IsCompleted)
                stream.Recycle();
            if (stream.State == StreamState.Idle)
                idle ??= stream;
        }

        if (live >= MaxLiveStreams || idle is null)
            return false;

        var r = _plan.Ranges[range];
        var decoder = StartDecoder(PreviewDecoding.VideoArguments(_sourcePath, r.Start, r.End, Width, Height, _frameRate));
        idle.Start(range, decoder, _plan.OutputStartOf(range), _frameRate, _frameBytes);
        return true;
    }

    private VideoStream? Find(int range)
    {
        foreach (var stream in _streams)
            if (stream.State == StreamState.Live && stream.Range == range)
                return stream;
        return null;
    }

    /// <summary>Shows the latest queued frame due by <paramref name="clock"/>; earlier ones go back to the pool unseen.</summary>
    private void ShowDueFrame(VideoStream stream, double clock)
    {
        byte[]? due = null;
        while (true)
        {
            if (stream.Next is null)
            {
                if (!stream.Frames.TryDequeue(out var frame))
                    break;
                (stream.NextTime, stream.Next) = frame;
            }

            if (stream.NextTime > clock)
                break;
            if (due is not null)
                stream.Return(due);
            due = stream.Next;
            stream.Next = null;
        }

        if (due is null)
            return;
        _texture.SetPixels(due);
        stream.Return(due);
    }

    private void Retire(VideoStream stream)
    {
        Kill(stream.Decoder!);
        stream.Stop();
        if (stream.Reader!.IsCompleted)
            stream.Recycle();
    }

    /// <summary>A failure to report: a live video decode, a reader or the audio task.</summary>
    private string? FindFailure()
    {
        foreach (var stream in _streams)
        {
            if (stream.State != StreamState.Live)
                continue;
            if (stream.Decoder!.Failed)
                return stream.Decoder.ToString();
            if (stream.Failure is { } failure)
                return failure;
        }

        return _audioRun?.Failure;
    }

    // ── Scrubbing ───────────────────────────────────────────────────────────────────────────────────────────────

    private void StartSeek(double time)
    {
        _seekFrame ??= new byte[_frameBytes];
        var decoder = StartDecoder(PreviewDecoding.FrameArguments(_sourcePath, time, Width, Height));
        _seekDecoder = decoder;
        var frame = _seekFrame;
        var cancel = new CancellationTokenSource();
        _seekCancel = cancel;
        _seekTask = Task.Run(() => ReadSeekFrame(decoder, frame, cancel.Token));
    }

    /// <summary>Main thread: shows a finished scrub's frame (unless it was cancelled) and starts the newest waiting one.</summary>
    private void PollSeek()
    {
        if (_seekTask is not { IsCompleted: true } task)
            return;
        var decoded = task.IsCompletedSuccessfully && task.Result;
        _seekTask = null;
        _seekCancel!.Dispose();
        _seekCancel = null;
        if (_seekDecoder is { } decoder)
        {
            _seekDecoder = null;
            if (decoded)
                _texture.SetPixels(_seekFrame!);
            else if (decoder.Failed)
                Log.Warning($"[Video] Preview decode failed: {decoder}");
            Kill(decoder);
        }

        if (!double.IsNaN(_pendingSeek))
        {
            var time = _pendingSeek;
            _pendingSeek = double.NaN;
            StartSeek(time);
        }
    }

    /// <summary>Reader thread: one whole frame into <paramref name="frame"/>, or false (no frame, killed or failed).</summary>
    private static bool ReadSeekFrame(IVideoDecoder decoder, byte[] frame, CancellationToken cancellation)
    {
        try
        {
            if (decoder.Output.ReadAtLeast(frame, frame.Length, throwOnEndOfStream: false) == frame.Length)
                return true;
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            return false; // killed by a newer Play or Pause
        }

        WaitForFailure(decoder, cancellation); // so PollSeek can report a failing exit
        return false;
    }

    // ── Decoders ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Any thread: starts a decode and counts it live. A factory that throws gives a failed decoder.</summary>
    private IVideoDecoder StartDecoder(IReadOnlyList<string> arguments)
    {
        IVideoDecoder decoder;
        try
        {
            decoder = _decoders.Start(arguments);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            decoder = new FailedVideoDecoder($"could not start the decoder: {e.Message}");
        }

        Interlocked.Increment(ref _liveDecoders);
        return decoder;
    }

    /// <summary>Kills a decoder started by <see cref="StartDecoder"/>. Callers make sure each is killed once.</summary>
    private void Kill(IVideoDecoder decoder)
    {
        decoder.Dispose();
        Interlocked.Decrement(ref _liveDecoders);
    }

    /// <summary>
    /// A decode that ended without output may still be exiting with an error; watch it briefly so a failure (a missing
    /// stream, an unreadable file) is reported instead of passing as empty.
    /// </summary>
    private static bool WaitForFailure(IVideoDecoder decoder, CancellationToken cancellation)
    {
        for (var waited = 0; waited < EmptyDecodeWaitMilliseconds && !cancellation.IsCancellationRequested; waited += 10)
        {
            if (decoder.Failed)
                return true;
            Thread.Sleep(10);
        }

        return decoder.Failed;
    }

    // ── Types ───────────────────────────────────────────────────────────────────────────────────────────────────

    private enum StreamState : byte
    {
        Idle,
        Live,
        Stopping,
    }

    /// <summary>
    /// A reusable slot for one range's video decode: its buffers, their free pool and the frame queue. Main-thread
    /// fields: <see cref="State"/>, <see cref="Range"/>, <see cref="Decoder"/>, <see cref="Reader"/>, <see cref="Next"/>.
    /// The reader shares only the two queues, the semaphore and <see cref="Failure"/>.
    /// </summary>
    private sealed class VideoStream : IDisposable
    {
        private readonly ConcurrentQueue<byte[]> _pool = new();
        private readonly SemaphoreSlim _free = new(0, FramesPerStream);
        private CancellationTokenSource? _cancel;
        private bool _allocated;
        private volatile string? _failure;

        public ConcurrentQueue<(double OutputTime, byte[] Frame)> Frames { get; } = new();

        public StreamState State { get; private set; }

        public int Range { get; private set; } = -1;

        public IVideoDecoder? Decoder { get; private set; }

        public Task? Reader { get; private set; }

        /// <summary>A frame the main thread dequeued that is not due yet.</summary>
        public byte[]? Next { get; set; }

        public double NextTime { get; set; }

        public string? Failure => _failure;

        public void Start(int range, IVideoDecoder decoder, double outputStart, double frameRate, int frameBytes)
        {
            Debug.Assert(State == StreamState.Idle);
            if (!_allocated)
            {
                // Once per slot, for the preview's lifetime: the frame pool the semaphore counts.
                for (var i = 0; i < FramesPerStream; i++)
                    _pool.Enqueue(new byte[frameBytes]);
                _free.Release(FramesPerStream);
                _allocated = true;
            }

            Debug.Assert(_free.CurrentCount == FramesPerStream && Frames.IsEmpty && Next is null);
            State = StreamState.Live;
            Range = range;
            Decoder = decoder;
            _failure = null;
            var cancel = new CancellationTokenSource();
            _cancel = cancel;
            Reader = Task.Factory.StartNew(() => Read(decoder, outputStart, frameRate, cancel.Token), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        /// <summary>Main thread: the decoder was killed; the reader stops at its next frame. The slot waits for it.</summary>
        public void Stop()
        {
            Debug.Assert(State == StreamState.Live);
            _cancel!.Cancel();
            State = StreamState.Stopping;
            Decoder = null;
            if (Next is { } next)
            {
                Next = null;
                Return(next);
            }
        }

        /// <summary>Main thread, once the reader has finished: every buffer back in the pool, the slot idle.</summary>
        public void Recycle()
        {
            Debug.Assert(State == StreamState.Stopping && Reader!.IsCompleted);
            while (Frames.TryDequeue(out var frame))
                Return(frame.Frame);
            _cancel!.Dispose();
            _cancel = null;
            Reader = null;
            Range = -1;
            State = StreamState.Idle;
            Debug.Assert(_free.CurrentCount == FramesPerStream);
        }

        /// <summary>A buffer back to the free pool (main thread, or the reader with the one in its hand).</summary>
        public void Return(byte[] buffer)
        {
            _pool.Enqueue(buffer);
            _free.Release();
        }

        public void Dispose()
        {
            _cancel?.Dispose();
            _free.Dispose();
        }

        /// <summary>Reader thread: whole frames into pool buffers, queued with their output time, until the end or a stop.</summary>
        private void Read(IVideoDecoder decoder, double outputStart, double frameRate, CancellationToken cancellation)
        {
            byte[]? held = null;
            try
            {
                var output = decoder.Output;
                for (var n = 0; ; n++)
                {
                    _free.Wait(cancellation); // an empty pool blocks here, and ffmpeg blocks on its full pipe
                    if (!_pool.TryDequeue(out held))
                        throw new InvalidOperationException("The preview frame pool lost count of its buffers.");
                    if (output.ReadAtLeast(held, held.Length, throwOnEndOfStream: false) < held.Length)
                        break; // the end of the range (a partial last frame is dropped)
                    Frames.Enqueue((outputStart + n / frameRate, held));
                    held = null;
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                // Stopped: past its range, paused or replaced.
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                if (!cancellation.IsCancellationRequested)
                    _failure = e.Message; // a killed process ends the pipe like this; only an unrequested end is a failure
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                _failure = e.Message;
            }
            finally
            {
                if (held is not null)
                    Return(held);
            }
        }
    }

    /// <summary>
    /// The audio of one play: decodes range after range into the voice's ring, the ring's only producer. The main thread
    /// stops it with <see cref="Stop"/>; whichever side takes the current decoder out of <c>_decoder</c> kills it, so it
    /// dies exactly once even when the task starts it just as the main thread stops.
    /// </summary>
    private sealed class AudioRun(VideoPreview owner, IReadOnlyList<PlayRange> ranges, int sampleRate, AudioStreamGeneratorPlayback playback)
        : IDisposable
    {
        private readonly CancellationTokenSource _cancel = new();
        private IVideoDecoder? _decoder;
        private volatile string? _failure;

        public Task? Task { get; set; }

        public string? Failure => _failure;

        /// <summary>Main thread.</summary>
        public void Stop()
        {
            _cancel.Cancel();
            KillDecoder();
        }

        /// <summary>Main thread, once <see cref="Task"/> has finished.</summary>
        public void Dispose() => _cancel.Dispose();

        public void Run()
        {
            var cancellation = _cancel.Token;
            var chunk = new byte[AudioChunkFrames * AudioFrameBytes];
            try
            {
                foreach (var range in ranges)
                {
                    var decoder = owner.StartDecoder(PreviewDecoding.AudioArguments(owner._sourcePath, range.Start, range.End, sampleRate));
                    Interlocked.Exchange(ref _decoder, decoder); // a full fence: a Stop before this is seen below
                    if (cancellation.IsCancellationRequested)
                        return;
                    if (!Decode(decoder.Output, chunk, cancellation, out var empty))
                        return;
                    if (decoder.Failed || (empty && WaitForFailure(decoder, cancellation)))
                    {
                        _failure = decoder.ToString();
                        return;
                    }

                    KillDecoder();
                }
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                if (!cancellation.IsCancellationRequested)
                    _failure = e.Message;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                _failure = e.Message;
            }
            finally
            {
                KillDecoder();
            }
        }

        private void KillDecoder()
        {
            if (Interlocked.Exchange(ref _decoder, null) is { } decoder)
                owner.Kill(decoder);
        }

        /// <summary>Pushes one range's samples in whole chunks, then its last whole frames. False when stopped.</summary>
        private bool Decode(Stream output, byte[] chunk, CancellationToken cancellation, out bool empty)
        {
            empty = true;
            var have = 0;
            while (true)
            {
                var read = output.Read(chunk, have, chunk.Length - have);
                if (read == 0)
                    break;
                empty = false;
                have += read;
                if (have < chunk.Length)
                    continue;
                if (!Push(chunk, have, cancellation))
                    return false;
                have = 0;
            }

            have -= have % AudioFrameBytes; // only whole stereo frames
            return have == 0 || Push(chunk, have, cancellation);
        }

        private bool Push(byte[] chunk, int bytes, CancellationToken cancellation)
        {
            var frames = bytes / AudioFrameBytes;
            while (playback.FramesAvailable < frames)
            {
                if (cancellation.IsCancellationRequested)
                    return false;
                Thread.Sleep(2);
            }

            if (cancellation.IsCancellationRequested)
                return false;
            playback.PushFrames(MemoryMarshal.Cast<byte, float>(chunk.AsSpan(0, bytes)));
            return true;
        }
    }
}

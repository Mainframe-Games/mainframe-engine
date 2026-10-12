using System.Diagnostics;
using MainframeEngine.Audio;

namespace MainframeEngine;

/// <summary>Game name, current scene and engine version for the sidecar.</summary>
internal sealed record CaptureInfoSource(string Game, Func<string> Scene, string EngineVersion);

/// <summary>
/// Records gameplay to a video and a <c>.mfcap.json</c> sidecar (ADR 0190) that Cutback reads to cut idle play,
/// loading and pauses. Live: real time on a <see cref="Stopwatch"/> started by <see cref="Start"/> (never game time),
/// frames resampled to a constant rate, audio tapped from the master mix. Offline (<c>--record</c> with
/// <c>--fixed-fps</c>): every update is one output frame and exactly <c>sampleRate/fps</c> samples rendered from the
/// manual null device, so a slow 4K render still plays back smoothly and in sync.
/// </summary>
/// <remarks>
/// <para>Main thread only. <see cref="Stop"/> returns at once and finalising (encoder tail, mux, sidecar) runs in the
/// background; <see cref="Stopped"/> is raised on the main thread by the next update once it is done. Until then a new
/// <see cref="Start"/> throws, because the previous recording still owns its files.</para>
/// <para>Nothing the recorder does may take the game down: a failing encoder, a full disk or a resized window ends the
/// recording with a readable <see cref="RecordingResult.Error"/> (naming the kept <c>.partial</c> folder), never an
/// exception from the renderer, the audio path or the update. Only <see cref="Start"/> throws, so a <c>--record</c>
/// run that cannot record can fail loudly.</para>
/// </remarks>
public sealed class GameRecorder : IFrameStreamSink
{
    private const int DefaultSampleRate = 48_000;
    private const int Channels = 2; // the WAV and the mux are stereo; the master mix is too
    private const int TapCapacitySamples = 1 << 18; // ~2.7 s of 48 kHz stereo: a long loading hitch before the ring overflows
    private const string NoFrameError = "Recording stopped before any frame was captured.";

    // After EndCapture the audio thread may still be inside one block that read the tap before it was detached.
    private static readonly TimeSpan s_tailWait = TimeSpan.FromMilliseconds(20);

    private readonly Func<RecordingFormat, IRecordingBackend> _backendFactory;
    private readonly IFrameStreamSource? _frames;
    private readonly AudioServer? _audio;
    private readonly bool _offline;
    private readonly CaptureInfoSource _info;
    private readonly Func<double>? _clockOverride;
    private readonly InputActivity _input = new();
    private readonly float[] _drain = new float[16_384]; // a multiple of Channels: drains end on whole frames

    private IRecordingBackend? _backend;
    private FrameScheduler? _scheduler;
    private CaptureTimeline? _timeline;
    private AudioCaptureTap? _tap;
    private RecordingFormat? _format;
    private Stopwatch? _stopwatch;
    private long _startTimestamp;
    private long _offlineFrame;
    private long _audioFramesWritten;
    private long _overflowPadded;
    private long _dropped;
    private bool _audioStarted;
    private float[] _offlineAudio = [];
    private string? _endReason;
    private Task<RecordingResult>? _finishing;

    /// <param name="backendFactory">Creates the backend for one recording (the host's resolves ffmpeg here, so a missing ffmpeg fails <see cref="Start"/>).</param>
    /// <param name="frames">The renderer's frame stream; null when nothing renders (recording then cannot start).</param>
    /// <param name="audio">The audio server: tapped live, rendered offline. Null records silence of the right length.</param>
    /// <param name="offline">One output frame per update (<c>--fixed-fps</c>) instead of real time.</param>
    /// <param name="info">What the sidecar says about the game.</param>
    /// <param name="liveClock">Seconds since <see cref="Start"/> for live recordings (tests); default a <see cref="Stopwatch"/>.</param>
    internal GameRecorder(Func<RecordingFormat, IRecordingBackend> backendFactory, IFrameStreamSource? frames, AudioServer? audio,
        bool offline, CaptureInfoSource info, Func<double>? liveClock = null)
    {
        ArgumentNullException.ThrowIfNull(backendFactory);
        ArgumentNullException.ThrowIfNull(info);
        _backendFactory = backendFactory;
        _frames = frames;
        _audio = audio;
        _offline = offline;
        _info = info;
        _clockOverride = liveClock;
    }

    /// <summary>A recording is running (false again as soon as <see cref="Stop"/> is called, while it finalises).</summary>
    public bool IsRecording => _backend is not null && _finishing is null;

    /// <summary>A stopped recording is still being encoded, muxed and written.</summary>
    public bool IsFinalising => _finishing is { IsCompleted: false };

    /// <summary>Time recorded so far (zero when not recording): wall-clock live, frames / fps offline.</summary>
    public TimeSpan Elapsed => TimeSpan.FromSeconds(IsRecording ? Now() : 0);

    /// <summary>Raised by <see cref="Start"/> once frames and audio are flowing (hide HUDs and overlays here).</summary>
    public event Action? Started;

    /// <summary>Raised on the main thread once a recording has been finalised, with where it went or why it failed.</summary>
    public event Action<RecordingResult>? Stopped;

    /// <summary>Starts recording to <paramref name="options"/>' path from the next rendered frame.</summary>
    /// <exception cref="InvalidOperationException">Already recording, still finalising, or frames cannot be captured.</exception>
    /// <exception cref="FfmpegNotFoundException">No ffmpeg (from the host's backend factory).</exception>
    /// <exception cref="IOException">The encoder could not be started or the output folder could not be created.</exception>
    public void Start(RecordingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Path);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Fps, 1);
        if (IsRecording)
            throw new InvalidOperationException("Already recording.");
        if (_finishing is not null)
            throw new InvalidOperationException("The previous recording is still being finalised.");
        if (_frames is null || !_frames.CanStream)
            throw new InvalidOperationException("Recording needs frame capture (start the game with --record or --frame-capture).");

        var (width, height) = _frames.StreamSize;
        var format = new RecordingFormat(Path.GetFullPath(options.Path), width, height, options.Fps, _frames.StreamLayout,
            _audio?.SampleRate ?? DefaultSampleRate, Channels, _offline);
        var backend = _backendFactory(format);
        try
        {
            backend.Begin(format);
            _backend = backend;
            _format = format;
            _scheduler = new FrameScheduler(format.Fps, _offline);
            _timeline = new CaptureTimeline();
            _offlineFrame = 0;
            _audioFramesWritten = 0;
            _overflowPadded = 0;
            _dropped = 0;
            _audioStarted = false;
            _endReason = null;
            _stopwatch = Stopwatch.StartNew();
            _startTimestamp = Stopwatch.GetTimestamp(); // before the tap attaches, so the audio lead is never negative
            if (!_offline && _audio is not null)
            {
                _tap = new AudioCaptureTap(TapCapacitySamples);
                _audio.BeginCapture(_tap);
            }

            _frames.BeginStream(this);
        }
        catch
        {
            // Back to idle with nothing running, then let the caller see the original failure.
            if (_tap is not null)
                _audio!.EndCapture();
            _tap = null;
            _backend = null;
            DisposeQuietly(backend);
            throw;
        }

        Log.Info($"[Recorder] Recording {(_offline ? "offline" : "live")} to {format.OutputPath}");
        Started?.Invoke();
    }

    /// <summary>Ends the recording; finalising (encode tail, mux, sidecar) continues in the background.</summary>
    public void Stop()
    {
        if (!IsRecording)
            return;
        _frames!.EndStream(); // delivers the frames still in flight
        if (_tap is { } tap)
        {
            _audio!.EndCapture();
            if (tap.FirstBlockTimestamp != 0)
                Thread.Sleep(s_tailWait); // the block being mixed now; the WAV is padded to the video length anyway
        }

        WriteAudio(); // offline: the frames rendered since the last update; live: what the tap still holds

        var backend = _backend!;
        var format = _format!;
        var reason = _endReason;
        var written = _scheduler!.WrittenFrames;
        if (written == 0)
        {
            // Nothing to mux, and a sidecar with durationSeconds 0 is rejected by Cutback: abandon instead of finishing.
            _finishing = Task.Run(() => Abandon(backend, format, reason));
            return;
        }

        var duration = (double)written / format.Fps;
        var spans = _timeline!.Finish(duration);
        var info = new CaptureInfo(Path.GetFileName(format.OutputPath), _info.EngineVersion, _info.Game, _info.Scene(), _offline,
            format.Fps, format.Width, format.Height, duration, _dropped);
        _finishing = Task.Run(() => FinishAsync(backend, format, written, info, spans, reason));
    }

    /// <summary>
    /// Stops and waits for finalising (the window is closing: the audio server and renderer must outlive the mux), then
    /// raises <see cref="Stopped"/>. Null when there was nothing to wait for or it took longer than <paramref name="timeout"/>.
    /// </summary>
    internal RecordingResult? StopAndWait(TimeSpan timeout)
    {
        Stop();
        if (_finishing is not { } finishing || !finishing.Wait(timeout))
            return null;
        var result = finishing.Result;
        Complete(result);
        return result;
    }

    /// <summary>Main thread, every update: raises <see cref="Stopped"/> once finalising has finished.</summary>
    internal void PollCompletion()
    {
        if (_finishing is { IsCompleted: true } done)
            Complete(done.Result);
    }

    /// <summary>Marks a moment in the sidecar (a boss intro, a death); ignored when not recording.</summary>
    public void Mark(string label)
    {
        ArgumentNullException.ThrowIfNull(label);
        if (IsRecording)
            _timeline!.Mark(Now(), label);
    }

    /// <summary>Footage the editor must keep (a cutscene) until the returned scope is disposed; harmless when not recording.</summary>
    public IDisposable Keep(string label) => Scope(keep: true, label);

    /// <summary>Footage the editor should cut (a menu, a respawn wait) until the returned scope is disposed; harmless when not recording.</summary>
    public IDisposable Skip(string label) => Scope(keep: false, label);

    /// <summary>
    /// Every input event, before the UI can consume it (menus clicked while recording are still play). Held controls are
    /// tracked even when not recording, so a key already held when recording starts counts as input.
    /// </summary>
    internal void ObserveInput(InputEvent e)
    {
        if (_input.Observe(e) && IsRecording)
            _timeline!.Input(Now());
    }

    /// <summary>The window lost focus: its release events may never arrive, so nothing counts as held any more.</summary>
    internal void ReleaseHeldInput() => _input.Reset();

    /// <summary>Main thread, once per update before the scene ticks — loading updates included.</summary>
    internal void OnUpdate(bool loading, bool paused)
    {
        PollCompletion();
        if (!IsRecording)
            return;
        if (_endReason is null)
        {
            _timeline!.Update(Now(), _input.IsHeld, loading, paused);
            WriteAudio();
        }

        if (_endReason is not null)
            Stop();
    }

    // The sink members run inside VulkanRenderer.BeginFrame/EndFrame/EndStream: they never throw.

    bool IFrameStreamSink.ShouldCapture(out double stamp)
    {
        if (_scheduler is not { } scheduler || _format is not { } format || _endReason is not null || !IsRecording)
        {
            stamp = 0;
            return false;
        }

        stamp = _offline ? _offlineFrame++ / (double)format.Fps : Now();
        return scheduler.ShouldCapture(stamp);
    }

    void IFrameStreamSink.OnFrame(ReadOnlySpan<byte> pixels, int width, int height, double stamp)
    {
        if (_backend is not { } backend || _scheduler is not { } scheduler || _endReason is not null)
            return;
        var copies = scheduler.CopiesFor(stamp);
        if (copies <= 0)
            return;
        try
        {
            if (backend.TryWriteVideo(pixels, copies, wait: _offline))
                scheduler.Committed(copies);
            else
                _dropped++; // the encoder is behind; the next frame's copies make up the time
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or ObjectDisposedException)
        {
            Fail($"the encoder failed: {e.Message}");
        }
    }

    void IFrameStreamSink.OnStreamEnded(string reason) => Fail(reason);

    // The first failure wins; the next update (or the Stop in progress) ends the recording with it.
    private void Fail(string reason) => _endReason ??= reason;

    private double Now() => _offline
        ? _offlineFrame / (double)(_format?.Fps ?? 60)
        : _clockOverride?.Invoke() ?? _stopwatch?.Elapsed.TotalSeconds ?? 0;

    private void WriteAudio()
    {
        try
        {
            if (_offline)
                RenderOfflineAudio();
            else
                DrainLiveAudio();
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or ObjectDisposedException or UnauthorizedAccessException)
        {
            Fail($"writing the audio failed: {e.Message}");
        }
    }

    // Offline: through frame n the WAV holds exactly SamplesThrough(n) frames, rendered from the manual null device.
    private void RenderOfflineAudio()
    {
        var target = SampleScheduler.SamplesThrough(_offlineFrame, _format!.SampleRate, _format.Fps);
        var frames = (int)(target - _audioFramesWritten);
        if (frames <= 0)
            return;
        if (_audio is { } audio)
        {
            var samples = frames * Channels;
            if (_offlineAudio.Length < samples)
                _offlineAudio = new float[samples]; // grows to one frame's worth once, then never allocates
            var buffer = _offlineAudio.AsSpan(0, samples);
            audio.RenderNullDevice(frames, buffer);
            _backend!.WriteAudio(buffer);
        }
        else
        {
            _backend!.WriteSilence(frames);
        }

        _audioFramesWritten = target;
    }

    // Live: align the first block to t0 (pad the gap with silence), copy whatever the audio thread produced, and pad
    // blocks the ring had to drop with silence of the same length so audio never drifts against the video.
    private void DrainLiveAudio()
    {
        if (_tap is not { } tap)
            return;
        var firstBlock = tap.FirstBlockTimestamp; // before Channels: non-zero guarantees Channels is set
        if (firstBlock == 0)
            return;
        if (!_audioStarted)
        {
            if (tap.Channels != Channels)
            {
                Fail($"the audio device mixes {tap.Channels} channels and recordings are stereo");
                return;
            }

            var lead = Stopwatch.GetElapsedTime(_startTimestamp, firstBlock);
            _backend!.WriteSilence((long)Math.Max(0, lead.TotalSeconds * _format!.SampleRate));
            _audioStarted = true;
        }

        int n;
        while ((n = tap.Drain(_drain)) > 0)
            _backend!.WriteAudio(_drain.AsSpan(0, n));

        var overflow = tap.OverflowSamples;
        if (overflow > _overflowPadded)
        {
            _backend!.WriteSilence((overflow - _overflowPadded) / Channels);
            _overflowPadded = overflow;
        }
    }

    private static async Task<RecordingResult> FinishAsync(IRecordingBackend backend, RecordingFormat format, long frames,
        CaptureInfo info, CaptureSpans spans, string? reason)
    {
        RecordingResult result;
        try
        {
            result = await backend.FinishAsync(frames, info, spans, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or FfmpegNotFoundException or IOException or InvalidOperationException
                                      or ArgumentException or UnauthorizedAccessException)
        {
            result = new RecordingResult
            {
                Path = format.OutputPath,
                DurationSeconds = info.DurationSeconds,
                DroppedFrames = info.DroppedFrames,
                Error = e.Message + PartialNote(format),
            };
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // A bug, not an expected failure: log it in full, but Stopped must still be raised and the game keep running.
            Log.Error($"[Recorder] Finalising failed unexpectedly: {e}");
            result = new RecordingResult
            {
                Path = format.OutputPath,
                DurationSeconds = info.DurationSeconds,
                DroppedFrames = info.DroppedFrames,
                Error = $"Finalising the recording failed unexpectedly ({e.GetType().Name}: {e.Message})." + PartialNote(format),
            };
        }
        finally
        {
            DisposeQuietly(backend);
        }

        return reason is null ? result : result with { Error = $"Recording stopped: {reason}." + (result.Error is null ? "" : " " + result.Error) };
    }

    private static RecordingResult Abandon(IRecordingBackend backend, RecordingFormat format, string? reason)
    {
        DisposeQuietly(backend);
        var partial = format.OutputPath + ".partial";
        try
        {
            if (Directory.Exists(partial) && !Directory.EnumerateFileSystemEntries(partial).Any())
                Directory.Delete(partial);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"[Recorder] Could not remove the empty {partial}: {e.Message}");
        }

        var error = reason is null ? NoFrameError : $"{NoFrameError[..^1]}: {reason}.";
        return new RecordingResult { Path = format.OutputPath, Error = error + PartialNote(format) };
    }

    // Where the kept intermediate files are, when there are any.
    private static string PartialNote(RecordingFormat format)
    {
        var partial = format.OutputPath + ".partial";
        try
        {
            return Directory.Exists(partial) && Directory.EnumerateFileSystemEntries(partial).Any()
                ? $" The partial recording is in {partial}."
                : "";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    private static void DisposeQuietly(IRecordingBackend backend)
    {
        try
        {
            backend.Dispose();
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or ObjectDisposedException)
        {
            Log.Warning($"[Recorder] Closing the recording backend failed: {e.Message}");
        }
    }

    private void Complete(RecordingResult result)
    {
        _finishing = null;
        _backend = null;
        _tap = null;
        if (result.Error is null)
            Log.Info($"[Recorder] Saved {result.Path} ({result.DurationSeconds:0.0} s, {result.DroppedFrames} dropped frames).");
        else
            Log.Error($"[Recorder] {result.Error}");
        Stopped?.Invoke(result);
    }

    private IDisposable Scope(bool keep, string label)
    {
        ArgumentNullException.ThrowIfNull(label);
        if (!IsRecording)
            return NoScope.Instance;
        var timeline = _timeline!;
        var handle = keep ? timeline.OpenKeep(Now(), label) : timeline.OpenSkip(Now(), label);
        // A scope outliving its recording closes nothing: Stop already closed it at the end of the video.
        return new ScopeHandle(() =>
        {
            if (IsRecording && ReferenceEquals(timeline, _timeline))
                timeline.Close(handle, Now());
        });
    }

    private sealed class ScopeHandle(Action close) : IDisposable
    {
        private Action? _close = close;

        public void Dispose() => Interlocked.Exchange(ref _close, null)?.Invoke();
    }

    private sealed class NoScope : IDisposable
    {
        public static readonly NoScope Instance = new();

        public void Dispose()
        {
        }
    }
}

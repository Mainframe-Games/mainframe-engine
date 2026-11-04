using MainframeEngine.Video;
using MainframeEngine.Video.Analysis;
using MainframeEngine.Video.Capture;
using MainframeEngine.Video.Detection;
using MainframeEngine.Video.Media;
using MainframeEngine.Video.Models;
using MainframeEngine.Video.Timelines;
using MainframeEngine.Video.Transcript;
using Clip = MainframeEngine.Video.Timelines.Clip;

namespace MainframeEngine.Editor.Video;

/// <summary>Runs Detect Silence over one source range (behind a seam so tests never start ffmpeg).</summary>
public delegate Task<IReadOnlyList<SilenceSpan>> SilenceRunner(FfmpegLocation ffmpeg, string path, double duration, DetectionSettings settings,
    double start, double end, IProgress<double> progress, CancellationToken cancellationToken);

/// <summary>What a clip job started on: the clip, its media and the window, and the settings it ran with.</summary>
internal sealed record ClipJob(string ClipId, MediaRef Media, double In, double Out, DetectionSettings Settings);

/// <summary>What a transcription started with, and whether Remove Fillers waits for it.</summary>
internal sealed class TranscribeRun(WhisperModel model, bool downloads, MediaRef media, string path, string? clipId, bool thenRemoveFillers)
{
    public WhisperModel Model { get; } = model;

    /// <summary>True when the model is downloaded first (the first half of the progress).</summary>
    public bool Downloads { get; } = downloads;

    public MediaRef Media { get; } = media;

    public string Path { get; } = path;

    public string? ClipId { get; } = clipId;

    public bool ThenRemoveFillers { get; set; } = thenRemoveFillers;
}

/// <summary>
/// The transcripts of the project's media, by content hash: read from each file's <c>.mftranscript.json</c> the first time
/// it is asked for, so every clip of a recording shares one transcript.
/// </summary>
public sealed class TranscriptStore
{
    private readonly Dictionary<string, IReadOnlyList<Word>?> _byHash = new(StringComparer.Ordinal);

    /// <summary>Bumped when a transcript arrives: the Transcript tab rebuilds.</summary>
    public int Version { get; private set; }

    /// <summary>The words of <paramref name="media"/> (at <paramref name="fullPath"/>), or null when it has no transcript.</summary>
    public IReadOnlyList<Word>? Get(MediaRef media, string fullPath)
    {
        ArgumentNullException.ThrowIfNull(media);
        if (_byHash.TryGetValue(media.Hash, out var words))
            return words;
        try
        {
            // disk-io: the media's own transcript sidecar in the project, read once
            words = TranscriptSerializer.LoadAsync(fullPath, media.Hash, CancellationToken.None).GetAwaiter().GetResult()?.Words;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"[Video] Could not read the transcript of {Path.GetFileName(fullPath)}: {e.Message}");
            words = null;
        }

        _byHash[media.Hash] = words;
        return words;
    }

    /// <summary>Installs a transcript made now (its sidecar was written by the job).</summary>
    public void Set(MediaRef media, IReadOnlyList<Word> words)
    {
        ArgumentNullException.ThrowIfNull(media);
        _byHash[media.Hash] = words;
        Version++;
    }

    /// <summary>Forgets every transcript (another project).</summary>
    public void Clear()
    {
        _byHash.Clear();
        Version++;
    }
}

/// <summary>
/// Cutback's four tools on the selected clip of a timeline (G20d, docs/design/video-editor.md#the-per-clip-cutback-tools):
/// Detect Silence (a job over the clip's window), Detect Idle Gameplay (the recording's sidecar), Transcribe (a job over the
/// whole media file, cached beside it) and Remove Fillers. Each result is one undo step through
/// <see cref="TimelineEdits.SetClipCuts"/>, so the link group cuts together and the clips after it ripple, and each keeps
/// the cuts of its kind outside the window (<see cref="ClipDetection.InWindow"/>) and every manual cut.
/// </summary>
public sealed class ClipTools
{
    /// <summary>Word edges snap to the waveform within this.</summary>
    public const double WordSnapSeconds = 0.040;

    /// <summary>A snapped word shorter than this keeps Whisper's edges.</summary>
    public const double MinSnappedWordSeconds = 0.020;

    /// <summary>The waveform envelope's bucket for locating fillers.</summary>
    public const double EnvelopeBucketSeconds = 0.010;

    private readonly EditorWorkspace _workspace;

    public ClipTools(EditorWorkspace workspace)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
    }

    public TranscriptStore Transcripts { get; } = new();

    /// <summary>Detect Silence's ffmpeg run; tests return spans instead.</summary>
    internal SilenceRunner SilenceDetector { get; set; } = static (ffmpeg, path, duration, settings, start, end, p, ct) =>
        new SilenceDetector(ffmpeg).DetectAsync(path, duration, settings, p, ct, start, end);

    /// <summary>Transcribe's recogniser; tests return words instead.</summary>
    internal Func<FfmpegLocation, string, WhisperModel, ITranscriber> TranscriberFactory { get; set; } =
        static (ffmpeg, modelPath, model) => new WhisperTranscriber(ffmpeg, modelPath, model);

    private EditorSettings Settings => _workspace.Settings;

    /// <summary>True when no clip job runs on <paramref name="tab"/>.</summary>
    public static bool IsIdle(TimelineTab tab) => tab.SilenceJob is null && tab.TranscribeJob is null;

    /// <summary>The words of <paramref name="clip"/>'s media, or null.</summary>
    public IReadOnlyList<Word>? WordsOf(TimelineTab tab, Clip clip) => Transcripts.Get(clip.Media, tab.Document.Resolve(clip.Media));

    /// <summary>Whether the clip's recording has the Mainframe sidecar Detect Idle Gameplay reads.</summary>
    public static bool HasCapture(TimelineTab tab, Clip? clip) =>
        clip is not null && File.Exists(CaptureSidecar.PathFor(tab.Document.Resolve(clip.Media))); // disk-io: the media's sidecar

    // ── Detect Silence ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Detect Silence on the selected clip: ffmpeg reads only the clip's window in the background; <see cref="Poll"/> lays
    /// out the cuts. A second call while it runs cancels it.
    /// </summary>
    public void DetectSilence(TimelineTab tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (tab.SilenceJob is { } running)
        {
            if (!running.IsDone)
                running.Cancel();
            return;
        }

        if (ClipOf(tab, "Detect Silence") is not { } clip || FfmpegFor(tab, "Detect Silence") is not { } ffmpeg)
            return;
        var run = new ClipJob(clip.Id, clip.Media, clip.SourceIn, clip.SourceOut, Settings.VideoDetection);
        var path = tab.Document.Resolve(clip.Media);
        var detector = SilenceDetector;
        tab.SilenceRun = run;
        tab.SilenceJob = VideoJob.Start("silence", (p, ct) => detector(ffmpeg, path, run.Media.Duration, run.Settings, run.In, run.Out, p, ct));
        Log.Info($"[Video] Detecting silence in {Path.GetFileName(path)} ({VideoFormat.Clock(run.In)}–{VideoFormat.Clock(run.Out)})…");
    }

    private void FinishSilence(TimelineTab tab, VideoJob<IReadOnlyList<SilenceSpan>> job)
    {
        var run = tab.SilenceRun;
        tab.SilenceJob = null;
        tab.SilenceRun = null;
        var task = job.Task;
        if (task.IsCanceled || job.IsCancelled || task.Exception?.GetBaseException() is OperationCanceledException)
            tab.Notice = "Silence detection cancelled.";
        else if (task.Exception?.GetBaseException() is { } error)
            ReportFailure(tab, "Detect Silence", error);
        else if (run is not null)
            ApplySilence(tab, run, task.Result);
        job.Dispose();
    }

    // Kept out of Poll: a lambda capturing its locals there would allocate on every call.
    private void ApplySilence(TimelineTab tab, ClipJob run, IReadOnlyList<SilenceSpan> spans) => Guard("Detect Silence failed", () =>
    {
        if (tab.Timeline.FindClip(run.ClipId) is not { } clip)
        {
            tab.Notice = "The clip was removed while its silence was detected; nothing was cut.";
            return;
        }

        var inside = ClipDetection.SpansInWindow(spans, clip.SourceIn, clip.SourceOut);
        var planned = SilenceCutPlanner.Plan(inside, run.Settings, clip.Media.Duration);
        var before = ClipDetection.KeptInWindow(clip.Segments, clip.SourceIn, clip.SourceOut);
        tab.Controller.CancelGesture();
        tab.Document.Apply("Detect Silence", t => TimelineEdits.SetClipCuts(t, clip.Id, list =>
            list.ReplaceAutoSegments(ClipDetection.InWindow(planned, list.Segments, SegmentOrigin.Auto, clip.SourceIn, clip.SourceOut))));
        tab.Notice = ClipDetection.Status(planned.Count, "silence", "silences", before, KeptAfter(tab, clip.Id),
            "Detected", "No silence found with the current settings.");
    });

    // ── Detect Idle Gameplay ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Detect Idle Gameplay on the selected clip, from its recording's <c>.mfcap.json</c> (no ffmpeg needed).</summary>
    public void DetectIdle(TimelineTab tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (ClipOf(tab, "Detect Idle Gameplay") is not { } clip)
            return;
        Guard("Detect Idle Gameplay failed", () =>
        {
            var path = tab.Document.Resolve(clip.Media);
            // disk-io: the recording's sidecar beside it
            var capture = CaptureSidecarSerializer.TryLoadForVideoAsync(path, CancellationToken.None).GetAwaiter().GetResult();
            if (capture is null)
            {
                tab.Notice = $"{Path.GetFileName(path)} has no Mainframe recording data, so there is no gameplay to detect.";
                return;
            }

            var planned = GameplayCutPlanner.Plan(capture, clip.Segments, Settings.VideoGameplay, clip.Media.Duration);
            var inside = ClipDetection.InWindow(planned, [], SegmentOrigin.Gameplay, clip.SourceIn, clip.SourceOut);
            var before = ClipDetection.KeptInWindow(clip.Segments, clip.SourceIn, clip.SourceOut);
            tab.Controller.CancelGesture();
            tab.Document.Apply("Detect Idle Gameplay", t => TimelineEdits.SetClipCuts(t, clip.Id, list =>
                list.ReplaceOriginCuts(SegmentOrigin.Gameplay, ClipDetection.InWindow(planned, list.Segments, SegmentOrigin.Gameplay, clip.SourceIn, clip.SourceOut))));
            var after = KeptAfter(tab, clip.Id);
            var stretches = inside.Count == 1 ? "1 stretch of idle gameplay" : $"{inside.Count} stretches of idle gameplay";
            tab.Notice = inside.Count == 0
                ? "No idle gameplay found with the current settings."
                : Math.Abs(before - after) < 0.05
                    ? $"Found {stretches}; all were already cut."
                    : before > after
                        ? $"Cut {stretches}, {VideoFormat.Clock(before - after)} removed."
                        : $"Cut {stretches}, {VideoFormat.Clock(after - before)} restored.";
        });
    }

    // ── Transcribe ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Transcribe the selected clip's media file: downloads the model first when it is missing (the first half of the
    /// progress), runs Whisper on this computer, writes <c>&lt;media&gt;.mftranscript.json</c> and shows the Transcript tab.
    /// A media file that already has a transcript asks before making it again. A second call while it runs cancels it.
    /// </summary>
    public void Transcribe(TimelineTab tab, bool thenRemoveFillers = false)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (tab.TranscribeJob is { } running)
        {
            if (!running.IsDone)
                running.Cancel();
            return;
        }

        if (ClipOf(tab, "Transcribe") is not { } clip)
            return;
        if (WordsOf(tab, clip) is { Count: > 0 } words && !thenRemoveFillers)
        {
            _workspace.Message.Show(new MessageRequest
            {
                Title = "Transcribe",
                Message = $"{Path.GetFileName(clip.Media.Path)} already has a transcript ({words.Count} words); every clip of it shows it in the Transcript tab.",
                Buttons = ["Transcribe Again", "Show Transcript", "Cancel"],
                DefaultButton = 1,
                CancelButton = 2,
                Callback = (button, _) =>
                {
                    if (button == 0)
                        StartTranscribe(tab, clip, thenRemoveFillers: false);
                    else if (button == 1)
                        tab.ShowsTranscript = true;
                },
            });
            return;
        }

        StartTranscribe(tab, clip, thenRemoveFillers);
    }

    private void StartTranscribe(TimelineTab tab, Clip clip, bool thenRemoveFillers)
    {
        if (tab.TranscribeJob is not null || FfmpegFor(tab, "Transcribe") is not { } ffmpeg)
            return;
        var model = WhisperModelInfo.Parse(Settings.VideoWhisperModel);
        var store = new ModelStore(_workspace.Options.VideoModelsDirectory);
        var downloads = !store.IsDownloaded(model);
        var factory = TranscriberFactory;
        var media = clip.Media;
        var path = tab.Document.Resolve(media);
        var key = WhisperModelInfo.For(model).Key;
        tab.TranscribeRun = new TranscribeRun(model, downloads, media, path, clip.Id, thenRemoveFillers);
        tab.TranscribeJob = VideoJob.Start<IReadOnlyList<Word>>("transcribe", async (p, ct) =>
        {
            var share = downloads ? 0.5 : 0.0;
            var modelPath = await store.EnsureAsync(model, downloads ? new PhaseProgress(p, 0, 0.5) : null, ct).ConfigureAwait(false);
            var words = await factory(ffmpeg, modelPath, model).TranscribeAsync(path, new PhaseProgress(p, share, 1 - share), ct).ConfigureAwait(false);
            await TranscriptSerializer.SaveAsync(path, new TranscriptFile(TranscriptSerializer.CurrentVersion, media.Hash, key, words), ct).ConfigureAwait(false);
            return words;
        });
        Log.Info($"[Video] Transcribing {Path.GetFileName(path)} with {key}…");
    }

    private void FinishTranscribe(TimelineTab tab, VideoJob<IReadOnlyList<Word>> job)
    {
        var run = tab.TranscribeRun;
        tab.TranscribeJob = null;
        tab.TranscribeRun = null;
        var task = job.Task;
        if (task.IsCanceled || job.IsCancelled || task.Exception?.GetBaseException() is OperationCanceledException)
            tab.Notice = "Transcription cancelled.";
        else if (task.Exception?.GetBaseException() is { } error && IsMissingFfmpeg(error))
            tab.ReportNoFfmpeg("Transcribe");
        else if (task.Exception?.GetBaseException() is { } failure)
            _workspace.Commands.ReportError("Transcription failed", ExplainTranscribeFailure(failure, run));
        else if (run is not null)
            InstallTranscript(tab, run, task.Result);
        job.Dispose();
    }

    private void InstallTranscript(TimelineTab tab, TranscribeRun run, IReadOnlyList<Word> words) => Guard("Transcription failed", () =>
    {
        Transcripts.Set(run.Media, words);
        tab.Notice = words.Count == 0 ? "No speech was recognised." : $"Transcribed {words.Count} words.";
        tab.ShowsTranscript = true;
        if (words.Count > 0 && run.ThenRemoveFillers)
            RemoveFillers(tab);
    });

    // ── Remove Fillers ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Remove Filler Words in the selected clip's window, as one undo step. Without a transcript it transcribes first and
    /// cuts when the words arrive (while a transcription runs, it asks that one to).
    /// </summary>
    public void RemoveFillers(TimelineTab tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (ClipOf(tab, "Remove Filler Words") is not { } clip)
            return;
        if (WordsOf(tab, clip) is not { Count: > 0 } words)
        {
            if (tab.TranscribeRun is { } run && tab.TranscribeJob is not null)
                run.ThenRemoveFillers = true; // not Transcribe(): a second call would cancel the running job
            else
                Transcribe(tab, thenRemoveFillers: true);
            return;
        }

        Guard("Remove Filler Words failed", () => ApplyFillers(tab, clip, words));
    }

    private void ApplyFillers(TimelineTab tab, Clip clip, IReadOnlyList<Word> words)
    {
        var settings = Settings.VideoDetection;
        var inWindow = ClipDetection.WordsInWindow(words, clip.SourceIn, clip.SourceOut);
        var fillers = FillerDetector.Find(inWindow, Settings.VideoFillerWords);
        var waveform = tab.Cache?.WaveformOf(clip.Media.Hash, tab.Document.Resolve(clip.Media), clip.Media.Duration);
        var envelope = waveform?.Envelope(EnvelopeBucketSeconds);
        var threshold = envelope is null ? 0 : FillerSpanLocator.SpeechThreshold(envelope);
        var located = 0;
        var spans = new List<FillerSpan>(fillers.Count);
        foreach (var word in fillers)
        {
            var (start, end) = LocateFiller(words, word, waveform, envelope, threshold, ref located);
            spans.Add(new FillerSpan(start, end, FillerDetector.Normalize(word.Text)));
        }

        var planned = FillerCutPlanner.Plan(spans, clip.Segments, settings, clip.Media.Duration);
        var inside = ClipDetection.InWindow(planned, [], SegmentOrigin.Filler, clip.SourceIn, clip.SourceOut);
        tab.Controller.CancelGesture();
        tab.Document.Apply("Remove Filler Words", t => TimelineEdits.SetClipCuts(t, clip.Id, list =>
            list.ApplyFillerCuts(ClipDetection.InWindow(planned, list.Segments, SegmentOrigin.Filler, clip.SourceIn, clip.SourceOut))));
        tab.ShowsTranscript = true;

        var removed = VideoFormat.Clock(inside.Sum(c => c.Duration));
        var removedWords = spans.Count(s => inside.Any(c => c.Start < s.End && c.End > s.Start));
        var status = inside.Count switch
        {
            0 when fillers.Count == 0 => "No filler words found.",
            0 => "No filler words left to cut.",
            _ when removedWords == 1 => $"Removed 1 filler word, {removed} cut.",
            _ => $"Removed {removedWords} filler words, {removed} cut.",
        };
        tab.Notice = fillers.Count == 0 || located > 0
            ? status
            : envelope is null
                ? status + " The waveform is still loading; filler cuts use Whisper's rough timing."
                : status + " Transcribe again for cuts aligned to the audio.";
    }

    // Where a filler was spoken: the burst of speech around its anchor, else Whisper's span snapped to the waveform.
    private static (double Start, double End) LocateFiller(IReadOnlyList<Word> words, Word word, Waveform? waveform, double[]? envelope,
        double threshold, ref int located)
    {
        if (envelope is not null)
        {
            var index = -1;
            for (var i = 0; i < words.Count; i++)
                if (ReferenceEquals(words[i], word))
                {
                    index = i;
                    break;
                }

            var previous = index > 0 ? words[index - 1] : null;
            var next = index >= 0 && index + 1 < words.Count ? words[index + 1] : null;
            if (FillerSpanLocator.Locate(envelope, EnvelopeBucketSeconds, threshold, previous, word, next) is var (start, end))
            {
                located++;
                return (start, end);
            }
        }

        return SnapWord(waveform, word.Start, word.End);
    }

    /// <summary>Both edges of a word snapped to the quietest nearby sample; a word the snap would collapse keeps its edges.</summary>
    public static (double Start, double End) SnapWord(Waveform? waveform, double start, double end)
    {
        if (waveform is null)
            return (start, end);
        var snappedStart = waveform.SnapToZeroCrossing(start, WordSnapSeconds);
        var snappedEnd = waveform.SnapToZeroCrossing(end, WordSnapSeconds);
        return snappedEnd - snappedStart < MinSnappedWordSeconds ? (start, end) : (snappedStart, snappedEnd);
    }

    // ── Jobs ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Main thread, every frame: applies a tab's finished silence detection and transcription.</summary>
    internal void Poll(TimelineTab tab)
    {
        if (tab.SilenceJob is { IsDone: true } silence)
            FinishSilence(tab, silence);
        if (tab.TranscribeJob is { IsDone: true } transcribe)
            FinishTranscribe(tab, transcribe);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────────────────

    // The clip a tool runs on: the selected one (the video of a linked pair); else a notice says to select one.
    private static Clip? ClipOf(TimelineTab tab, string what)
    {
        if (tab.Primary is { } clip)
            return clip;
        tab.Notice = $"Select a clip first: {what} runs on the selected clip.";
        return null;
    }

    private static double KeptAfter(TimelineTab tab, string clipId) =>
        tab.Timeline.FindClip(clipId) is { } now ? ClipDetection.KeptInWindow(now.Segments, now.SourceIn, now.SourceOut) : 0;

    // The ffmpeg now (a changed setting reaches open tabs), else the one the tab opened with; else the notice.
    private FfmpegLocation? FfmpegFor(TimelineTab tab, string what)
    {
        try
        {
            return _workspace.Commands.LocateFfmpeg(Settings.VideoFfmpegPath);
        }
        catch (FfmpegMissingException)
        {
            if (tab.Ffmpeg is { } opened && File.Exists(opened.FfmpegPath))
                return opened;
            tab.ReportNoFfmpeg(what);
            return null;
        }
    }

    private static bool IsMissingFfmpeg(Exception error) => error is FfmpegMissingException or System.ComponentModel.Win32Exception;

    private void ReportFailure(TimelineTab tab, string what, Exception error)
    {
        if (IsMissingFfmpeg(error))
            tab.ReportNoFfmpeg(what);
        else
            _workspace.Commands.ReportError(what + " failed", error);
    }

    // Cutback's advice per failure: a truncated model is deleted so the next run fetches it again; a native library
    // that would not load says so (the model is fine then, and kept).
    private Exception ExplainTranscribeFailure(Exception error, TranscribeRun? run)
    {
        switch (error)
        {
            case ModelLoadException { IsNativeLibraryFailure: true } native:
                return new InvalidOperationException("The speech recognition library could not be loaded. " + native.InnerException!.Message, native);
            case ModelLoadException load:
                var store = new ModelStore(_workspace.Options.VideoModelsDirectory);
                var model = run?.Model ?? WhisperModelInfo.Parse(Settings.VideoWhisperModel);
                if (!store.IsPlausiblyComplete(model))
                {
                    store.Delete(model);
                    return new InvalidOperationException(load.Message + " The cached file looked incomplete and has been removed; run Transcribe again to download it.", load);
                }

                return new InvalidOperationException(load.Message + $" If this keeps happening, delete the file at {load.ModelPath} to download it again.", load);
            case DllNotFoundException:
                return new InvalidOperationException("The speech recognition library could not be loaded. " + error.Message, error);
            default:
                return error;
        }
    }

    private void Guard(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception e) when (EditorCommands.IsRecoverable(e))
        {
            _workspace.Commands.ReportError(what, e);
        }
    }

    // One phase of a job's progress mapped into [offset, offset + scale] of the whole bar.
    private sealed class PhaseProgress(IProgress<double> inner, double offset, double scale) : IProgress<double>
    {
        public void Report(double value) => inner.Report(offset + Math.Clamp(value, 0, 1) * scale);
    }
}

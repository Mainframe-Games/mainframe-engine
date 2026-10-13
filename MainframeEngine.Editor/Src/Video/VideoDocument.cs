using System.Text.Json;
using MainframeEngine.Video;
using MainframeEngine.Video.Capture;
using MainframeEngine.Video.Detection;
using MainframeEngine.Video.Media;
using MainframeEngine.Video.Models;
using MainframeEngine.Video.Projects;
using MainframeEngine.Video.Transcript;

namespace MainframeEngine.Editor.Video;

/// <summary>
/// One video being trimmed: the project (source, segments, transcript), its undo history, the recording sidecar and the
/// waveform, with every operation Video mode offers (detect, edit by transcript, save). It owns no UI, so the tab, the
/// timeline and the transcript panel all drive the same object and the tests drive it without a window.
/// <para>
/// Ported from Cutback's <c>MainWindowViewModel</c>. The behaviour that matters is kept exactly: re-detection never
/// discards a manual edit, every operation is one undo step, and word edges snap to the quietest waveform bucket within
/// <see cref="WordSnapSeconds"/> (Whisper's timing is coarse) while dragged boundaries snap within
/// <see cref="BoundarySnapSeconds"/>.
/// </para>
/// </summary>
public sealed class VideoDocument
{
    /// <summary>Undo steps kept when the caller does not say.</summary>
    public const int DefaultHistoryLimit = 100;

    /// <summary>Snap window for a dragged boundary or a drawn section.</summary>
    public const double BoundarySnapSeconds = 0.020;

    /// <summary>Snap window for a transcript word's edges: twice a boundary drag's, because Whisper timing is coarse.</summary>
    public const double WordSnapSeconds = 0.040;

    /// <summary>Envelope resolution for locating fillers: fine enough to find a word boundary, coarse enough to ignore pitch periods.</summary>
    public const double EnvelopeBucketSeconds = 0.010;

    /// <summary>A snapped word shorter than this has collapsed, so the raw edges are used instead.</summary>
    private const double MinSnappedWordSeconds = 0.02;

    private readonly string? _legacyPath;
    private bool _legacyDirty;
    private VideoProject _baseProject;
    private VideoProject? _cachedProject;
    private int _cachedVersion = -1;
    private IReadOnlyList<Word> _transcript;
    private bool _transcriptDirty;
    private bool _dirty;

    private VideoDocument(VideoProject project, string? projectPath, string? legacyPath, CaptureSidecar? capture,
        IReadOnlyList<string> warnings, int historyLimit, bool legacy)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(warnings);
        _baseProject = project;
        _transcript = project.Transcript;
        TranscriptHasAnchors = HasAnchors(_transcript);
        FilePath = projectPath;
        _legacyPath = legacyPath;
        _legacyDirty = legacy;
        Capture = capture;
        Warnings = [.. warnings];

        Segments = new SegmentList(project.Source.DurationSeconds, project.Segments);
        Editor = new SegmentEditor(Segments, new EditHistory<Segment[]>(historyLimit));
        Segments.Changed += (_, _) =>
        {
            Version++;
            Changed?.Invoke();
        };
        Editor.DirtyChanged += (_, _) => UpdateDirty();
        _dirty = legacy;
    }

    /// <summary>Raised after the partition or the transcript changes. The timeline and transcript panel redraw on it.</summary>
    public event Action? Changed;

    /// <summary>Raised only when <see cref="IsDirty"/> flips, so the tab title updates without a flicker on every drag step.</summary>
    public event Action? DirtyChanged;

    /// <summary>
    /// The project as it would be saved now: the live segments and transcript over the source and the settings the last
    /// Detect silence run used. Rebuilt only when <see cref="Version"/> moves.
    /// </summary>
    public VideoProject Project
    {
        get
        {
            if (_cachedProject is null || _cachedVersion != Version)
            {
                _cachedProject = _baseProject with { Segments = Segments.Segments.ToArray(), Transcript = _transcript };
                _cachedVersion = Version;
            }

            return _cachedProject;
        }
    }

    /// <summary>The <c>.mfcut</c> this document saves to; null until saved (a new recording, or an imported <c>.cutback</c>).</summary>
    public string? FilePath { get; private set; }

    public string SourcePath => _baseProject.Source.Path;

    public double Duration => _baseProject.Source.DurationSeconds;

    /// <summary>The partition of the timeline. Edit it through <see cref="Editor"/> so the change is an undo step.</summary>
    public SegmentList Segments { get; }

    public SegmentEditor Editor { get; }

    /// <summary>The Mainframe Engine recording data next to the video, or null for any other video.</summary>
    public CaptureSidecar? Capture { get; }

    /// <summary>Set by the tab once its extraction job finishes; null until then, and every snap passes the time through.</summary>
    public Waveform? Waveform { get; set; }

    public IReadOnlyList<Word> Transcript => _transcript;

    /// <summary>
    /// False when the transcript has words but none carries an alignment anchor: it was made before anchors existed, so
    /// filler cuts fall back to Whisper's heuristic spans and the status line says to transcribe again.
    /// </summary>
    public bool TranscriptHasAnchors { get; private set; }

    /// <summary>Problems found on open that did not stop it: a changed source, an unreadable sidecar.</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>Plus one per partition or transcript change; lets a cache check whether it is stale.</summary>
    public int Version { get; private set; }

    /// <summary>Unsaved segment edits, an unsaved transcript, or an imported <c>.cutback</c> not yet saved as <c>.mfcut</c>.</summary>
    public bool IsDirty => _dirty;

    /// <summary>
    /// Where Save As starts: next to the video as <c>&lt;name&gt;.mfcut</c>, or next to the old file for an imported
    /// <c>.cutback</c>, so the original is never overwritten.
    /// </summary>
    public string DefaultProjectPath
    {
        get
        {
            var origin = _legacyPath ?? SourcePath;
            return Path.Combine(Path.GetDirectoryName(origin) ?? "", Path.GetFileNameWithoutExtension(origin) + ProjectSerializer.FileExtension);
        }
    }

    public static VideoDocument FromProject(VideoProject project, string? projectPath, CaptureSidecar? capture,
        IReadOnlyList<string> warnings, int historyLimit = DefaultHistoryLimit, bool legacy = false) =>
        new(project, projectPath, legacyPath: null, capture, warnings, historyLimit, legacy);

    /// <summary>
    /// Opens a <c>.mfcut</c>, a legacy <c>.cutback</c> or a video (a new, unsaved project). The waveform is not read here: the
    /// tab starts that as a job so the document appears at once.
    /// </summary>
    /// <exception cref="FileNotFoundException">A project's recording is missing.</exception>
    /// <exception cref="InvalidOperationException">The video has no audio track.</exception>
    /// <exception cref="ProjectFormatException">The project file is damaged or newer than this build.</exception>
    /// <exception cref="MediaProbeException">ffprobe could not read the video.</exception>
    public static async Task<VideoDocument> OpenAsync(string path, FfmpegLocation ffmpeg, int historyLimit, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(ffmpeg);

        var warnings = new List<string>();
        VideoProject project;
        string? projectPath = null;
        string? legacyPath = null;
        if (ProjectSerializer.IsProjectPath(path))
        {
            project = await ProjectSerializer.LoadAsync(path, cancellationToken).ConfigureAwait(false);
            var source = project.Source.Path;
            if (!File.Exists(source))
            {
                throw new FileNotFoundException($"The recording this project trims is missing: {source}", source);
            }

            if (!await SourceHash.MatchesAsync(source, project.Source.Sha256, cancellationToken).ConfigureAwait(false))
            {
                warnings.Add($"{Path.GetFileName(source)} has changed since this project was saved; check the cuts.");
            }

            if (Path.GetExtension(path).Equals(ProjectSerializer.LegacyFileExtension, StringComparison.OrdinalIgnoreCase))
            {
                legacyPath = path;
            }
            else
            {
                projectPath = path;
            }
        }
        else
        {
            var name = Path.GetFileName(path);
            var info = await new MediaProbe(ffmpeg).ProbeAsync(path, cancellationToken).ConfigureAwait(false);
            if (!info.HasAudio)
            {
                throw new InvalidOperationException($"{name} has no audio track, so it cannot be trimmed here.");
            }

            var hash = await SourceHash.ComputeAsync(path, cancellationToken).ConfigureAwait(false);
            project = VideoProject.CreateNew(new SourceInfo(Path.GetFullPath(path), hash, info.DurationSeconds, info.Width, info.Height, info.FrameRate));
        }

        CaptureSidecar? capture = null;
        try
        {
            capture = await CaptureSidecarSerializer.TryLoadForVideoAsync(project.Source.Path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is CaptureSidecarFormatException or JsonException or IOException or UnauthorizedAccessException)
        {
            warnings.Add($"The recording's sidecar could not be read ({ex.Message}); Detect Idle Gameplay is off.");
        }

        return new VideoDocument(project, projectPath, legacyPath, capture, warnings, historyLimit, legacy: legacyPath is not null);
    }

    /// <summary>
    /// Writes the project to <paramref name="path"/> (else <see cref="FilePath"/>), atomically, adding <c>.mfcut</c> to a
    /// path with no extension. The document is then clean and named.
    /// </summary>
    /// <exception cref="InvalidOperationException">The document is unnamed and no path was given.</exception>
    public void Save(string? path = null)
    {
        path ??= FilePath ?? throw new InvalidOperationException("This project has no file yet; choose where to save it.");
        if (Path.GetExtension(path).Length == 0)
        {
            path += ProjectSerializer.FileExtension;
        }

        ProjectSerializer.SaveAsync(Project, path, CancellationToken.None).GetAwaiter().GetResult();
        FilePath = path;
        _transcriptDirty = false;
        _legacyDirty = false;
        Editor.MarkSaved();
        UpdateDirty();
    }

    /// <summary>
    /// Cuts the loading, pause and skip stretches and the idle gameplay the recording data describes, in one undo step.
    /// Manual cuts and kept ranges are respected.
    /// </summary>
    /// <returns>The status line.</returns>
    /// <exception cref="InvalidOperationException">The video has no recording data.</exception>
    public string DetectIdleGameplay(GameplaySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (Capture is null)
        {
            throw new InvalidOperationException("This video has no Mainframe recording data, so there is no gameplay to detect.");
        }

        var cuts = GameplayCutPlanner.Plan(Capture, Segments.Segments, settings, Duration);
        Editor.Edit(s => s.ReplaceOriginCuts(SegmentOrigin.Gameplay, cuts));

        return cuts.Count == 0
            ? "No idle gameplay found with the current settings."
            : $"Cut {cuts.Count} stretch{(cuts.Count == 1 ? "" : "es")} of idle gameplay, {VideoFormat.Clock(cuts.Sum(c => c.Duration))} removed.";
    }

    /// <summary>
    /// Lays out the cuts for the silences ffmpeg found, in one undo step, and records <paramref name="settings"/> as the ones
    /// this run used. Manual and filler cuts survive: only <c>auto</c> footage is re-laid.
    /// </summary>
    /// <returns>The status line.</returns>
    public string ApplySilence(IReadOnlyList<SilenceSpan> spans, DetectionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(spans);
        ArgumentNullException.ThrowIfNull(settings);
        var cuts = SilenceCutPlanner.Plan(spans, settings, Duration);

        Editor.Edit(s => s.ReplaceAutoSegments(cuts));
        _baseProject = _baseProject with { Settings = settings };
        _cachedProject = null;

        return cuts.Count == 0
            ? "No silence found with the current settings."
            : $"Detected {cuts.Count} silence{(cuts.Count == 1 ? "" : "s")}, {VideoFormat.Clock(cuts.Sum(c => c.Duration))} removed.";
    }

    /// <summary>Installs a freshly transcribed (or cleared) transcript. The document is dirty until saved.</summary>
    public void SetTranscript(IReadOnlyList<Word> words)
    {
        ArgumentNullException.ThrowIfNull(words);
        _transcript = [.. words];
        TranscriptHasAnchors = HasAnchors(_transcript);
        _transcriptDirty = true;
        Version++;
        Changed?.Invoke();
        UpdateDirty();
    }

    /// <summary>
    /// Snaps both edges of a word to the quietest nearby waveform bucket. A word the snap would collapse keeps its raw
    /// edges, and with no waveform yet the edges pass through.
    /// </summary>
    public (double Start, double End) SnapWordSpan(double start, double end)
    {
        if (Waveform is not { } waveform)
        {
            return (start, end);
        }

        var snappedStart = waveform.SnapToZeroCrossing(start, WordSnapSeconds);
        var snappedEnd = waveform.SnapToZeroCrossing(end, WordSnapSeconds);
        return snappedEnd - snappedStart < MinSnappedWordSeconds ? (start, end) : (snappedStart, snappedEnd);
    }

    /// <summary>A time snapped to the quietest nearby waveform sample, for boundary drags and drawn sections.</summary>
    public double SnapTime(double time) => Waveform?.SnapToZeroCrossing(time, BoundarySnapSeconds) ?? time;

    /// <summary>
    /// A click or drag across transcript words <paramref name="first"/>..<paramref name="last"/>. The run takes the opposite
    /// state of the word the gesture started on (<paramref name="anchor"/>): clicking a kept word cuts it, clicking a struck
    /// word restores it. Marked manual, like a section drawn on the timeline, and one undo step.
    /// </summary>
    /// <returns>Whether the partition changed.</returns>
    public bool CutWords(int first, int last, int anchor)
    {
        if (_transcript.Count == 0)
        {
            return false;
        }

        var lastIndex = _transcript.Count - 1;
        var from = Math.Clamp(Math.Min(first, last), 0, lastIndex);
        var to = Math.Clamp(Math.Max(first, last), 0, lastIndex);
        var enabled = TranscriptView.IsCut(_transcript[Math.Clamp(anchor, from, to)], Segments.Segments);
        var (start, end) = SnapWordSpan(_transcript[from].Start, _transcript[to].End);
        return Editor.Edit(s => s.SetRange(start, end, enabled));
    }

    /// <summary>
    /// Cuts every filler word, in one undo step. The caller transcribes first when there is no transcript. The pipeline is
    /// <see cref="FillerDetector"/>, <see cref="FillerSpanLocator"/> (falling back to the snapped heuristic span for a word
    /// without an anchor), <see cref="FillerCutPlanner"/>, then <c>ApplyFillerCuts</c>.
    /// </summary>
    /// <returns>The status line.</returns>
    public string RemoveFillerWords(IReadOnlyList<string> fillerWords, DetectionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(fillerWords);
        ArgumentNullException.ThrowIfNull(settings);

        var fillers = FillerDetector.Find(_transcript, fillerWords);
        var envelope = Waveform?.Envelope(EnvelopeBucketSeconds);
        var threshold = envelope is null ? 0 : FillerSpanLocator.SpeechThreshold(envelope);
        var located = 0;
        var spans = new List<FillerSpan>(fillers.Count);
        foreach (var word in fillers)
        {
            var (start, end) = LocateFillerSpan(word, envelope, threshold, ref located);
            spans.Add(new FillerSpan(start, end, FillerDetector.Normalize(word.Text)));
        }

        var cuts = FillerCutPlanner.Plan(spans, Segments.Segments, settings, Duration);
        Editor.Edit(s => s.ApplyFillerCuts(cuts));

        var removed = VideoFormat.Clock(cuts.Sum(c => c.Duration));
        var removedWords = spans.Count(s => cuts.Any(c => c.Start < s.End && c.End > s.Start));
        var status = cuts.Count switch
        {
            0 when fillers.Count == 0 => "No filler words found.",
            0 => "No filler words left to cut.",
            _ when removedWords == 1 => $"Removed 1 filler word, {removed} cut.",
            _ => $"Removed {removedWords} filler words, {removed} cut.",
        };
        return fillers.Count > 0 && located == 0 ? status + " Transcribe again for cuts aligned to the audio." : status;
    }

    /// <summary>The edited transcript (cut words omitted) as SRT or plain text, with times remapped to the exported video.</summary>
    public string TranscriptText(bool srt) => srt
        ? TranscriptExporter.ToSrt(_transcript, Segments.Segments)
        : TranscriptExporter.ToPlainText(_transcript, Segments.Segments);

    /// <summary>
    /// Where a filler was actually spoken: the burst of speech around its alignment anchor when the transcript has anchors,
    /// else Whisper's own span snapped to the waveform. <paramref name="located"/> counts the anchored hits.
    /// </summary>
    private (double Start, double End) LocateFillerSpan(Word word, double[]? envelope, double threshold, ref int located)
    {
        if (envelope is not null)
        {
            var index = -1;
            for (var i = 0; i < _transcript.Count; i++)
            {
                if (ReferenceEquals(_transcript[i], word))
                {
                    index = i;
                    break;
                }
            }

            var previous = index > 0 ? _transcript[index - 1] : null;
            var next = index >= 0 && index + 1 < _transcript.Count ? _transcript[index + 1] : null;
            if (FillerSpanLocator.Locate(envelope, EnvelopeBucketSeconds, threshold, previous, word, next) is var (start, end))
            {
                located++;
                return (start, end);
            }
        }

        return SnapWordSpan(word.Start, word.End);
    }

    private static bool HasAnchors(IReadOnlyList<Word> words) => words.Any(w => w.Anchor is not null);

    private void UpdateDirty()
    {
        var dirty = Editor.IsDirty || _transcriptDirty || _legacyDirty;
        if (dirty == _dirty)
        {
            return;
        }

        _dirty = dirty;
        DirtyChanged?.Invoke();
    }
}

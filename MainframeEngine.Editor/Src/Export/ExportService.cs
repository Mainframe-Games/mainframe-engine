using System.Collections.Concurrent;

namespace MainframeEngine.Editor;

/// <summary>
/// The editor's side of File › Export (docs/design/game-export.md#editor-file--export): runs presets with an
/// <see cref="IExportRunner"/> on the thread pool, one export pipeline at a time, and reports to the main thread.
/// </summary>
/// <remarks>
/// Threading, as <see cref="PlayService"/>: every public member is called from the editor's main thread. Runner progress,
/// output lines and the result arrive on background threads and are only queued; <see cref="Update"/> (once per editor
/// frame) drains them and raises <see cref="Changed"/>, <see cref="Line"/> and <see cref="Finished"/>. An idle
/// <see cref="Update"/> allocates nothing. A start while Play is building waits for that build (<see cref="IsWaiting"/>).
/// </remarks>
public sealed class ExportService : IDisposable
{
    /// <summary>The log lines the dialog keeps.</summary>
    public const int MaxRecentLines = 200;

    /// <summary>Why Play and Build &amp; Reload are disabled while an export runs (their tooltips and log line).</summary>
    public const string BusyText = "An export is running (File › Export…); Play and Build & Reload wait until it ends.";

    private readonly Func<bool> _otherBuildRunning;
    private readonly ConcurrentQueue<string> _lines = new();
    private readonly Queue<string> _recent = new();
    private readonly object _progressGate = new();
    private IExportRunner _runner;
    private ExportProgress? _pendingProgress;
    private Task<IReadOnlyList<ExportResult>>? _task;
    private CancellationTokenSource? _cancel;
    private (string Directory, IReadOnlyList<ExportPreset> Presets)? _waiting;
    private Task<ExportEnvironment>? _detecting;
    private bool _detect;

    /// <param name="runner">The runner; null: a real <see cref="ExportRunner"/> on this machine (its SDK detected in the background).</param>
    /// <param name="otherBuildRunning">Whether Play or Build &amp; Reload is building now (an export waits for it).</param>
    public ExportService(IExportRunner? runner, Func<bool> otherBuildRunning)
    {
        _otherBuildRunning = otherBuildRunning ?? throw new ArgumentNullException(nameof(otherBuildRunning));
        if (runner is not null)
        {
            _runner = runner;
            return;
        }

        // Until the SDK check answers (started on first use, once per session), validation knows only whether dotnet was found.
        _runner = new ExportRunner(new ExportEnvironment { DotnetPath = DotnetSdk.FindDotnet() });
        _detect = true;
    }

    private void DetectOnce()
    {
        if (!_detect)
            return;
        _detect = false;
        _detecting = Task.Run(() => ExportEnvironment.DetectAsync());
    }

    /// <summary>An export pipeline is running (or waiting for a build to finish).</summary>
    public bool IsRunning => _task is not null || _waiting is not null;

    /// <summary>The export waits for Play's or Build &amp; Reload's build first.</summary>
    public bool IsWaiting => _waiting is not null;

    /// <summary>The last progress (null before the first report of a run).</summary>
    public ExportProgress? Progress { get; private set; }

    /// <summary>The names of the presets of the current or last run.</summary>
    public IReadOnlyList<string> PresetNames { get; private set; } = [];

    /// <summary>The last <see cref="MaxRecentLines"/> output lines, oldest first.</summary>
    public IReadOnlyCollection<string> RecentLines => _recent;

    /// <summary>The results of the last finished run (empty before one).</summary>
    public IReadOnlyList<ExportResult> LastResults { get; private set; } = [];

    /// <summary>Progress, start or end changed (main thread).</summary>
    public event Action? Changed;

    /// <summary>An output line (main thread).</summary>
    public event Action<string>? Line;

    /// <summary>A run ended, with one result per preset (main thread).</summary>
    public event Action<IReadOnlyList<ExportResult>>? Finished;

    /// <summary>The status line: <c>Exporting 2/5 · Linux · Publish</c>, <c>Waiting for the build…</c>, or empty.</summary>
    public string Status
    {
        get
        {
            if (_waiting is not null)
                return "Waiting for the build…";
            if (_task is null)
                return "";
            if (Progress is not { } p)
                return "Exporting…";
            var name = p.PresetIndex < PresetNames.Count ? PresetNames[p.PresetIndex] : "";
            return $"Exporting {p.PresetIndex + 1}/{p.PresetCount} · {name} · {p.Status}";
        }
    }

    /// <summary>Validates a preset (cheap; no process starts).</summary>
    public IReadOnlyList<ExportIssue> Validate(string projectDirectory, ProjectSettings project, ExportPreset preset, AssetDatabase? database = null)
    {
        DetectOnce();
        return _runner.Validate(projectDirectory, project, preset, database);
    }

    /// <summary>Starts exporting <paramref name="presets"/> in order; false when an export is already running.</summary>
    public bool Start(string projectDirectory, IReadOnlyList<ExportPreset> presets)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        ArgumentNullException.ThrowIfNull(presets);
        if (IsRunning || presets.Count == 0)
            return false;
        DetectOnce();
        PresetNames = [.. presets.Select(p => p.Name)];
        LastResults = [];
        Progress = null;
        _recent.Clear();
        var copies = presets.Select(p => p.Clone()).ToArray();
        if (_otherBuildRunning())
            _waiting = (projectDirectory, copies);
        else
            Begin(projectDirectory, copies);
        Changed?.Invoke();
        return true;
    }

    /// <summary>Cancels the running (or waiting) export: <c>dotnet publish</c> is killed, the previous artifacts stay.</summary>
    public void Cancel()
    {
        if (_waiting is not null)
        {
            _waiting = null;
            LastResults = [];
            Changed?.Invoke();
            return;
        }

        _cancel?.Cancel();
    }

    private void Begin(string projectDirectory, IReadOnlyList<ExportPreset> presets)
    {
        // The real runner needs the SDK check's answer first (an export without the SDK fails validation, not dotnet).
        if (_detecting is { } detecting)
        {
            _detecting = null;
            try
            {
                _runner = new ExportRunner(detecting.GetAwaiter().GetResult());
            }
            catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                Log.Warning($"[export] The .NET SDK check failed: {e.Message}");
            }
        }

        _cancel = new CancellationTokenSource();
        var token = _cancel.Token;
        var runner = _runner;
        var progress = new QueueProgress(this);
        _task = Task.Run(() => runner.RunAllAsync(projectDirectory, presets, progress, _lines.Enqueue, token), CancellationToken.None);
    }

    /// <summary>Drains queued lines and progress, starts a waiting export, completes a finished one (main thread, per frame).</summary>
    public void Update()
    {
        if (_detecting is { IsCompleted: true } detecting)
        {
            _detecting = null;
            if (detecting.IsCompletedSuccessfully)
                _runner = new ExportRunner(detecting.Result);
            Changed?.Invoke();
        }

        if (_waiting is { } waiting && !_otherBuildRunning())
        {
            _waiting = null;
            Begin(waiting.Directory, waiting.Presets);
            Changed?.Invoke();
        }

        var changed = false;
        while (_lines.TryDequeue(out var line))
        {
            _recent.Enqueue(line);
            while (_recent.Count > MaxRecentLines)
                _recent.Dequeue();
            Line?.Invoke(line);
            changed = true;
        }

        lock (_progressGate)
        {
            if (_pendingProgress is { } pending)
            {
                _pendingProgress = null;
                if (Progress != pending)
                {
                    Progress = pending;
                    changed = true;
                }
            }
        }

        if (_task is { IsCompleted: true } task)
        {
            _task = null;
            _cancel?.Dispose();
            _cancel = null;
            LastResults = task.IsCompletedSuccessfully
                ? task.Result
                : [new ExportResult(PresetNames.Count > 0 ? PresetNames[0] : "", false, false, null, null, TimeSpan.Zero, [], [],
                    task.Exception?.GetBaseException().Message ?? "The export stopped.")];
            Changed?.Invoke();
            Finished?.Invoke(LastResults);
            return;
        }

        if (changed)
            Changed?.Invoke();
    }

    /// <summary>Cancels a running export and waits briefly for it (editor quit).</summary>
    public void Dispose()
    {
        _waiting = null;
        if (_task is { } task)
        {
            _cancel?.Cancel();
            try
            {
                task.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
            }
        }

        _cancel?.Dispose();
    }

    // Keeps only the newest progress for the main thread (Progress<T> would post every report to the pool).
    private sealed class QueueProgress(ExportService service) : IProgress<ExportProgress>
    {
        public void Report(ExportProgress value)
        {
            lock (service._progressGate)
                service._pendingProgress = value;
        }
    }
}

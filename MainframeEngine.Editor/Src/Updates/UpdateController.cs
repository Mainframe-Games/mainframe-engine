namespace MainframeEngine.Editor;

public enum UpdateState
{
    Idle,
    Downloading,
    Ready,
    Failed,
}

/// <summary>
/// The editor side of updates (docs/design/editor-updates.md): the start-up check and the last update's report, the
/// badges' state, the update dialog's download and Update &amp; restart. Main thread only: background work runs as tasks
/// that <see cref="Tick"/> polls, like the Project Manager's SDK check.
/// </summary>
public sealed class UpdateController(EditorWorkspace workspace, IUpdateService? service) : IDisposable
{
    private readonly ProgressSink _progress = new();
    private Task<UpdateCheckResult>? _check;
    private bool _manualCheck;
    private Task<UpdateResult?>? _cleanUp;
    private Task<StagedUpdate>? _download;
    private CancellationTokenSource? _downloadCancel;
    private int _shownPercent = -1;
    private bool _preview;

    public bool IsEnabled => service is not null;

    /// <summary>The newer release (null: none known).</summary>
    public UpdateCheckResult? Available { get; private set; }

    public UpdateState State { get; private set; }

    /// <summary>Download progress 0–1.</summary>
    public double Progress => State switch
    {
        UpdateState.Downloading => _progress.Value,
        UpdateState.Ready => 1,
        _ => 0,
    };

    public string? Error { get; private set; }

    public StagedUpdate? Staged { get; private set; }

    public bool CanReplace => service?.Install.CanReplace ?? _preview;

    public string? InstallHint => service?.Install.Hint;

    public string CurrentVersion => service?.CurrentVersion ?? EngineInfo.Version;

    /// <summary>Raised when anything the badges or the dialog show changed.</summary>
    public event Action? Changed;

    /// <summary>Start-up: report the last update, then check unless Editor Settings turned checks off.</summary>
    public void Start()
    {
        if (service is null)
            return;
        _cleanUp = service.CleanUpAsync();
        if (workspace.Settings.CheckForUpdates)
            _check = service.CheckAsync(CancellationToken.None);
    }

    /// <summary>Help › Check for Updates…: reports every outcome (a running start-up check is reused).</summary>
    public void CheckNow()
    {
        if (service is null)
        {
            workspace.Message.Show(new MessageRequest { Title = "Check for Updates", Message = "Updates are not available in this run.", Icon = "refresh" });
            return;
        }

        _manualCheck = true;
        _check ??= service.CheckAsync(CancellationToken.None);
    }

    /// <summary>Main thread, every frame: finishes checks, clean-up and downloads.</summary>
    public void Tick()
    {
        if (_cleanUp is { IsCompleted: true } cleanUp)
        {
            _cleanUp = null;
            Report(cleanUp);
        }

        if (_check is { IsCompleted: true } check)
        {
            _check = null;
            FinishCheck(check);
        }

        if (_download is { IsCompleted: true } download)
        {
            _download = null;
            FinishDownload(download);
        }
        else if (_download is not null)
        {
            var percent = (int)(_progress.Value * 100);
            if (percent != _shownPercent)
            {
                _shownPercent = percent;
                Changed?.Invoke();
            }
        }
    }

    /// <summary>The dialog's main button: download (or retry); when staged, Update &amp; restart or show the files.</summary>
    public void Primary()
    {
        if (service is null || Available is not { IsUpdate: true } update)
            return;
        switch (State)
        {
            case UpdateState.Idle or UpdateState.Failed:
                BeginDownload(service, update);
                break;
            case UpdateState.Ready when Staged is { } staged:
                if (CanReplace)
                    Restart(service, staged);
                else
                    service.Reveal(staged);
                break;
        }
    }

    public void Cancel() => _downloadCancel?.Cancel();

    /// <summary>Shows <paramref name="result"/> as if a check found it and opens the dialog (QA captures; no network).</summary>
    public void ShowPreview(UpdateCheckResult result)
    {
        Available = result ?? throw new ArgumentNullException(nameof(result));
        _preview = true;
        State = UpdateState.Idle;
        Error = null;
        Changed?.Invoke();
        workspace.UpdateDialog.Open();
    }

    private static void Report(Task<UpdateResult?> cleanUp)
    {
        if (!cleanUp.IsCompletedSuccessfully || cleanUp.Result is not { } result)
            return;
        if (result.Ok)
            Log.Info($"[Editor] Updated to v{result.To} (from v{result.From}).");
        else
            Log.Error($"[Editor] The update to v{result.To} failed: {result.Error} (details in ~/.mainframe/updates/update.log)");
    }

    private void FinishCheck(Task<UpdateCheckResult> check)
    {
        var result = check.IsCompletedSuccessfully
            ? check.Result
            : new UpdateCheckResult(UpdateCheckStatus.Failed, Error: check.Exception?.GetBaseException().Message);
        if (result.IsUpdate)
            Available = result;
        else if (result.Status == UpdateCheckStatus.Failed)
            Log.Info($"[Editor] Update check: {result.Error}");
        Changed?.Invoke();

        var manual = _manualCheck;
        _manualCheck = false;
        if (!manual)
            return;
        if (result.IsUpdate)
            workspace.UpdateDialog.Open();
        else
            workspace.Message.Show(new MessageRequest
            {
                Title = "Check for Updates",
                Message = result.Describe(CurrentVersion),
                Icon = result.Status == UpdateCheckStatus.Failed ? "alert-circle" : "refresh",
            });
    }

    private void BeginDownload(IUpdateService updates, UpdateCheckResult update)
    {
        Error = null;
        State = UpdateState.Downloading;
        _progress.Value = 0;
        _shownPercent = -1;
        _downloadCancel?.Dispose();
        _downloadCancel = new CancellationTokenSource();
        _download = updates.DownloadAsync(update, _progress, _downloadCancel.Token);
        Changed?.Invoke();
    }

    private void FinishDownload(Task<StagedUpdate> download)
    {
        _downloadCancel?.Dispose();
        _downloadCancel = null;
        if (download.IsCanceled || download.Exception?.GetBaseException() is OperationCanceledException)
        {
            State = UpdateState.Idle;
            Changed?.Invoke();
            return;
        }

        if (!download.IsCompletedSuccessfully)
        {
            var error = download.Exception!.GetBaseException();
            State = UpdateState.Failed;
            Error = error is UpdateException ? error.Message : $"The download failed ({error.Message}).";
            Changed?.Invoke();
            return;
        }

        Staged = download.Result;
        State = UpdateState.Ready;
        Changed?.Invoke();
        Primary(); // one click: Update & restart (or show the files)
    }

    private void Restart(IUpdateService updates, StagedUpdate staged)
    {
        workspace.UpdateDialog.Close();
        workspace.RequestQuit(() =>
        {
            workspace.Play.Stop();
            try
            {
                updates.StartApplier(staged, workspace.Session.ProjectRoot);
                return true;
            }
            catch (UpdateException e)
            {
                State = UpdateState.Failed;
                Error = e.Message;
                Staged = null;
                Changed?.Invoke();
                workspace.UpdateDialog.Open();
                return false;
            }
        });
    }

    public void Dispose()
    {
        _downloadCancel?.Cancel();
        _downloadCancel?.Dispose();
        _downloadCancel = null;
    }

    private sealed class ProgressSink : IProgress<double>
    {
        private double _value;

        public double Value
        {
            get => Volatile.Read(ref _value);
            set => Volatile.Write(ref _value, value);
        }

        public void Report(double value) => Value = value;
    }
}

using MainframeEngine.UI.Rml;

namespace MainframeEngine.Editor;

/// <summary>
/// Downloads the Demo project for this editor version into a chosen folder (<see cref="DemoDownloader"/>), then opens it
/// like New Project does. Validation (engine checkout, location) blocks Download; a failed download shows its message and
/// leaves Download enabled for a retry. Cancel stops a running download.
/// </summary>
public sealed class DownloadDemoDialog : EditorDocument
{
    /// <summary>The folder the demo is saved to, inside the chosen location.</summary>
    public const string FolderName = "MainframeEngine.Demo";

    private RmlDataModel? _model;
    private Task<string>? _download;
    private CancellationTokenSource? _cancel;
    private ProgressSink _progress = new();
    private string _location = NewProjectDialog.DefaultLocation;
    private string _engine = "";
    private string _error = ""; // validation: blocks Download
    private string _failure = ""; // the last download's failure: shown, Download stays enabled (retry)

    public DownloadDemoDialog(EditorWorkspace workspace)
        : base(workspace, "download_demo.rml")
    {
        Visible = false;
        Modal = true;
    }

    /// <summary>Tests: "" simulates no engine checkout; null uses <see cref="TemplateLocator.FindEngineCheckout"/>.</summary>
    public string? EngineCheckoutOverride { get; set; }

    public bool Busy => _download is not null;

    /// <summary>The validation error (empty when the download may start).</summary>
    public string Error => _error;

    /// <summary>The last download's failure message (empty when none).</summary>
    public string Failure => _failure;

    public string Location
    {
        get => _location;
        set
        {
            _location = value ?? "";
            if (IsInsideTree)
                CallDeferred(static state => ((DownloadDemoDialog)state!).Validate(), this);
            else
                Validate();
        }
    }

    protected override void OnReady() =>
        _model = CreateDataModel("download_demo")
            .Bind("location", this, static d => d._location, static (d, v) => d.Location = v)
            .Bind("version", this, static _ => EditorBrand.Version)
            .Bind("target", this, static d => Path.Combine(d._location.Trim(), FolderName))
            .Bind("engine", this, static d => d._engine.Length == 0 ? "—" : d._engine)
            .Bind("busy", this, static d => d.Busy)
            .Bind("bar", this, static d => $"{Math.Max(0, d._progress.Value) * 100:0}%")
            .Bind("status", this, static d => d._progress.Value < 0 ? "Downloading…" : $"Downloading… {d._progress.Value:P0}")
            .Bind("error", this, static d => d._error)
            .Bind("failure", this, static d => d._failure)
            .Event("browse", Browse)
            .Event("download", () => Start())
            .Event("cancel", Cancel);

    /// <summary>Shows the dialog for the engine checkout this editor runs from.</summary>
    public void Open()
    {
        _engine = EngineCheckoutOverride ?? TemplateLocator.FindEngineCheckout() ?? "";
        _failure = "";
        Visible = true;
        Validate();
    }

    /// <summary>Main thread, every frame while shown: progress and completion.</summary>
    public void Tick()
    {
        if (_download is not { } task)
            return;
        if (!task.IsCompleted)
        {
            _model?.Dirty("bar");
            _model?.Dirty("status");
            return;
        }

        _download = null;
        _cancel?.Dispose();
        _cancel = null;
        if (task.IsCompletedSuccessfully)
        {
            Log.Info($"[Editor] Downloaded the demo to {task.Result}.");
            Close();
            Workspace.Commands.OpenProject(task.Result);
            return;
        }

        var exception = task.Exception?.GetBaseException();
        _failure = task.IsCanceled || exception is OperationCanceledException ? ""
            : exception is DemoDownloadException ? exception.Message
            : "The demo download failed: " + exception?.Message;
        if (_failure.Length > 0)
            Log.Error($"[Editor] {_failure}");
        _model?.DirtyAll();
    }

    private void Validate()
    {
        var location = _location.Trim();
        // Like New Project, a missing folder is created by the download; anything else must be a usable parent.
        _error = _engine.Length == 0
            ? "No engine checkout was found (set MAINFRAME_ENGINE_PATH); the demo builds against the engine sources."
            : Path.IsPathFullyQualified(location) && !Directory.Exists(location) && !File.Exists(location)
                ? ""
                : NewProjectValidation.ValidateLocation(location, FolderName) ?? "";
        _model?.DirtyAll();
    }

    /// <summary>Starts the download when the fields validate and none is running.</summary>
    public bool Start()
    {
        Validate();
        if (Busy || _error.Length > 0)
            return false;
        _failure = "";
        _cancel = new CancellationTokenSource();
        _progress = new ProgressSink();
        var handler = Workspace.Options.DemoHttpHandler?.Invoke();
        var http = handler is null ? EditorHttp.Shared : new HttpClient(handler);
        var downloader = new DemoDownloader(http, EngineInfo.Version, Workspace.Options.DemoDownloadsDirectory ?? DemoDownloader.DefaultDownloadsDirectory);
        _download = downloader.DownloadAsync(new DemoDownloadRequest(_location.Trim(), FolderName, _engine), _progress, _cancel.Token);
        Log.Info($"[Editor] Downloading the demo to {Path.Combine(_location.Trim(), FolderName)}…");
        _model?.DirtyAll();
        return true;
    }

    /// <summary>Cancels a running download, or closes the dialog.</summary>
    public void Cancel()
    {
        if (Busy)
        {
            _cancel?.Cancel();
            return;
        }

        Close();
    }

    private void Close() => HideAndReleaseFocus();

    private void Browse()
    {
        var start = Directory.Exists(_location) ? _location : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Workspace.FilePicker.Show(new FilePickerModel(FilePickerMode.Folder, start, []), "Demo Location", "Choose", folder => Location = folder);
    }

    protected override void OnAttach(RmlDocument document) => document.AsElement().AddEventListener("keydown", e =>
    {
        if (Visible && (RmlKey)e.GetParameter("key_identifier", 0) == RmlKey.Escape)
            Cancel();
    });

    /// <summary>Progress written by the download thread, read by the UI thread.</summary>
    private sealed class ProgressSink : IProgress<double>
    {
        private double _value = -1;

        public double Value => Volatile.Read(ref _value);

        public void Report(double value) => Volatile.Write(ref _value, value);
    }
}

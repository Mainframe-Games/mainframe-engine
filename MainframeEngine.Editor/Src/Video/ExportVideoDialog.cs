using MainframeEngine.UI.Rml;
using MainframeEngine.Video.Media.Export;

namespace MainframeEngine.Editor.Video;

/// <summary>
/// File › Export Video: where to save the trimmed recording and how (Precise re-encodes, Fast copies). It only chooses; Export
/// hands the choice to <see cref="VideoView.StartExport"/>, which snapshots the document on the main thread and runs the job.
/// A typed output that already exists asks first (a picked one was confirmed by the picker); the recording itself and any
/// extension ffmpeg cannot write are refused with a message in the dialog.
/// </summary>
public sealed class ExportVideoDialog : EditorDocument
{
    private const string FastHint = "Fast needs .mp4, .mov or .mkv: WebM cannot hold the recording's codecs, so it is always re-encoded.";

    private VideoTab? _tab;
    private RmlDataModel? _model;
    private string _path = "";
    private string _summary = "";
    private string _error = "";
    private bool _fast;

    // The path the file picker already asked about replacing; exporting to exactly it does not ask again.
    private string? _confirmed;

    public ExportVideoDialog(EditorWorkspace workspace)
        : base(workspace, "video_export.rml")
    {
        Visible = false;
        Modal = true;
    }

    /// <summary>The tab being exported, or null while closed.</summary>
    public VideoTab? Tab => _tab;

    /// <summary>The output file as typed (resolved and checked by Export). Choosing <c>.webm</c> turns Fast off.</summary>
    public string OutputPath
    {
        get => _path;
        set
        {
            _path = value ?? "";
            if (_fast && !FastAllowed)
                _fast = false;
            _error = "";
            _model?.DirtyAll();
        }
    }

    /// <summary>Fast export (stream copy) instead of Precise; never true for a WebM output.</summary>
    public bool Fast
    {
        get => _fast;
        set
        {
            _fast = value && FastAllowed;
            _model?.Dirty("fast");
        }
    }

    /// <summary>False for a <c>.webm</c> output, where only Precise is possible.</summary>
    public bool FastAllowed => VideoExport.FastAllowed(_path);

    /// <summary>"Kept 4:12.3 of 6:00.0 · 23 cuts".</summary>
    public string Summary => _summary;

    /// <summary>Why Export was refused (empty when it was not).</summary>
    public string Error => _error;

    protected override void OnReady() =>
        _model = CreateDataModel("video_export")
            .Bind("summary", this, static d => d._summary)
            .Bind("path", this, static d => d._path, static (d, v) => d.OutputPath = v ?? "")
            .Bind("fast", this, static d => d._fast)
            .Bind("fast_allowed", this, static d => d.FastAllowed)
            .Bind("hint", this, static d => d.FastAllowed ? "" : FastHint)
            .Bind("error", this, static d => d._error)
            .Event("choose", e => Fast = e.GetArgument(0).GetInt32() == 1)
            .Event("browse", _ => Browse())
            .Event("export", _ => Export())
            .Event("cancel", _ => Cancel());

    protected override void OnAttach(RmlDocument document) => document.AsElement().AddEventListener("keydown", OnKeyDown);

    private void OnKeyDown(RmlEvent e)
    {
        // A picker or message box opened from here has the keys while it is up.
        if (!Visible || Workspace.FilePicker.Visible || Workspace.Message.Visible)
            return;
        switch ((RmlKey)e.GetParameter("key_identifier", 0))
        {
            case RmlKey.Escape:
                Cancel();
                break;
            case RmlKey.Return or RmlKey.NumpadEnter:
                Export();
                break;
        }
    }

    /// <summary>Opens the dialog for <paramref name="tab"/>: Precise, next to the recording, under a name nothing uses yet.</summary>
    public void Open(VideoTab tab)
    {
        _tab = tab ?? throw new ArgumentNullException(nameof(tab));
        _path = VideoExport.DefaultPath(tab.Document.SourcePath, File.Exists);
        _fast = false;
        _error = "";
        _confirmed = null;
        _summary = VideoExport.Summary(tab.Document);
        Visible = true;
        _model?.DirtyAll();
        if (EnsureLoaded() && Document.GetElementById("ev-path") is { IsNull: false } field)
            field.Focus(focusVisible: true);
    }

    /// <summary>The Cancel button and Escape.</summary>
    public void Cancel()
    {
        _tab = null;
        _confirmed = null;
        HideAndReleaseFocus();
    }

    /// <summary>Browse: a Save picker over the supported containers, starting at the current name.</summary>
    public void Browse()
    {
        if (_tab is null)
            return;
        var source = _tab.Document.SourcePath;
        var current = VideoExport.ResolveOutput(_path, source);
        var directory = current.Length > 0 && Path.GetDirectoryName(current) is { Length: > 0 } d && Directory.Exists(d) ? d : Path.GetDirectoryName(source)!;
        var name = current.Length > 0 ? Path.GetFileName(current) : Path.GetFileName(VideoExport.DefaultPath(source, File.Exists));
        var model = new FilePickerModel(FilePickerMode.Save, directory, ["*.mp4", "*.mov", "*.mkv", "*.webm"], name);
        Workspace.FilePicker.Show(model, "Export Video", "Export", picked =>
        {
            var resolved = VideoExport.ResolveOutput(picked, source);
            OutputPath = resolved;
            if (File.Exists(resolved))
                _confirmed = resolved; // the picker asked before it returned
        });
    }

    /// <summary>
    /// The Export button: checks the output, asks before replacing a typed file that exists, then closes and starts the
    /// job. A refused output keeps the dialog open with the reason in it.
    /// </summary>
    public void Export()
    {
        if (_tab is not { } tab)
            return;
        var source = tab.Document.SourcePath;
        var path = VideoExport.ResolveOutput(_path, source);
        var problem = VideoExport.Problem(source, path);
        if (problem is null && VideoExport.KeptSeconds(tab.Document) <= 0)
            problem = VideoExport.NothingToExport;
        if (problem is not null)
        {
            _error = problem;
            _model?.Dirty("error");
            return;
        }

        _path = path;
        _error = "";
        _model?.DirtyAll();
        var mode = _fast ? ExportMode.Fast : ExportMode.Precise;
        if (File.Exists(path) && !string.Equals(_confirmed, path, StringComparison.Ordinal))
        {
            Workspace.Message.Show(new MessageRequest
            {
                Title = "Replace file?",
                Message = $"{Path.GetFileName(path)} already exists. Replace it?",
                Buttons = ["Replace", "Cancel"],
                DefaultButton = 1,
                CancelButton = 1,
                Callback = (button, _) =>
                {
                    if (button == 0)
                        Start(tab, path, mode);
                },
            });
            return;
        }

        Start(tab, path, mode);
    }

    private void Start(VideoTab tab, string path, ExportMode mode)
    {
        Cancel(); // closes first: a failure to start is a message box, not an error line in a hidden dialog
        Workspace.VideoView.StartExport(tab, path, mode);
    }
}

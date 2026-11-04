using MainframeEngine.UI.Rml;
using MainframeEngine.Video.Timelines.Export;

namespace MainframeEngine.Editor.Video;

/// <summary>
/// File › Export Video (Cmd+E): a preset (socials, YouTube and Steam, chat, loops, the timeline's own), the range (the whole
/// timeline, or in to out) and the output (default <c>&lt;project&gt;/Exports/&lt;timeline&gt;-&lt;preset&gt;</c>). It only
/// chooses: Export hands the choice to <see cref="VideoView.StartExport"/>, which snapshots the timeline and runs the job. A
/// typed output that exists asks first; a source file, a folder and another format are refused in the dialog.
/// </summary>
public sealed class ExportVideoDialog : EditorDocument
{
    private sealed class PresetRow
    {
        public required int Index { get; init; }
        public required string Name { get; init; }
    }

    private static readonly RmlStructType<PresetRow> PresetType = new RmlStructType<PresetRow>()
        .Member("index", static r => r.Index)
        .Member("name", static r => r.Name);

    private readonly List<PresetRow> _presets = [.. TimelineExportPresets.All.Select((p, i) => new PresetRow { Index = i, Name = p.Name })];
    private TimelineTab? _tab;
    private RmlDataModel? _model;
    private string _path = "";
    private string _error = "";
    private int _preset;
    private bool _inOut;
    private string? _confirmed;

    public ExportVideoDialog(EditorWorkspace workspace)
        : base(workspace, "video_export.rml")
    {
        Visible = false;
        Modal = true;
    }

    public TimelineTab? Tab => _tab;

    public TimelineExportPreset Preset => TimelineExportPresets.All[_preset];

    /// <summary>The preset picked (an index into <see cref="TimelineExportPresets.All"/>); the default path follows it.</summary>
    public int PresetIndex
    {
        get => _preset;
        set
        {
            var index = Math.Clamp(value, 0, TimelineExportPresets.All.Count - 1);
            if (_tab is not null && _path == DefaultPath(_tab, Preset))
                _path = DefaultPath(_tab, TimelineExportPresets.All[index]);
            else if (_tab is not null)
                _path = Path.ChangeExtension(_path, TimelineExportPresets.All[index].Extension);
            _preset = index;
            _error = "";
            _model?.DirtyAll();
        }
    }

    /// <summary>In to out (only when the timeline has an in or out point) instead of the whole timeline.</summary>
    public bool InToOut
    {
        get => _inOut;
        set
        {
            _inOut = value && HasInOut;
            _model?.DirtyAll();
        }
    }

    public bool HasInOut => _tab?.Timeline is { } t && (t.InPoint is not null || t.OutPoint is not null);

    public string OutputPath
    {
        get => _path;
        set
        {
            _path = value ?? "";
            _error = "";
            _model?.DirtyAll();
        }
    }

    public string Error => _error;

    public string Summary => _tab is { } tab ? TimelineExport.Summary(tab.Timeline, Preset, _inOut) : "";

    protected override void OnReady() =>
        _model = CreateDataModel("video_export")
            .BindList("presets", _presets, PresetType)
            .Bind("preset", this, static d => d._preset)
            .Bind("in_out", this, static d => d._inOut)
            .Bind("has_in_out", this, static d => d.HasInOut)
            .Bind("summary", this, static d => d.Summary)
            .Bind("path", this, static d => d._path, static (d, v) => d.OutputPath = v ?? "")
            .Bind("error", this, static d => d._error)
            .Event("choose", e => PresetIndex = e.GetArgument(0).GetInt32())
            .Event("range", e => InToOut = e.GetArgument(0).GetInt32() == 1)
            .Event("browse", _ => Browse())
            .Event("export", _ => Export())
            .Event("cancel", _ => Cancel());

    protected override void OnAttach(RmlDocument document) => document.AsElement().AddEventListener("keydown", OnKeyDown);

    private void OnKeyDown(RmlEvent e)
    {
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

    /// <summary>Opens the dialog for <paramref name="tab"/>, on the preset used last (the first the first time).</summary>
    public void Open(TimelineTab tab)
    {
        _tab = tab ?? throw new ArgumentNullException(nameof(tab));
        _inOut = HasInOut;
        _path = DefaultPath(tab, Preset);
        _error = "";
        _confirmed = null;
        Visible = true;
        _model?.DirtyAll();
        if (EnsureLoaded() && Document.GetElementById("ev-path") is { IsNull: false } field)
            field.Focus(focusVisible: true);
    }

    public void Cancel()
    {
        _tab = null;
        _confirmed = null;
        HideAndReleaseFocus();
    }

    public void Browse()
    {
        if (_tab is not { } tab)
            return;
        var current = TimelineExport.ResolveOutput(_path, tab.Document.ProjectRoot, Preset);
        var directory = current.Length > 0 && Path.GetDirectoryName(current) is { Length: > 0 } d && Directory.Exists(d) ? d : tab.Document.ProjectRoot;
        var model = new FilePickerModel(FilePickerMode.Save, directory, ["*" + Preset.Extension], Path.GetFileName(current));
        Workspace.FilePicker.Show(model, "Export Video", "Export", picked =>
        {
            var resolved = TimelineExport.ResolveOutput(picked, tab.Document.ProjectRoot, Preset);
            OutputPath = resolved;
            if (File.Exists(resolved))
                _confirmed = resolved;
        });
    }

    public void Export()
    {
        if (_tab is not { } tab)
            return;
        var path = TimelineExport.ResolveOutput(_path, tab.Document.ProjectRoot, Preset);
        var problem = TimelineExport.Problem(tab.Timeline, tab.Document.Resolve, path, Preset);
        var (start, end) = TimelineExport.Range(tab.Timeline, _inOut);
        if (problem is null && end - start <= 0)
            problem = TimelineExport.NothingToExport;
        if (problem is not null)
        {
            _error = problem;
            _model?.Dirty("error");
            return;
        }

        _path = path;
        var preset = Preset;
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
                        Start(tab, preset, start, end, path);
                },
            });
            return;
        }

        Start(tab, preset, start, end, path);
    }

    private void Start(TimelineTab tab, TimelineExportPreset preset, double start, double end, string path)
    {
        Cancel();
        Workspace.VideoView.StartExport(tab, preset, start, end, path);
    }

    private static string DefaultPath(TimelineTab tab, TimelineExportPreset preset) =>
        TimelineExport.DefaultPath(tab.Document.ProjectRoot, tab.Document.FilePath, preset, File.Exists);
}

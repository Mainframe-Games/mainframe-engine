using System.Globalization;
using MainframeEngine.UI.Rml;

namespace MainframeEngine.Editor;

/// <summary>
/// The RmlUi file picker (open / save / folder; SDL2 has no native dialogs): a modal view over a
/// <see cref="FilePickerModel"/>, data-bound (<c>file_picker</c> model). Save asks before overwriting.
/// </summary>
public sealed class FilePickerDialog : EditorDocument
{
    private sealed class EntryRow
    {
        public required FileEntry Entry { get; init; }
        public required int Index { get; init; }
        public bool Selected { get; set; }
        public string Css => Entry.IsDirectory ? (Selected ? "list-item dir selected" : "list-item dir") : (Selected ? "list-item selected" : "list-item");
        public string Mark => Entry.IsDirectory ? "folder-mark" : "file-mark";
        public string Size => Entry.IsDirectory ? "" : FormatSize(Entry.Size);
    }

    private static readonly RmlStructType<EntryRow> EntryType = new RmlStructType<EntryRow>()
        .Member("name", static r => r.Entry.Name)
        .Member("dir", static r => r.Entry.IsDirectory)
        .Member("index", static r => r.Index)
        .Member("css", static r => r.Css)
        .Member("mark", static r => r.Mark)
        .Member("size", static r => r.Size);

    private readonly List<EntryRow> _rows = [];
    private RmlDataModel? _model;
    private Action<string>? _onAccept;
    private Action? _onCancel;
    private string _title = "Open";
    private string _okLabel = "Open";
    private string _path = "";
    private int _selected = -1;

    public FilePickerDialog(EditorWorkspace workspace)
        : base(workspace, "file_picker.rml")
    {
        Visible = false;
        Modal = true;
    }

    /// <summary>The model on screen (null when closed).</summary>
    public FilePickerModel? Model { get; private set; }

    protected override void OnReady()
    {
        _model = CreateDataModel("file_picker")
            .Bind("title", this, static d => d._title)
            .Bind("ok_label", this, static d => d._okLabel)
            .Bind("path", this, static d => d._path, static (d, v) => d._path = v ?? "")
            .Bind("file_name", this, static d => d.Model?.FileName ?? "", static (d, v) =>
            {
                if (d.Model is not null)
                    d.Model.FileName = v ?? "";
            })
            .Bind("name_label", this, static d => d.Model?.Mode == FilePickerMode.Folder ? "Folder" : "File name")
            .Bind("show_name", this, static d => d.Model is not null)
            .Bind("error", this, static d => d.Model?.Error ?? "")
            .Bind("filter", this, static d => d.Model is { Filter.Count: > 0 } m ? string.Join(", ", m.Filter) : "")
            .Bind("has_project", this, static d => d.Workspace.Session.ProjectRoot is not null)
            .BindList("entries", _rows, EntryType)
            .Event("up", _ => Up())
            .Event("go", _ => NavigateTo(_path))
            .Event("place", e => GoToPlace(e.GetArgument(0).GetInt32()))
            .Event("select", e => Select(e.GetArgument(0).GetInt32()))
            .Event("activate", e => Activate(e.GetArgument(0).GetInt32()))
            .Event("ok", _ => Accept())
            .Event("cancel", _ => Cancel());
    }

    /// <summary>Opens the dialog over <paramref name="model"/>; <paramref name="onAccept"/> gets the chosen absolute path.</summary>
    public void Show(FilePickerModel model, string title, string okLabel, Action<string> onAccept, Action? onCancel = null)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
        _title = title;
        _okLabel = okLabel;
        _onAccept = onAccept;
        _onCancel = onCancel;
        Visible = true;
        Refresh();
    }

    private void Refresh()
    {
        _rows.Clear();
        _selected = -1;
        if (Model is { } model)
        {
            _path = model.CurrentDirectory;
            for (var i = 0; i < model.Entries.Count; i++)
                _rows.Add(new EntryRow { Entry = model.Entries[i], Index = i });
        }

        _model?.DirtyAll();
    }

    public void Up()
    {
        Model?.Up();
        Refresh();
    }

    public void NavigateTo(string path)
    {
        if (Model is null)
            return;
        Model.Navigate(path);
        Refresh();
    }

    private void GoToPlace(int place)
    {
        var root = Workspace.Session.ProjectRoot;
        var target = place switch
        {
            1 when root is not null => root,
            2 when root is not null => Path.Combine(root, ContentPaths.FolderName),
            _ => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        NavigateTo(target);
    }

    /// <summary>Selects entry <paramref name="index"/> (fills the file name for files).</summary>
    public void Select(int index)
    {
        if (Model is null || (uint)index >= (uint)_rows.Count)
            return;
        if (_selected >= 0 && _selected < _rows.Count)
            _rows[_selected].Selected = false;
        _selected = index;
        _rows[index].Selected = true;
        Model.Select(_rows[index].Entry);
        _model?.DirtyAll();
    }

    /// <summary>Double click: enters folders, accepts files.</summary>
    public void Activate(int index)
    {
        if (Model is null || (uint)index >= (uint)_rows.Count)
            return;
        if (Model.Activate(_rows[index].Entry))
            Accept();
        else
            Refresh();
    }

    /// <summary>The OK button: validates, confirms overwriting, then closes and calls back.</summary>
    public void Accept()
    {
        if (Model is not { } model)
            return;
        if (!model.TryGetResult(out var path, out var exists))
        {
            _model?.Dirty("error");
            return;
        }

        if (model.Mode == FilePickerMode.Save && exists)
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
                        Finish(path);
                },
            });
            return;
        }

        Finish(path);
    }

    private void Finish(string path)
    {
        var callback = _onAccept;
        Close();
        callback?.Invoke(path);
    }

    public void Cancel()
    {
        var callback = _onCancel;
        Close();
        callback?.Invoke();
    }

    private void Close()
    {
        Visible = false;
        Model = null;
        _onAccept = null;
        _onCancel = null;
        _rows.Clear();
        _model?.DirtyAll();
    }

    protected override void OnAttach(RmlDocument document)
    {
        document.AsElement().AddEventListener("keydown", OnKeyDown);
    }

    private void OnKeyDown(RmlEvent e)
    {
        if (!Visible)
            return;
        var key = (RmlKey)e.GetParameter("key_identifier", 0);
        if (key == RmlKey.Escape)
        {
            Cancel();
        }
        else if (key is RmlKey.Return or RmlKey.NumpadEnter)
        {
            // Enter in the path field navigates; elsewhere it accepts.
            if (e.Target.Id == "path")
                NavigateTo(_path);
            else
                Accept();
        }
    }

    internal static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => (bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB",
        _ => (bytes / (1024.0 * 1024.0)).ToString("0.#", CultureInfo.InvariantCulture) + " MB",
    };
}

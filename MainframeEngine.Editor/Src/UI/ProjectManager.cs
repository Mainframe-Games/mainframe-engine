using System.Globalization;
using MainframeEngine.UI.Rml;

namespace MainframeEngine.Editor;

/// <summary>
/// The Project Manager (E4, Godot's project list): shown when the editor starts without a project, and from Project ›
/// Project Manager. Recent projects (icon, name, folder, last opened; a missing folder is flagged), a filter, New
/// Project (the <see cref="NewProjectDialog"/>), Open Project (a <c>project.mfproj</c>), Open and Remove from the list (the files stay), and the
/// .NET SDK check the project tools need.
/// </summary>
public sealed class ProjectManager : EditorDocument
{
    private sealed class Row
    {
        public required RecentProject Project { get; init; }
        public required int Index { get; init; }
        public required bool Valid { get; init; }
        public required string When { get; init; }
        public required string Folder { get; init; }
        public string Icon { get; init; } = "";
        public bool Selected { get; set; }
    }

    /// <summary>A listed project (Name, Path, and the absolute path of its icon PNG — "" when it has none), for tests and QA.</summary>
    public sealed record Entry(string Name, string Path, string Icon);

    private static readonly RmlStructType<Row> RowType = new RmlStructType<Row>()
        .Member("name", static r => r.Project.Name)
        .Member("folder", static r => r.Folder)
        .Member("when", static r => r.When)
        .Member("valid", static r => r.Valid)
        .Member("index", static r => r.Index)
        .Member("selected", static r => r.Selected)
        .Member("icon", static r => r.Icon)
        .Member("has_icon", static r => r.Icon.Length > 0);

    private readonly List<Row> _rows = [];
    private readonly ProjectIconResolver _icons = new();
    private RmlDataModel? _model;
    private string _query = "";
    private int _selected = -1;
    private string _sdkText = "Checking the .NET SDK…";
    private bool _sdkOk = true;
    private Task<DotnetSdkInfo>? _sdkCheck;

    public ProjectManager(EditorWorkspace workspace)
        : base(workspace, "project_manager.rml")
    {
        Visible = false;
        Modal = true;
    }

    /// <summary>The .NET SDK check result (null while checking).</summary>
    public DotnetSdkInfo? Sdk { get; private set; }

    /// <summary>The listed projects (after the filter), for tests and QA.</summary>
    public IReadOnlyList<Entry> VisibleProjects => _rows.Select(r => new Entry(r.Project.Name, r.Project.Path, r.Icon)).ToArray();

    protected override void OnReady()
    {
        _model = CreateDataModel("project_manager")
            .Bind("version", this, static _ => "Editor " + EditorBrand.Version)
            .Bind("sdk_text", this, static p => p._sdkText)
            .Bind("sdk_ok", this, static p => p._sdkOk)
            .Bind("has_selection", this, static p => p._selected >= 0)
            .Bind("count", this, static p => p._rows.Count)
            .Bind("query", this, static p => p._query, static (p, v) =>
            {
                p._query = v ?? "";
                if (p.IsInsideTree)
                    p.CallDeferred(static state => ((ProjectManager)state!).Rebuild(), p);
            })
            .BindList("projects", _rows, RowType)
            .Event("select", e => Select(e.GetArgument(0).GetInt32()))
            .Event("activate", e =>
            {
                Select(e.GetArgument(0).GetInt32());
                OpenSelected();
            })
            .Event("open", _ => OpenSelected())
            .Event("new", _ => NewProject())
            .Event("browse", _ => OpenProjectFile())
            .Event("download_demo", _ => Workspace.DownloadDemo.Open())
            .Event("remove", _ => RemoveSelected())
            .Event("download", _ => OpenDownloadPage());
    }

    /// <summary>Shows the manager (re-reads the recent list, checks the SDK once).</summary>
    public void Open()
    {
        Visible = true;
        _selected = -1;
        Rebuild();
        if (_rows.Count > 0)
            Select(0);
        if (_sdkCheck is null && Sdk is null)
            _sdkCheck = DotnetSdk.DetectAsync();
    }

    public void Close() => HideAndReleaseFocus();

    /// <summary>Main thread, every frame while shown: finishes the SDK check.</summary>
    public void Tick()
    {
        if (_sdkCheck is not { IsCompleted: true } check)
            return;
        _sdkCheck = null;
        Sdk = check.IsCompletedSuccessfully ? check.Result : new DotnetSdkInfo(null, [], null, false, check.Exception?.GetBaseException().Message);
        _sdkOk = Sdk.IsSupported;
        _sdkText = Sdk.IsSupported
            ? $".NET SDK {Sdk.BestVersion} — ready to create, build and play projects."
            : Sdk.Error ?? $"The .NET {DotnetSdk.RequiredMajor} SDK is required. Install it from {DotnetSdk.DownloadUrl}";
        _model?.DirtyAll();
    }

    /// <summary>Sets the SDK check's result directly (the golden capture: no machine-specific version).</summary>
    public void SetSdk(DotnetSdkInfo info)
    {
        _sdkCheck = null;
        Sdk = info ?? throw new ArgumentNullException(nameof(info));
        _sdkOk = info.IsSupported;
        _sdkText = info.IsSupported ? $".NET SDK {info.BestVersion} — ready to create, build and play projects." : info.Error ?? "";
        _model?.DirtyAll();
    }

    /// <summary>Re-reads the recent list through the filter.</summary>
    public void Rebuild()
    {
        var previous = (uint)_selected < (uint)_rows.Count ? _rows[_selected].Project.Path : null;
        _rows.Clear();
        _selected = -1;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var iconsChanged = false;
        foreach (var project in Workspace.RecentProjects.Items)
        {
            if (_query.Length > 0 && !project.Name.Contains(_query, StringComparison.OrdinalIgnoreCase) &&
                !project.Path.Contains(_query, StringComparison.OrdinalIgnoreCase))
                continue;
            var folder = project.Path.StartsWith(home, StringComparison.Ordinal) ? "~" + project.Path[home.Length..] : project.Path;
            var valid = RecentProjects.IsValid(project);
            var icon = valid ? _icons.Resolve(project.Path) : default;
            iconsChanged |= icon.Changed;
            _rows.Add(new Row
            {
                Project = project,
                Index = _rows.Count,
                Valid = valid,
                Icon = icon.Path ?? "",
                When = Ago(project.LastOpenedUtc),
                Folder = folder,
            });
            if (string.Equals(project.Path, previous, StringComparison.Ordinal))
            {
                _selected = _rows.Count - 1;
                _rows[^1].Selected = true;
            }
        }

        // RmlUi caches textures by source: an icon that changed on disk is re-read after the release (the Project Manager is the
        // only document using them while it is open).
        if (iconsChanged)
            RmlCore.ReleaseTextures();
        _model?.DirtyAll();
    }

    /// <summary>"just now", "5 min ago", "3 h ago", "2 days ago", else the date.</summary>
    public static string Ago(DateTime utc)
    {
        var span = DateTime.UtcNow - utc;
        if (span.TotalMinutes < 1)
            return "just now";
        if (span.TotalHours < 1)
            return $"{(int)span.TotalMinutes} min ago";
        if (span.TotalDays < 1)
            return $"{(int)span.TotalHours} h ago";
        if (span.TotalDays < 30)
            return $"{(int)span.TotalDays} day{((int)span.TotalDays == 1 ? "" : "s")} ago";
        return utc.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture);
    }

    public void Select(int index)
    {
        if ((uint)index >= (uint)_rows.Count)
            return;
        if ((uint)_selected < (uint)_rows.Count)
            _rows[_selected].Selected = false;
        _selected = index;
        _rows[index].Selected = true;
        _model?.DirtyAll();
    }

    /// <summary>Selects the project in <paramref name="folder"/> (tests, QA).</summary>
    public bool Select(string folder)
    {
        var index = _rows.FindIndex(r => EditorSession.PathsEqual(r.Project.Path, folder));
        Select(index);
        return index >= 0;
    }

    /// <summary>Opens the selected project (a missing one offers to remove it from the list).</summary>
    public void OpenSelected()
    {
        if ((uint)_selected >= (uint)_rows.Count)
            return;
        var row = _rows[_selected];
        if (!row.Valid)
        {
            Workspace.Message.Show(new MessageRequest
            {
                Title = "Project Missing",
                Message = $"{row.Project.Name} is no longer at {row.Project.Path} (no {ProjectSettings.FileName}). Remove it from the list?",
                Buttons = ["Remove", "Cancel"],
                DefaultButton = 0,
                CancelButton = 1,
                Callback = (button, _) =>
                {
                    if (button == 0)
                        RemoveSelected();
                },
            });
            return;
        }

        Workspace.Commands.OpenProject(row.Project.Path);
    }

    public void RemoveSelected()
    {
        if ((uint)_selected >= (uint)_rows.Count)
            return;
        Workspace.RecentProjects.Remove(_rows[_selected].Project.Path);
        Rebuild();
        if (_rows.Count > 0)
            Select(0);
    }

    private void NewProject() => Workspace.NewProject.Open();

    private void OpenProjectFile()
    {
        var start = Workspace.RecentProjects.Items.Count > 0 && Path.GetDirectoryName(Workspace.RecentProjects.Items[0].Path) is { } parent && Directory.Exists(parent)
            ? parent
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Workspace.Commands.ChooseProjectFile(start);
    }

    private static void OpenDownloadPage()
    {
        try
        {
            using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(DotnetSdk.DownloadUrl) { UseShellExecute = true });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Warning($"[Editor] Open {DotnetSdk.DownloadUrl} in a browser ({e.Message}).");
        }
    }

    private ReleaseVersion? _badgeVersion;

    /// <summary>True while the update badge is shown (a newer release is available).</summary>
    public bool UpdateBadgeVisible { get; private set; }

    /// <summary>Shows or hides the update badge (on <see cref="UpdateController.Changed"/>, never per frame).</summary>
    public void RefreshUpdate()
    {
        var release = Workspace.Updates?.Available?.Release;
        UpdateBadgeVisible = release is not null;
        if (!IsLoaded)
            return;
        var badge = Document.GetElementById("pm-update");
        badge.SetClass("shown", UpdateBadgeVisible);
        if (release is not null && _badgeVersion != release.Version)
        {
            _badgeVersion = release.Version;
            badge.SetAttribute("data-tooltip", $"Mainframe Engine v{release.Version} is available — click for the release notes and Update and restart");
        }
    }

    protected override void OnAttach(RmlDocument document)
    {
        _badgeVersion = null;
        document.AsElement().AddEventListener("keydown", e =>
        {
            if (!Visible)
                return;
            switch ((RmlKey)e.GetParameter("key_identifier", 0))
            {
                case RmlKey.Return or RmlKey.NumpadEnter:
                    OpenSelected();
                    break;
                case RmlKey.Down:
                    Select(Math.Min(_rows.Count - 1, _selected + 1));
                    break;
                case RmlKey.Up:
                    Select(Math.Max(0, _selected - 1));
                    break;
            }
        });
        RefreshUpdate();
    }
}

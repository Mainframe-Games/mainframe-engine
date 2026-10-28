using MainframeEngine.UI.Rml;

namespace MainframeEngine.Editor;

/// <summary>
/// The centre panel: one tab per open tab of the current <see cref="EditorMode"/> (title with <c>*</c> while dirty, close
/// button, + for a new scene, or to open a recording in video mode) above the view. For a scene the view is an <c>&lt;img&gt;</c> showing its offscreen render target through
/// <c>engine://</c>; it lets the mouse through to the <see cref="ViewportController"/>. For a UI preview a bar lists the
/// document's linked files (click: open in the code editor) and the backdrop controls, and the document draws over the
/// view from its own layer (<see cref="UiPreview"/>) — over a neutral backdrop, or a scene's render.
/// </summary>
public sealed class ViewportPanel : EditorDocument
{
    /// <summary>The <c>engine://</c> name the active scene's colour target is published under.</summary>
    public const string TextureName = "editor-viewport";

    private sealed class Tab
    {
        public required IEditorTab Item { get; init; }
        public required int Index { get; init; }
        public required string Title { get; init; }
        public required bool Active { get; init; }
        public required string Icon { get; init; }
        public required string Tooltip { get; init; }
    }

    private sealed class Link
    {
        public required int Index { get; init; }
        public required string Name { get; init; }
        public required string Via { get; init; }
        public required string Icon { get; init; }
        public required string Tooltip { get; init; }
        public required bool Missing { get; init; }
        public required bool Inline { get; init; }
        public string? Path { get; init; }
    }

    private static readonly RmlStructType<Tab> TabType = new RmlStructType<Tab>()
        .Member("title", static t => t.Title)
        .Member("active", static t => t.Active)
        .Member("index", static t => t.Index)
        .Member("icon", static t => t.Icon)
        .Member("tooltip", static t => t.Tooltip);

    private static readonly RmlStructType<Link> LinkType = new RmlStructType<Link>()
        .Member("index", static l => l.Index)
        .Member("name", static l => l.Name)
        .Member("via", static l => l.Via)
        .Member("icon", static l => l.Icon)
        .Member("tooltip", static l => l.Tooltip)
        .Member("missing", static l => l.Missing)
        .Member("inline", static l => l.Inline);

    private readonly List<Tab> _tabs = [];
    private readonly List<IEditorTab> _visibleTabs = [];
    private readonly List<Link> _links = [];
    private RmlDataModel? _model;
    private bool _imageShown;
    private string? _info;
    private UiPreview? _shownPreview;
    private int _shownPreviewVersion = -1;
    private bool _preview;
    private bool _backdropOn;
    private string _backdropName = "";
    private string _backdropTooltip = "";
    private string _newTooltip = NewSceneTooltip;

    private const string NewSceneTooltip = "New Scene (Ctrl+N) — open an empty scene in a new tab";
    private const string OpenRecordingTooltip = "Open a recording (Ctrl+O)";
    private const string NewSongTooltip = "New Song (Ctrl+N) — create a song in the project's Assets/Music";

    public ViewportPanel(EditorWorkspace workspace)
        : base(workspace, "viewport.rml")
    {
    }

    protected override void OnReady()
    {
        _model = CreateDataModel("viewport_tabs")
            .BindList("tabs", _tabs, TabType)
            .BindList("links", _links, LinkType)
            .Bind("preview", this, static p => p._preview)
            .Bind("backdrop_on", this, static p => p._backdropOn)
            .Bind("backdrop_name", this, static p => p._backdropName)
            .Bind("backdrop_tooltip", this, static p => p._backdropTooltip)
            .Bind("new_tooltip", this, static p => p._newTooltip)
            .Event("activate", e =>
            {
                if (TabAt(e.GetArgument(0).GetInt32()) is { } tab)
                    Workspace.Session.Activate(tab);
            })
            .Event("close", e =>
            {
                e.Event.StopPropagation();
                CloseAt(e.GetArgument(0).GetInt32());
            })
            .Event("create", _ => Workspace.Commands.Execute(Workspace.Mode switch
            {
                EditorMode.Video => "file.open",
                EditorMode.Music => "file.new_song",
                _ => "file.new",
            }))
            .Event("open_link", e => OpenLink(e.GetArgument(0).GetInt32()))
            .Event("toggle_backdrop", _ => ToggleBackdrop())
            .Event("pick_backdrop", _ => PickBackdrop())
            .Event("reload_preview", _ => Workspace.Session.ActivePreview?.Reload())
            .Event("edit_document", _ =>
            {
                if (Workspace.Session.ActivePreview is { } preview)
                    Workspace.CodeEditor.Open(preview.FilePath);
            });
        RefreshTabs();
    }

    protected override void OnAttach(RmlDocument document)
    {
        _paintVersion = -1;
        _paintShown = false;
        _imageShown = false;
        _info = null;
        _previewSize = null;
        _shownPreviewVersion = -1;
        UpdateImage();
    }

    private IEditorTab? TabAt(int index) => (uint)index < (uint)_tabs.Count ? _tabs[index].Item : null;

    /// <summary>The tabs the strip shows: the session's tabs of the current mode, in tab order.</summary>
    public IReadOnlyList<IEditorTab> VisibleTabs => _visibleTabs;

    /// <summary>Closes the tab at <paramref name="index"/> of the strip (<see cref="VisibleTabs"/>), asking about unsaved changes.</summary>
    internal void CloseAt(int index) => Workspace.Commands.CloseTab(TabAt(index));

    /// <summary>Rebuilds the tab strip (the current mode's tabs, titles, active tab) and the preview bar.</summary>
    public void RefreshTabs()
    {
        _tabs.Clear();
        _visibleTabs.Clear();
        var session = Workspace.Session;
        var mode = Workspace.Mode;
        foreach (var tab in session.Tabs)
        {
            if (tab.Mode != mode)
                continue;
            _tabs.Add(new Tab
            {
                Item = tab,
                Index = _tabs.Count, // the strip position: what the activate/close events pass back
                Title = tab.Title,
                Active = ReferenceEquals(tab, session.ActiveTab),
                Icon = tab.IconClasses,
                Tooltip = tab.Tooltip,
            });
            _visibleTabs.Add(tab);
        }

        var newTooltip = mode switch
        {
            EditorMode.Video => OpenRecordingTooltip,
            EditorMode.Music => NewSongTooltip,
            _ => NewSceneTooltip,
        };
        if (!ReferenceEquals(newTooltip, _newTooltip))
        {
            _newTooltip = newTooltip;
            _model?.Dirty("new_tooltip");
        }

        _model?.Dirty("tabs");
        _shownPreviewVersion = -1;
        Tick();
        UpdateImage();
    }

    private int _paintVersion = -1;
    private bool _paintShown;

    // G2b: the paint bar shows while a tile map layer is edited; rebuilt only when the tile tools' state changes.
    private void TickPaintBar()
    {
        if (!IsLoaded || Workspace.TileMap is not { } tiles)
            return;
        var shown = tiles.IsActive;
        if (shown == _paintShown && (!shown || tiles.Version == _paintVersion))
            return;
        _paintShown = shown;
        _paintVersion = tiles.Version;
        var bar = Document.GetElementById("paint-bar");
        if (bar.IsNull)
            return;
        bar.SetClass("shown", shown);
        Document.GetElementById("view-info").SetClass("below-bar", shown);
        bar.SetInnerRml(shown ? tiles.PaintBarRml() : "");
    }

    /// <summary>Every frame: refreshes the preview bar when the active preview changed (links re-read, backdrop).</summary>
    public void Tick()
    {
        TickPaintBar();
        var preview = Workspace.Session.ActivePreview;
        if (ReferenceEquals(preview, _shownPreview) && (preview is null || preview.Version == _shownPreviewVersion))
            return;
        _shownPreview = preview;
        _shownPreviewVersion = preview?.Version ?? -1;
        _preview = preview is not null;
        _links.Clear();
        if (preview is not null)
            FillLinks(preview);
        _backdropOn = preview is { ShowBackdrop: true, BackdropScene: not null };
        _backdropName = preview?.BackdropScene?.DisplayName ?? "No scene";
        _backdropTooltip = _backdropOn
            ? $"Scene Behind UI — on: {_backdropName} renders behind the document; click for the neutral backdrop"
            : "Scene Behind UI — off: the document shows over a neutral backdrop; click to render a scene behind it";
        if (_model is { } model)
        {
            model.Dirty("links");
            model.Dirty("preview");
            model.Dirty("backdrop_on");
            model.Dirty("backdrop_name");
            model.Dirty("backdrop_tooltip");
        }

        if (IsLoaded && Document.GetElementById("view") is { IsNull: false } view)
            view.SetClass("preview", _preview);
        UpdateImage();
    }

    private void FillLinks(UiPreview preview)
    {
        _links.Add(new Link
        {
            Index = 0,
            Name = preview.DisplayName,
            Via = "",
            Icon = "icon icon-file-type-html icon-ui",
            Tooltip = $"{preview.DisplayName} — the document; click to open it in the code editor — {preview.FilePath}",
            Missing = !File.Exists(preview.FilePath),
            Inline = false,
            Path = preview.FilePath,
        });
        foreach (var link in preview.Links.Links)
        {
            var name = Path.GetFileName(link.Href.TrimEnd('/'));
            var what = link.Kind == RmlLinkKind.Template ? "template" : "style sheet";
            _links.Add(new Link
            {
                Index = _links.Count,
                Name = name,
                Via = link.Via is { } via ? "via " + Path.GetFileName(via) : "",
                Icon = link.Kind == RmlLinkKind.Template ? "icon icon-file-type-html icon-ui" : "icon icon-file-type-css icon-ui",
                Tooltip = link.FullPath is { } full
                    ? $"{name} — linked {what} (href=\"{link.Href}\"); click to open it in the code editor — {full}"
                    : $"{name} — linked {what} not found (href=\"{link.Href}\")",
                Missing = !link.Exists,
                Inline = false,
                Path = link.FullPath,
            });
        }

        if (preview.Links.HasInlineStyle)
            _links.Add(new Link
            {
                Index = _links.Count,
                Name = "inline <style>",
                Via = "",
                Icon = "icon icon-brush",
                Tooltip = "The document has <style> blocks of its own; they apply after the linked style sheets",
                Missing = false,
                Inline = true,
            });

        if (preview.Data.Models.Count > 0)
            _links.Add(new Link
            {
                Index = _links.Count,
                Name = "stand-in data",
                Via = "",
                Icon = "icon icon-braces",
                Tooltip = $"Data models ({string.Join(", ", preview.Data.Models.Select(m => m.Name))}) are created by game code, so the preview " +
                          $"binds stand-ins: text shows each variable's name, conditions are true, numbers 0, lists have {UiPreviewData.ListSize} " +
                          "rows, events do nothing; inputs keep what you type",
                Missing = false,
                Inline = true,
            });
    }

    private void OpenLink(int index)
    {
        if ((uint)index < (uint)_links.Count && _links[index].Path is { } path && File.Exists(path))
            Workspace.CodeEditor.Open(path);
    }

    private void ToggleBackdrop()
    {
        if (Workspace.Session.ActivePreview is not { } preview)
            return;
        if (preview is { ShowBackdrop: true, BackdropScene: not null })
        {
            Workspace.Session.SetBackdrop(preview, preview.BackdropScene, show: false);
            return;
        }

        var scene = preview.BackdropScene ?? Workspace.Session.LastActiveScene ??
                    (Workspace.Session.Scenes.Count > 0 ? Workspace.Session.Scenes[0] : null);
        if (scene is null)
        {
            PickBackdrop();
            return;
        }

        Workspace.Session.SetBackdrop(preview, scene, show: true);
    }

    private void PickBackdrop()
    {
        if (Workspace.Session.ActivePreview is not { } preview)
            return;
        var scenes = Workspace.Session.Scenes;
        if (scenes.Count == 0)
        {
            Workspace.Message.Show(new MessageRequest
            {
                Title = "Scene Behind UI",
                Message = "Open a scene (in another tab) to render it behind the UI.",
                Buttons = ["OK"],
            });
            return;
        }

        var items = scenes
            .Select(s => new ListPickerItem(s.DisplayName, s.FilePath is { } path ? Workspace.Session.DisplayPath(path) : "not saved yet",
                EditorIcons.Classes(s.Root), s))
            .ToArray();
        Workspace.ListPicker.Show("Scene Behind UI", items, "Show", payload =>
        {
            if (payload is EditedScene scene && Workspace.Session.Scenes.Contains(scene) && Workspace.Session.Tabs.Contains(preview))
                Workspace.Session.SetBackdrop(preview, scene, show: true);
        });
    }

    /// <summary>
    /// Shows the view's image once the shown scene's target exists (<paramref name="available"/>): the active scene's, or
    /// a preview's backdrop scene. Otherwise the empty-state text, or nothing behind a preview.
    /// </summary>
    public void UpdateImage(bool? available = null)
    {
        if (!IsLoaded)
            return;
        var session = Workspace.Session;
        var backdrop = session.ActivePreview is { ShowBackdrop: true, BackdropScene: not null };
        var show = available ?? (_imageShown && (session.Active is not null || backdrop));
        var image = Document.GetElementById("view-image");
        var empty = Document.GetElementById("view-empty");
        if (!empty.IsNull)
            empty.SetProperty("display", session.ActiveTab is null ? "block" : "none");
        if (Document.GetElementById("view-info") is { IsNull: false } info)
            info.SetProperty("display", session.Active is null ? "none" : "flex"); // a preview shows its size in its bar
        if (show == _imageShown || image.IsNull)
            return;
        _imageShown = show;
        if (show)
            image.SetAttribute("src", "engine://" + TextureName);
        else
            image.RemoveAttribute("src");
        image.SetProperty("visibility", show ? "visible" : "hidden");
    }

    private string? _previewSize;

    /// <summary>The preview bar's size label (e.g. <c>1280×720</c>); only touches the DOM when it changed.</summary>
    public void SetPreviewSize(string size)
    {
        if (string.Equals(size, _previewSize, StringComparison.Ordinal))
            return;
        _previewSize = size;
        SetText("preview-size", size);
    }

    private string? _handleLabel;

    /// <summary>The text shown next to the mouse over a range handle (G7.3), or null when none is shown.</summary>
    public string? HandleLabel => _handleLabel;

    /// <summary>
    /// Shows <paramref name="text"/> next to the mouse at (<paramref name="x"/>, <paramref name="y"/>) dp from the view's
    /// top-left corner, or hides the label (null). Called on mouse moves over handles, not per frame.
    /// </summary>
    public void SetHandleLabel(string? text, float x, float y)
    {
        if (!IsLoaded || Document.GetElementById("handle-label") is not { IsNull: false } label)
            return;
        if (!string.Equals(text, _handleLabel, StringComparison.Ordinal))
        {
            _handleLabel = text;
            label.SetClass("shown", text is not null);
            if (text is not null)
                label.SetInnerRml(RmlText.Escape(text));
        }

        if (text is null)
            return;
        label.SetProperty("left", RmlText.Dp(x));
        label.SetProperty("top", RmlText.Dp(y));
    }

    private LayoutRect? _boxSelect;

    /// <summary>The box-selection rectangle in dp from the view's top-left corner (G7.4), or null to hide it.</summary>
    public void SetBoxSelect(LayoutRect? rect)
    {
        if (rect == _boxSelect || !IsLoaded || Document.GetElementById("box-select") is not { IsNull: false } box)
            return;
        _boxSelect = rect;
        box.SetClass("shown", rect is not null);
        if (rect is not { } r)
            return;
        box.SetProperty("left", RmlText.Dp(r.X));
        box.SetProperty("top", RmlText.Dp(r.Y));
        box.SetProperty("width", RmlText.Dp(r.Width));
        box.SetProperty("height", RmlText.Dp(r.Height));
    }

    /// <summary>The corner label (view name, camera mode) after its icon; only touches the DOM when it changed.</summary>
    public void SetInfo(string info, string icon = "perspective")
    {
        ArgumentNullException.ThrowIfNull(icon);
        if (string.Equals(info, _info, StringComparison.Ordinal))
            return;
        _info = info;
        SetText("view-info-text", info);
        if (IsLoaded && Document.GetElementById("view-info-icon") is { IsNull: false } element)
            element.SetClassNames(icon switch
            {
                "plane" => "icon icon-sm icon-plane",
                "square" => "icon icon-sm icon-square",
                _ => "icon icon-sm icon-perspective",
            });
    }
}

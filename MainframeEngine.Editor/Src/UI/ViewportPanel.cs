using MainframeEngine.UI.Rml;

namespace MainframeEngine.Editor;

/// <summary>
/// The centre panel: one tab per open scene (title with <c>*</c> while dirty, close button, + for a new scene) above
/// the 3D view — an <c>&lt;img&gt;</c> showing the active scene's offscreen render target through <c>engine://</c>. The
/// view lets the mouse through to the <see cref="ViewportController"/>.
/// </summary>
public sealed class ViewportPanel : EditorDocument
{
    /// <summary>The <c>engine://</c> name the active scene's colour target is published under.</summary>
    public const string TextureName = "editor-viewport";

    private sealed class Tab
    {
        public required EditedScene Scene { get; init; }
        public required int Index { get; init; }
        public required string Title { get; init; }
        public required bool Active { get; init; }
        public required string Icon { get; init; }
        public required string Tooltip { get; init; }
    }

    private static readonly RmlStructType<Tab> TabType = new RmlStructType<Tab>()
        .Member("title", static t => t.Title)
        .Member("active", static t => t.Active)
        .Member("index", static t => t.Index)
        .Member("icon", static t => t.Icon)
        .Member("tooltip", static t => t.Tooltip);

    private readonly List<Tab> _tabs = [];
    private RmlDataModel? _model;
    private bool _imageShown;
    private string? _info;

    public ViewportPanel(EditorWorkspace workspace)
        : base(workspace, "viewport.rml")
    {
    }

    protected override void OnReady()
    {
        _model = CreateDataModel("viewport_tabs")
            .BindList("tabs", _tabs, TabType)
            .Event("activate", e =>
            {
                if (TabAt(e.GetArgument(0).GetInt32()) is { } scene)
                    Workspace.Session.Activate(scene);
            })
            .Event("close", e =>
            {
                e.Event.StopPropagation();
                Workspace.Commands.CloseScene(TabAt(e.GetArgument(0).GetInt32()));
            })
            .Event("create", _ => Workspace.Commands.Execute("file.new"));
        RefreshTabs();
    }

    protected override void OnAttach(RmlDocument document)
    {
        _imageShown = false;
        _info = null;
        UpdateImage();
    }

    private EditedScene? TabAt(int index) => (uint)index < (uint)_tabs.Count ? _tabs[index].Scene : null;

    /// <summary>Rebuilds the tab strip (titles, active tab).</summary>
    public void RefreshTabs()
    {
        _tabs.Clear();
        var session = Workspace.Session;
        for (var i = 0; i < session.Scenes.Count; i++)
        {
            var scene = session.Scenes[i];
            _tabs.Add(new Tab
            {
                Scene = scene,
                Index = i,
                Title = scene.Title,
                Active = ReferenceEquals(scene, session.Active),
                // The scene's icon is its root node's (tinted by family), like Godot's scene tabs.
                Icon = EditorIcons.Classes(scene.Root),
                Tooltip = scene.FilePath is { } path
                    ? scene.DisplayName + (scene.IsDirty ? " (unsaved changes)" : "") + " — " + path
                    : scene.DisplayName + " — not saved yet",
            });
        }

        _model?.Dirty("tabs");
        UpdateImage();
    }

    /// <summary>
    /// Shows the 3D view once the active scene's target exists (<paramref name="available"/>), or the empty-state text.
    /// The texture itself is registered by the <see cref="ViewportController"/>.
    /// </summary>
    public void UpdateImage(bool? available = null)
    {
        if (!IsLoaded)
            return;
        var show = available ?? (_imageShown && Workspace.Session.Active is not null);
        var image = Document.GetElementById("view-image");
        var empty = Document.GetElementById("view-empty");
        if (!empty.IsNull)
            empty.SetProperty("display", Workspace.Session.Active is null ? "block" : "none");
        if (show == _imageShown || image.IsNull)
            return;
        _imageShown = show;
        if (show)
            image.SetAttribute("src", "engine://" + TextureName);
        else
            image.RemoveAttribute("src");
        image.SetProperty("visibility", show ? "visible" : "hidden");
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
            element.SetClassNames(icon == "plane" ? "icon icon-sm icon-plane" : "icon icon-sm icon-perspective");
    }
}

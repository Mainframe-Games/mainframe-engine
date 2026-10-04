using System.Globalization;
using MainframeEngine.UI.Rml;

namespace MainframeEngine.Editor;

/// <summary>The menu bar (File, Edit, View, Help): a click opens the menu's <see cref="PopupMenu"/>.</summary>
public sealed class MenuBarPanel : EditorDocument
{
    public MenuBarPanel(EditorWorkspace workspace)
        : base(workspace, "menubar.rml")
    {
    }

    /// <summary>Opens menu <paramref name="menu"/> (file, edit, view, help) under its title.</summary>
    public void OpenMenu(string menu)
    {
        var x = 80f;
        if (EnsureLoaded() && Document.GetElementById($"menu-{menu}") is { IsNull: false } title)
        {
            title.SetClass("open", true);
            x = title.Bounds.X / MathF.Max(0.01f, Workspace.Host.PixelScale);
        }

        Workspace.Popup.Show(Workspace.Commands.MenuItems(menu), x, EditorLayout.MenuBarHeight, onClose: () => CloseMenus());
    }

    private void CloseMenus()
    {
        if (!IsLoaded)
            return;
        foreach (var name in (ReadOnlySpan<string>)["file", "edit", "view", "help"])
            Document.GetElementById($"menu-{name}").SetClass("open", false);
    }

    private string _windowTitle = "";

    /// <summary>The scene name shown on the right of the bar, with a dirty marker.</summary>
    public void SetWindowTitle(string text, bool dirty)
    {
        _windowTitle = dirty ? text + "  •  unsaved" : text;
        SetText("window-title", _windowTitle);
    }

    protected override void OnAttach(RmlDocument document) => SetText("window-title", _windowTitle);

    protected override void OnClickElement(RmlEvent e)
    {
        if (FindAttribute(e.Target, "data-menu") is { } menu)
            OpenMenu(menu);
    }
}

/// <summary>The toolbar: gizmo mode, local/global, snapping, frame, grid; play controls (disabled until E4); frame stats.</summary>
public sealed class ToolbarPanel : EditorDocument
{
    private RmlEventListener? _snapListener;

    public ToolbarPanel(EditorWorkspace workspace)
        : base(workspace, "toolbar.rml")
    {
    }

    protected override void OnAttach(RmlDocument document)
    {
        _snapListener?.Remove();
        _snapListener = document.GetElementById("snap-step").AddEventListener("change", OnSnapStep);
        Refresh();
    }

    private void OnSnapStep(RmlEvent e)
    {
        if (ValueText.TryParseFloat(e.Value, out var step) && step > 0)
            Workspace.Gizmo.Snap = Workspace.Gizmo.Snap with { Translate = step };
    }

    /// <summary>Re-applies the active states (mode, space, snap, grid).</summary>
    public void Refresh()
    {
        if (!IsLoaded)
            return;
        var gizmo = Workspace.Gizmo;
        var document = Document;
        document.GetElementById("tool-select").SetClass("active", gizmo.Mode == GizmoMode.Select);
        document.GetElementById("tool-translate").SetClass("active", gizmo.Mode == GizmoMode.Translate);
        document.GetElementById("tool-rotate").SetClass("active", gizmo.Mode == GizmoMode.Rotate);
        document.GetElementById("tool-scale").SetClass("active", gizmo.Mode == GizmoMode.Scale);
        var local = document.GetElementById("tool-local");
        local.SetClass("active", gizmo.Local);
        local.SetInnerRml(gizmo.Local ? "Local<span class=\"key\">T</span>" : "Global<span class=\"key\">T</span>");
        document.GetElementById("tool-snap").SetClass("active", gizmo.Snap.Enabled);
        document.GetElementById("tool-grid").SetClass("active", Workspace.Viewport?.GridVisible ?? true);
    }

    /// <summary>Writes the frame stats without allocating (stack-formatted span into the element).</summary>
    public void UpdateStats(float fps, float ms)
    {
        if (!IsLoaded || !Workspace.Options.ShowFrameStats)
            return;
        var element = Document.GetElementById("stats");
        if (element.IsNull)
            return;
        Span<char> text = stackalloc char[48];
        // Integers only: custom float format strings allocate in the runtime's number formatting.
        var tenths = (int)MathF.Round(ms * 10f);
        if (text.TryWrite(CultureInfo.InvariantCulture, $"{(int)MathF.Round(fps)} fps  {tenths / 10}.{tenths % 10} ms", out var written))
            element.SetInnerRml(text[..written]);
    }
}

/// <summary>The FileSystem dock: a placeholder until projects arrive (E4); shows the project folder.</summary>
public sealed class FileSystemPanel : EditorDocument
{
    private string? _shown;

    public FileSystemPanel(EditorWorkspace workspace)
        : base(workspace, "filesystem.rml")
    {
    }

    protected override void OnAttach(RmlDocument document)
    {
        _shown = null;
        Refresh();
    }

    public void Refresh()
    {
        // The folder name only: absolute paths overflow the dock (and differ between machines in captures).
        var root = Workspace.Session.ProjectRoot is { } path ? Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)) : "none";
        if (string.Equals(root, _shown, StringComparison.Ordinal))
            return;
        _shown = root;
        SetText("project-root", root);
    }
}

/// <summary>
/// The output panel: engine <see cref="Log"/> messages (through <see cref="OutputLog"/>) with level filters and a clear
/// button, data-bound as a list (<c>output</c> model). It only changes when a message arrives.
/// </summary>
public sealed class OutputPanel : EditorDocument
{
    private static readonly RmlStructType<OutputMessage> LineType = new RmlStructType<OutputMessage>()
        .Member("level", static m => m.LevelText)
        .Member("time", static m => m.TimeText)
        .Member("text", static m => m.Text);

    private static readonly RmlStructType<OutputMessage> LineTypeWithoutTime = new RmlStructType<OutputMessage>()
        .Member("level", static m => m.LevelText)
        .Member("time", static _ => "")
        .Member("text", static m => m.Text);

    private readonly List<OutputMessage> _visible = [];
    private RmlDataModel? _model;
    private int _filter;
    private int _shownVersion = -1;
    private int _scrollCountdown;
    private readonly int[] _counts = new int[4];

    public OutputPanel(EditorWorkspace workspace)
        : base(workspace, "output.rml")
    {
        _filter = workspace.Layout.Settings.OutputFilter;
    }

    /// <summary>The messages passing the filter, oldest first.</summary>
    public IReadOnlyList<OutputMessage> VisibleMessages => _visible;

    public bool IsShown(OutputLevel level) => (_filter & (1 << (int)level)) != 0;

    protected override void OnReady()
    {
        _model = CreateDataModel("output")
            .Bind("show_debug", this, static p => p.IsShown(OutputLevel.Debug))
            .Bind("show_info", this, static p => p.IsShown(OutputLevel.Info))
            .Bind("show_warn", this, static p => p.IsShown(OutputLevel.Warning))
            .Bind("show_error", this, static p => p.IsShown(OutputLevel.Error))
            .Bind("count_debug", this, static p => p._counts[0])
            .Bind("count_info", this, static p => p._counts[1])
            .Bind("count_warn", this, static p => p._counts[2])
            .Bind("count_error", this, static p => p._counts[3])
            .BindList("lines", _visible, Workspace.Options.OutputTimestamps ? LineType : LineTypeWithoutTime)
            .Event("toggle", e => Toggle((OutputLevel)e.GetArgument(0).GetInt32()))
            .Event("clear", _ => Clear());
        Refresh();
    }

    public void Toggle(OutputLevel level)
    {
        _filter ^= 1 << (int)level;
        Workspace.Layout.SetOutputFilter(_filter);
        _shownVersion = -1;
        Refresh();
    }

    public void Clear()
    {
        Workspace.Output.Clear();
        Refresh();
    }

    /// <summary>Rebuilds the visible list when the log changed, and scrolls to the newest line.</summary>
    public void Refresh()
    {
        var log = Workspace.Output;
        if (_shownVersion == log.Version)
            return;
        _shownVersion = log.Version;
        _visible.Clear();
        Array.Clear(_counts);
        foreach (var message in log.Messages)
        {
            _counts[(int)message.Level]++;
            if (IsShown(message.Level))
                _visible.Add(message);
        }

        if (_model is null)
            return;
        _model.DirtyAll();
        // Scroll to the newest line once the data views created it (the UI updates after the tree's process step).
        if (_scrollCountdown == 0)
            _scrollCountdown = 2;
    }

    /// <summary>Called every frame by the workspace: finishes a pending scroll to the newest line.</summary>
    public void Tick()
    {
        if (_scrollCountdown == 0 || --_scrollCountdown > 0)
            return;
        ScrollToEnd();
    }

    private void ScrollToEnd()
    {
        if (!IsLoaded || Document.GetElementById("lines") is not { IsNull: false } lines || lines.ChildCount == 0)
            return;
        // The data-for template element stays (hidden) after the generated lines: scroll to the last real line.
        for (var i = lines.ChildCount - 1; i >= 0; i--)
        {
            var line = lines.GetChild(i);
            if (line.IsClassSet("line"))
            {
                line.ScrollIntoView(alignWithTop: false);
                return;
            }
        }
    }
}

/// <summary>The four splitter handles (full-window overlay, mouse only over the handles; RCSS <c>drag: drag</c>).</summary>
public sealed class SplittersOverlay : EditorDocument
{
    private static readonly (string Id, Splitter Splitter)[] Handles =
    [
        ("split-left", Splitter.Left),
        ("split-right", Splitter.Right),
        ("split-bottom", Splitter.Bottom),
        ("split-leftdock", Splitter.LeftDock),
    ];

    public SplittersOverlay(EditorWorkspace workspace)
        : base(workspace, "splitters.rml")
    {
    }

    protected override void OnAttach(RmlDocument document)
    {
        foreach (var (id, splitter) in Handles)
        {
            var element = document.GetElementById(id);
            if (element.IsNull)
                continue;
            element.AddEventListener("drag", e => OnDrag(splitter, e));
            element.AddEventListener("dragstart", e => e.CurrentElement.SetClass("dragging", true));
            element.AddEventListener("dragend", e =>
            {
                e.CurrentElement.SetClass("dragging", false);
                Workspace.SaveLayout();
            });
        }

        Apply(Workspace.Layout);
    }

    private void OnDrag(Splitter splitter, RmlEvent e)
    {
        var scale = MathF.Max(0.01f, Workspace.Host.PixelScale);
        var x = e.GetParameter("mouse_x", 0.0) / scale;
        var y = e.GetParameter("mouse_y", 0.0) / scale;
        if (Workspace.Layout.DragSplitter(splitter, (float)x, (float)y))
            Workspace.ApplyLayout();
    }

    /// <summary>Positions the handles from <paramref name="layout"/>.</summary>
    public void Apply(EditorLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (!IsLoaded)
            return;
        foreach (var (id, splitter) in Handles)
        {
            var element = Document.GetElementById(id);
            if (element.IsNull)
                continue;
            var rect = layout.SplitterRect(splitter);
            element.SetProperty("left", RmlText.Dp(rect.X));
            element.SetProperty("top", RmlText.Dp(rect.Y));
            element.SetProperty("width", RmlText.Dp(rect.Width));
            element.SetProperty("height", RmlText.Dp(rect.Height));
        }
    }
}

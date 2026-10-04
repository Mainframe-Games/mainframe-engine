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
    private bool _dirty;

    /// <summary>The scene name shown on the right of the bar (scene icon, name, unsaved marker).</summary>
    public string WindowTitle => _windowTitle;

    /// <summary>Whether the bar shows the unsaved marker.</summary>
    public bool ShowsUnsaved => _dirty;

    /// <summary>The scene name shown on the right of the bar, with an unsaved marker when <paramref name="dirty"/>.</summary>
    public void SetWindowTitle(string text, bool dirty)
    {
        _windowTitle = text ?? "";
        _dirty = dirty;
        ApplyTitle();
    }

    private void ApplyTitle()
    {
        SetText("title-text", _windowTitle);
        if (!IsLoaded)
            return;
        Document.GetElementById("title-icon").SetClass("hidden", _windowTitle.Length == 0);
        Document.GetElementById("title-dirty").SetClass("hidden", !_dirty);
    }

    protected override void OnAttach(RmlDocument document) => ApplyTitle();

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

    private bool? _shownLocal;

    protected override void OnAttach(RmlDocument document)
    {
        _shownLocal = null;
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
        if (_shownLocal != gizmo.Local)
        {
            _shownLocal = gizmo.Local;
            local.SetAttribute("data-tooltip", gizmo.Local
                ? "Local Space (T) — handles follow the node's own axes; click for the world axes"
                : "Global Space (T) — handles follow the world axes; click for the node's local axes");
            var icon = document.GetElementById("tool-local-icon");
            icon.SetClass("icon-world", !gizmo.Local);
            icon.SetClass("icon-cube", gizmo.Local);
        }
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

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
        foreach (var name in (ReadOnlySpan<string>)["file", "edit", "view", "project", "run", "help"])
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
        RefreshPlay();
    }

    private string _instancesRml = "";
    private string _playStatus = "";

    /// <summary>Re-applies the play controls' states and the running instances (on play/project changes, never per frame).</summary>
    public void RefreshPlay()
    {
        if (!IsLoaded)
            return;
        var document = Document;
        var play = Workspace.Play;
        var project = Workspace.Project;
        var canPlay = project.LauncherProject is not null && !play.IsBuilding;
        document.GetElementById("play").SetClass("disabled", !canPlay);
        document.GetElementById("play-scene").SetClass("disabled", !canPlay || Workspace.Session.Active is null);
        document.GetElementById("pause").SetClass("disabled", !play.IsPlaying);
        document.GetElementById("pause").SetClass("active", play.IsPaused);
        document.GetElementById("stop").SetClass("disabled", !play.IsPlaying);
        var build = document.GetElementById("build-reload");
        build.SetClass("disabled", project.GameLibraryProject is null);
        build.SetClass("attention", project.NeedsRebuild && !project.IsBuilding);
        build.SetClass("busy", play.IsBuilding);

        var rml = new System.Text.StringBuilder();
        foreach (var instance in play.Service.Instances)
        {
            var (css, icon) = instance.State switch
            {
                PlayInstanceState.Launching => ("launching", "loader-2"),
                PlayInstanceState.Running => ("running", "player-play"),
                PlayInstanceState.Paused => ("paused", "player-pause"),
                PlayInstanceState.Stopping => ("stopping", "player-stop"),
                PlayInstanceState.Crashed => ("crashed", "alert-octagon"),
                _ => ("exited", "check"),
            };
            rml.Append("<div class=\"instance ").Append(css).Append("\" data-instance=\"").Append(instance.Number)
                .Append("\" data-tooltip=\"").Append(RmlText.Escape($"{instance.Label} #{instance.Number} — {instance.State}. Click to pause, reload its scene or stop it."))
                .Append("\"><span class=\"icon icon-sm icon-").Append(icon).Append("\"></span>").Append(RmlText.Escape(instance.Label)).Append("</div>");
        }

        var instances = rml.ToString();
        if (!string.Equals(instances, _instancesRml, StringComparison.Ordinal))
        {
            _instancesRml = instances;
            document.GetElementById("instances").SetInnerRml(instances);
        }

        var status = play.IsBuilding || project.IsBuilding ? "Building…" : "";
        if (!string.Equals(status, _playStatus, StringComparison.Ordinal))
        {
            _playStatus = status;
            SetText("play-status", status);
        }
    }

    protected override void OnClickElement(RmlEvent e)
    {
        if (FindAttribute(e.Target, "data-instance") is not { } text ||
            !int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ||
            Workspace.Play.Service.Instances.FirstOrDefault(i => i.Number == number) is not { } instance)
            return;
        var element = FindWithAttribute(e.Target, "data-instance");
        var scale = MathF.Max(0.01f, Workspace.Host.PixelScale);
        ShowInstanceMenu(instance, element.Bounds.X / scale, (element.Bounds.Y + element.Bounds.Height) / scale);
    }

    /// <summary>The per-instance menu: pause/resume, reload its scene, stop, clear finished instances.</summary>
    public void ShowInstanceMenu(PlayInstance instance, float x, float y)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var service = Workspace.Play.Service;
        var paused = service.IsPaused(instance);
        Workspace.Popup.Show(
        [
            MenuItem.Header($"{instance.Label} #{instance.Number} · {instance.State}"),
            new MenuItem(paused ? "Resume" : "Pause", "pause", null, instance.IsAlive && instance.HasConnected),
            new MenuItem("Reload Scene", "reload", null, instance.IsAlive && instance.HasConnected),
            new MenuItem("Stop", "stop", null, instance.IsAlive),
            MenuItem.Separator,
            new MenuItem("Clear Finished", "clear", null, service.Instances.Any(i => !i.IsAlive)),
        ], x, y, command =>
        {
            switch (command)
            {
                case "pause" when paused:
                    service.Resume(instance);
                    break;
                case "pause":
                    service.Pause(instance);
                    break;
                case "reload":
                    Workspace.Commands.SaveAll();
                    service.ReloadScene(instance);
                    break;
                case "stop":
                    service.Stop(instance);
                    break;
                case "clear":
                    service.ClearExited();
                    break;
            }

            RefreshPlay();
        });
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

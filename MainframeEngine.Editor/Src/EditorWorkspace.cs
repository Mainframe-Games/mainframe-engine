using System.Globalization;
using Silk.NET.Input;

namespace MainframeEngine.Editor;

/// <summary>Start-up settings of the editor workspace.</summary>
public sealed record EditorWorkspaceOptions
{
    /// <summary>Where the panel layout persists (default <c>~/.mainframe/editor_layout.json</c>); null disables persistence.</summary>
    public string? LayoutPath { get; init; } = EditorLayout.DefaultPath;

    /// <summary>A scene to open at start-up; null opens a new empty scene.</summary>
    public string? InitialScene { get; init; }

    /// <summary>Shows the splash screen while the editor starts and scenes load.</summary>
    public bool ShowSplash { get; init; } = true;

    /// <summary>Shows message times in the output panel (off for deterministic captures).</summary>
    public bool OutputTimestamps { get; init; } = true;

    /// <summary>Shows the fps / frame-time readout in the toolbar (off for deterministic captures).</summary>
    public bool ShowFrameStats { get; init; } = true;

    /// <summary>Where crash recovery copies of unsaved scenes go (default <c>~/.mainframe/recovery</c>).</summary>
    public string RecoveryDirectory { get; init; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mainframe", "recovery");
}

/// <summary>
/// The editor's composition root, a <c>[Tool]</c> node in the editor's scene tree: owns the <see cref="EditorSession"/>
/// (open scenes), the <see cref="EditorLayout"/>, the <see cref="OutputLog"/> and <see cref="EditorCommands"/>, and builds
/// the RmlUi panels (one document each: menu bar, toolbar, scene tree, file system, viewport, inspector, output,
/// splitters), the dialog layer (file picker, list picker, message box, popup menus) and the
/// <see cref="ViewportController"/>. Keyboard shortcuts arrive here as unhandled input (the UI takes keys only while a
/// text field has focus). Hosted by <see cref="EditorApp"/>, or headless in tests.
/// </summary>
[Tool]
public sealed class EditorWorkspace : Node
{
    private string _title = "";
    private double _statsTimer;
    private EditorModifiers _trackedModifiers;
    private readonly bool _firstLaunch;
    private Action? _pendingLoad;
    private int _pendingLoadFrames;

    public EditorWorkspace(IEditorHost host, EditorWorkspaceOptions? options = null)
    {
        Host = host ?? throw new ArgumentNullException(nameof(host));
        Options = options ?? new EditorWorkspaceOptions();
        Name = "EditorWorkspace";
        Layout = new EditorLayout(Options.LayoutPath is { } path ? EditorLayout.Load(path) : null);
        _firstLaunch = Options.LayoutPath is { } layoutPath && !File.Exists(layoutPath);
        Commands = new EditorCommands(this);
        ScenesHost = new Node { Name = "EditedScenes" };
        Session = new EditorSession(ScenesHost);
    }

    public IEditorHost Host { get; }
    public EditorWorkspaceOptions Options { get; }
    public EditorSession Session { get; }
    public EditorLayout Layout { get; }
    public OutputLog Output { get; } = new();
    public EditorCommands Commands { get; }

    /// <summary>Mode, space and snapping of the transform gizmo (shared by every tab).</summary>
    public TransformGizmo Gizmo { get; } = new();

    /// <summary>Parent of the edited scenes' sub-viewports.</summary>
    public Node ScenesHost { get; }

    public UiLayer PanelLayer { get; private set; } = null!;
    public UiLayer DialogLayer { get; private set; } = null!;
    public MenuBarPanel MenuBar { get; private set; } = null!;
    public ToolbarPanel Toolbar { get; private set; } = null!;
    public SceneTreePanel SceneTree { get; private set; } = null!;
    public FileSystemPanel FileSystem { get; private set; } = null!;
    public ViewportPanel ViewportPanel { get; private set; } = null!;
    public InspectorPanel Inspector { get; private set; } = null!;
    public OutputPanel OutputPanel { get; private set; } = null!;
    public SplittersOverlay Splitters { get; private set; } = null!;
    public PopupMenu Popup { get; private set; } = null!;
    public FilePickerDialog FilePicker { get; private set; } = null!;
    public ListPickerDialog ListPicker { get; private set; } = null!;
    public MessageDialog Message { get; private set; } = null!;
    public UiLayer SplashLayer { get; private set; } = null!;
    public SplashScreen Splash { get; private set; } = null!;
    public ViewportController Viewport { get; private set; } = null!;

    /// <summary>True while a modal dialog is open (shortcuts are suspended).</summary>
    public bool IsDialogOpen => FilePicker.Visible || ListPicker.Visible || Message.Visible || Splash.Visible;

    /// <summary>The modifier keys held (host state, or tracked from key events when the host has none).</summary>
    public EditorModifiers Modifiers => Host.Modifiers | _trackedModifiers;

    protected override void OnReady()
    {
        Output.Attach();
        Output.Add(OutputLevel.Info, "Mainframe Editor started.");
        AddChild(ScenesHost);

        PanelLayer = new UiLayer { Name = "EditorPanels", Layer = 0 };
        MenuBar = new MenuBarPanel(this) { Name = "MenuBar" };
        Toolbar = new ToolbarPanel(this) { Name = "Toolbar" };
        SceneTree = new SceneTreePanel(this) { Name = "SceneTree" };
        FileSystem = new FileSystemPanel(this) { Name = "FileSystem" };
        ViewportPanel = new ViewportPanel(this) { Name = "Viewport" };
        Inspector = new InspectorPanel(this) { Name = "Inspector" };
        OutputPanel = new OutputPanel(this) { Name = "Output" };
        Splitters = new SplittersOverlay(this) { Name = "Splitters" };
        PanelLayer.AddChild(ViewportPanel);
        PanelLayer.AddChild(MenuBar);
        PanelLayer.AddChild(Toolbar);
        PanelLayer.AddChild(SceneTree);
        PanelLayer.AddChild(FileSystem);
        PanelLayer.AddChild(Inspector);
        PanelLayer.AddChild(OutputPanel);
        PanelLayer.AddChild(Splitters);

        DialogLayer = new UiLayer { Name = "EditorDialogs", Layer = 50 };
        Popup = new PopupMenu(this) { Name = "Popup" };
        FilePicker = new FilePickerDialog(this) { Name = "FilePicker" };
        ListPicker = new ListPickerDialog(this) { Name = "ListPicker" };
        Message = new MessageDialog(this) { Name = "Message" };
        DialogLayer.AddChild(Popup);
        DialogLayer.AddChild(FilePicker);
        DialogLayer.AddChild(ListPicker);
        DialogLayer.AddChild(Message);

        SplashLayer = new UiLayer { Name = "EditorSplash", Layer = 100 };
        Splash = new SplashScreen(this) { Name = "Splash" };
        SplashLayer.AddChild(Splash);

        Viewport = new ViewportController(this) { Name = "ViewportController" };
        AddChild(Viewport);
        AddChild(PanelLayer);
        AddChild(DialogLayer);
        AddChild(SplashLayer);

        Session.ScenesChanged += OnScenesChanged;
        Session.ActiveChanged += OnActiveChanged;
        Session.SceneEdited += OnSceneEdited;
        Session.SelectionChanged += OnSelectionChanged;

        SyncLayout();
        if (Options.ShowSplash)
            Splash.Show("Starting…", 0.15f, minimumSeconds: _firstLaunch ? 1.0 : 0);
        RunWithSplash(Options.InitialScene is { } initial ? $"Loading {Path.GetFileName(initial)}…" : "Creating a new scene…", OpenInitialScene);
        RefreshAll();
    }

    private void OpenInitialScene()
    {
        if (Options.InitialScene is { } scene)
        {
            try
            {
                Session.Open(scene);
                return;
            }
            catch (Exception e) when (EditorCommands.IsRecoverable(e))
            {
                Log.Error($"[Editor] Could not open '{scene}': {e.Message}");
            }
        }

        Session.NewScene();
    }

    /// <summary>
    /// Runs <paramref name="work"/> (opening a scene, later a project) behind the splash screen: the splash shows
    /// <paramref name="status"/> first, the work runs once that frame has been presented, then the splash fades out.
    /// Without the splash (<see cref="EditorWorkspaceOptions.ShowSplash"/> off) it runs at once.
    /// </summary>
    public void RunWithSplash(string status, Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (!Options.ShowSplash)
        {
            work();
            return;
        }

        if (!Splash.Visible)
            Splash.Show(status, 0.3f);
        else
            Splash.SetStatus(status, 0.4f);
        _pendingLoad = work;
        _pendingLoadFrames = 2; // the splash must reach the screen before the blocking load
    }

    private void RunPendingLoad()
    {
        if (_pendingLoad is null || --_pendingLoadFrames > 0)
            return;
        var work = _pendingLoad;
        _pendingLoad = null;
        try
        {
            work();
        }
        catch (Exception e) when (EditorCommands.IsRecoverable(e))
        {
            Splash.Finish("Failed");
            Commands.ReportError("Loading failed", e);
            return;
        }

        Splash.Finish();
    }

    protected override void OnExitTree()
    {
        SaveLayout();
        Session.ScenesChanged -= OnScenesChanged;
        Session.ActiveChanged -= OnActiveChanged;
        Session.SceneEdited -= OnSceneEdited;
        Session.SelectionChanged -= OnSelectionChanged;
        base.OnExitTree();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Session.Dispose();
            Output.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override void OnProcess(in GameTime gameTime)
    {
        RunPendingLoad();
        Splash.Tick(gameTime.DeltaTime);
        if (Output.Drain())
            OutputPanel.Refresh();
        OutputPanel.Tick();
        SyncLayout();
        UpdateTitle();

        _statsTimer -= gameTime.DeltaTime;
        if (_statsTimer <= 0)
        {
            _statsTimer = 0.5;
            Toolbar.UpdateStats(Host.FramesPerSecond, Host.FrameMilliseconds);
        }
    }

    /// <summary>Re-lays out the panels when the window size changed (no work otherwise).</summary>
    public void SyncLayout()
    {
        var size = Host.WindowSize;
        var before = Layout.Version;
        Layout.Update(size.X, size.Y);
        if (Layout.Version != before)
            ApplyLayout();
    }

    /// <summary>Pushes the layout rectangles to the panels (after a resize or splitter drag).</summary>
    public void ApplyLayout()
    {
        MenuBar.SetRect(Layout.MenuBar);
        Toolbar.SetRect(Layout.Toolbar);
        SceneTree.SetRect(Layout.SceneTree);
        FileSystem.SetRect(Layout.FileSystem);
        ViewportPanel.SetRect(Layout.Viewport);
        Inspector.SetRect(Layout.Inspector);
        OutputPanel.SetRect(Layout.Output);
        Splitters.Apply(Layout);
    }

    /// <summary>Writes the layout settings (window size included) to <see cref="EditorWorkspaceOptions.LayoutPath"/>.</summary>
    public void SaveLayout()
    {
        if (Options.LayoutPath is not { } path)
            return;
        var size = Host.WindowSize;
        Layout.RememberWindowSize((int)size.X, (int)size.Y);
        EditorLayout.Save(path, Layout.Settings);
    }

    // The title only changes with the active scene, its file or its dirty state: compare those, build strings on change.
    private EditedScene? _titleScene;
    private string? _titleFile;
    private bool _titleDirty;

    private void UpdateTitle()
    {
        var active = Session.Active;
        var dirty = active?.IsDirty == true;
        if (_title.Length > 0 && ReferenceEquals(active, _titleScene) && ReferenceEquals(active?.FilePath, _titleFile) && dirty == _titleDirty)
            return;
        _titleScene = active;
        _titleFile = active?.FilePath;
        _titleDirty = dirty;
        var title = active is null ? "Mainframe Editor" : $"{active.Title} — Mainframe Editor";
        if (string.Equals(title, _title, StringComparison.Ordinal))
            return;
        _title = title;
        Host.SetTitle(title);
        MenuBar.SetWindowTitle(active is null ? "" : active.DisplayName, active?.IsDirty == true);
    }

    // ── Session events → panels ──────────────────────────────────────────────────────────────────────────────────

    private void OnScenesChanged()
    {
        ViewportPanel.RefreshTabs();
        FileSystem.Refresh();
    }

    private void OnActiveChanged() => RefreshAll();

    private void OnSceneEdited(EditedScene scene)
    {
        SceneTree.Refresh();
        Inspector.OnSceneEdited();
    }

    private void OnSelectionChanged(EditedScene scene)
    {
        SceneTree.Refresh();
        Inspector.Rebuild();
    }

    private void RefreshAll()
    {
        ViewportPanel.RefreshTabs();
        SceneTree.Refresh();
        Inspector.Rebuild();
        FileSystem.Refresh();
        Viewport.OnActiveSceneChanged();
    }

    // ── Shortcuts ────────────────────────────────────────────────────────────────────────────────────────────────

    protected override void OnInput(InputEvent inputEvent)
    {
        if (inputEvent is InputEventKey key)
            TrackModifier(key);
    }

    protected override void OnUnhandledInput(InputEvent inputEvent)
    {
        if (inputEvent is not InputEventKey { Pressed: true } key || IsDialogOpen)
            return;
        if (Popup.Visible)
        {
            if (key.Key == Key.Escape)
                Popup.Close();
            return;
        }

        var command = ShortcutFor(key.Key, Modifiers);
        if (command is null)
            return;
        GetViewport()?.SetInputAsHandled();
        Commands.Execute(command);
    }

    private void TrackModifier(InputEventKey key)
    {
        var flag = key.Key switch
        {
            Key.ControlLeft or Key.ControlRight or Key.SuperLeft or Key.SuperRight => EditorModifiers.Command,
            Key.ShiftLeft or Key.ShiftRight => EditorModifiers.Shift,
            Key.AltLeft or Key.AltRight => EditorModifiers.Alt,
            _ => EditorModifiers.None,
        };
        if (flag != EditorModifiers.None)
            _trackedModifiers = key.Pressed ? _trackedModifiers | flag : _trackedModifiers & ~flag;
    }

    /// <summary>The command bound to a key with modifiers, or null (the table in docs/design/editor.md).</summary>
    public static string? ShortcutFor(Key key, EditorModifiers modifiers)
    {
        var command = (modifiers & EditorModifiers.Command) != 0;
        var shift = (modifiers & EditorModifiers.Shift) != 0;
        if (command)
        {
            return key switch
            {
                Key.N => "file.new",
                Key.O => "file.open",
                Key.S when shift => "file.save_as",
                Key.S => "file.save",
                Key.W => "file.close",
                Key.Q => "file.quit",
                Key.Z when shift => "edit.redo",
                Key.Z => "edit.undo",
                Key.Y => "edit.redo",
                Key.D => "edit.duplicate",
                Key.A when shift => "scene.instance",
                Key.A => "node.add",
                Key.Up => "node.move_up",
                Key.Down => "node.move_down",
                _ => null,
            };
        }

        if ((modifiers & EditorModifiers.Alt) != 0)
            return null;
        return key switch
        {
            Key.Delete or Key.Backspace => "edit.delete",
            Key.F2 => "edit.rename",
            Key.F => "view.frame",
            Key.G => "view.grid",
            Key.Q => "gizmo.select",
            Key.W => "gizmo.translate",
            Key.E => "gizmo.rotate",
            Key.R => "gizmo.scale",
            Key.T => "gizmo.local",
            Key.Y => "gizmo.snap",
            Key.Keypad1 or Key.Number1 when !shift => "view.front",
            Key.Keypad3 or Key.Number3 when !shift => "view.right",
            Key.Keypad7 or Key.Number7 when !shift => "view.top",
            _ => null,
        };
    }

    // ── Quit and crash safety ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The window's close button or Cmd+Q: asks about unsaved scenes first, then quits.</summary>
    public void RequestQuit()
    {
        var dirty = Session.Scenes.Where(s => s.IsDirty).ToArray();
        if (dirty.Length == 0)
        {
            SaveLayout();
            Host.Quit();
            return;
        }

        var names = string.Join(", ", dirty.Select(s => s.DisplayName));
        Message.Show(new MessageRequest
        {
            Title = "Unsaved changes",
            Message = $"Save the changes to {names} before closing?",
            Buttons = ["Save All", "Don't Save", "Cancel"],
            DefaultButton = 0,
            CancelButton = 2,
            Callback = (button, _) =>
            {
                if (button == 2)
                    return;
                if (button == 0 && !Commands.SaveAll())
                    return; // a save failed or was cancelled: stay open
                SaveLayout();
                Host.Quit();
            },
        });
    }

    /// <summary>
    /// Writes every dirty scene to <see cref="EditorWorkspaceOptions.RecoveryDirectory"/> (crash handler: unhandled
    /// exception). Never throws; returns the files written.
    /// </summary>
    public IReadOnlyList<string> WriteRecoveryCopies()
    {
        var written = new List<string>();
        foreach (var scene in Session.Scenes)
        {
            if (!scene.IsDirty)
                continue;
            try
            {
                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                var name = Path.GetFileNameWithoutExtension(scene.DisplayName.Replace(' ', '_'));
                var path = Path.Combine(Options.RecoveryDirectory, $"{name}-{stamp}.mscene");
                AtomicFile.WriteAllBytes(path, SceneSaver.ToJson(scene.Root));
                written.Add(path);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"[Editor] Recovery copy of {scene.DisplayName} failed: {e.Message}");
            }
        }

        return written;
    }
}

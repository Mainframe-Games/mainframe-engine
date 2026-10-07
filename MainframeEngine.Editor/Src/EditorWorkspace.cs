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

    /// <summary>A project folder (holding <c>project.mfproj</c>) to open at start-up; wins over <see cref="InitialScene"/>'s project.</summary>
    public string? InitialProject { get; init; }

    /// <summary>
    /// Shows the Project Manager at start-up when neither a project nor a scene is given (the editor executable's
    /// default; tests and scripted runs open a new scene instead).
    /// </summary>
    public bool ShowProjectManager { get; init; }

    /// <summary>
    /// The recent projects list (the editor executable passes <c>~/.mainframe/recent_projects.json</c>); null keeps it
    /// in memory (tests, scripted runs).
    /// </summary>
    public string? RecentProjectsPath { get; init; }

    /// <summary>
    /// The editor settings file (the editor executable passes <c>~/.mainframe/editor_settings.json</c>); null uses the
    /// defaults in memory (tests, scripted runs).
    /// </summary>
    public string? EditorSettingsPath { get; init; }

    /// <summary>
    /// A content folder checked before the editor's own for the generated accent theme (<see cref="EditorTheme"/>); the
    /// editor app passes it to the UI server. Null disables accent colours other than the default.
    /// </summary>
    public string? ThemeOverlayDirectory { get; init; }

    /// <summary>Builds games (default: <c>dotnet build</c>); tests pass a fake.</summary>
    public IGameBuilder? GameBuilder { get; init; }

    /// <summary>Launches games (default: a process); tests pass a fake.</summary>
    public IGameLauncher? GameLauncher { get; init; }

    /// <summary>Opens source files (default: the external code editor command); tests record instead.</summary>
    public Func<System.Diagnostics.ProcessStartInfo, bool>? StartCodeEditor { get; init; }

    /// <summary>
    /// Editor updates from GitHub Releases (the editor executable passes <see cref="GitHubUpdateService"/>); null disables
    /// update checks (tests, <c>--smoke</c>, <c>--qa-script</c>, <c>--hidden</c>).
    /// </summary>
    public IUpdateService? Updates { get; init; }

    /// <summary>The HTTP handler the Download Demo dialog uses (default: the editor's shared client); tests pass a stub.</summary>
    public Func<HttpMessageHandler>? DemoHttpHandler { get; init; }

    /// <summary>Where the Download Demo dialog keeps the zip while it extracts (default <c>~/.mainframe/downloads</c>); tests use a temp folder.</summary>
    public string? DemoDownloadsDirectory { get; init; }
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
public sealed partial class EditorWorkspace : Node
{
    private string _title = "";
    private bool _closeRequested;
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
        Settings = EditorSettings.Load(Options.EditorSettingsPath);
        RecentProjects = RecentProjects.Load(Options.RecentProjectsPath);
        CodeEditor = new CodeEditorLauncher(() => Settings, () => Session.ProjectRoot, Options.StartCodeEditor);
        var dotnet = DotnetSdk.FindDotnet() ?? "dotnet";
        Play = new PlayController(this, Options.GameBuilder ?? new DotnetGameBuilder(dotnet), Options.GameLauncher ?? new ProcessGameLauncher(dotnet));
        Project = new ProjectService(this) { AutoReload = Settings.AutoReloadCode };
        AudioPreview = new AudioPreview(() => Tree?.Servers.Get<AudioServer>());
        AudioPreview.Changed += OnAudioPreviewChanged;
        Midi = new Music.MidiInputService(static () => Music.PluginHostClient.TryCreate());
        Midi.SetEnabled(Settings.MidiInputs);
        // Output source links open through the code editor of the Editor Settings.
        SourceOpener = (file, line) => CodeEditor.Open(file, line);
    }

    public IEditorHost Host { get; }
    public EditorWorkspaceOptions Options { get; }
    public EditorSession Session { get; }
    public EditorLayout Layout { get; }
    public OutputLog Output { get; } = new();
    public EditorCommands Commands { get; }

    /// <summary>Opens a source file at a line (the Output panel's source links); default <see cref="ExternalEditor.Open"/>.</summary>
    public Func<string, int, bool> SourceOpener { get; set; } = ExternalEditor.Open;

    /// <summary>The editor's preferences (Editor Settings dialog).</summary>
    public EditorSettings Settings { get; private set; }

    /// <summary>Recently opened projects (Project Manager).</summary>
    public RecentProjects RecentProjects { get; }

    /// <summary>The open game project: its code (load, build, reload) and watchers.</summary>
    public ProjectService Project { get; }

    /// <summary>Out-of-process Play.</summary>
    public PlayController Play { get; }

    /// <summary>The sound preview (one voice): FileSystem Play, inspector AudioStream rows, the sound designer.</summary>
    public AudioPreview AudioPreview { get; }

    /// <summary>MIDI keyboards (Editor Settings › MIDI): messages go to the active song tab (ADR 0148).</summary>
    public Music.MidiInputService Midi { get; }

    /// <summary>Opens source files in the external code editor.</summary>
    public CodeEditorLauncher CodeEditor { get; }

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

    /// <summary>The song tab's chrome (transport, track headers, mixer), shown over the view while a song tab is active.</summary>
    public SongPanel SongPanel { get; private set; } = null!;

    /// <summary>The song tab's canvases, input and renders.</summary>
    public Music.SongView SongView { get; private set; } = null!;
    public SplittersOverlay Splitters { get; private set; } = null!;
    public PopupMenu Popup { get; private set; } = null!;
    public FilePickerDialog FilePicker { get; private set; } = null!;
    public ListPickerDialog ListPicker { get; private set; } = null!;
    public MessageDialog Message { get; private set; } = null!;
    public TreePickerDialog TreePicker { get; private set; } = null!;
    public ConnectSignalDialog SignalDialog { get; private set; } = null!;
    public UiLayer TooltipLayer { get; private set; } = null!;

    /// <summary>The tooltip widget (null until the workspace is ready).</summary>
    public TooltipOverlay? Tooltips { get; private set; }

    public UiLayer SplashLayer { get; private set; } = null!;
    public SplashScreen Splash { get; private set; } = null!;
    public ViewportController Viewport { get; private set; } = null!;

    /// <summary>True while a modal dialog is open (shortcuts are suspended).</summary>
    public bool IsDialogOpen => FilePicker.Visible || ListPicker.Visible || TreePicker.Visible || Message.Visible || Splash.Visible ||
                                SignalDialog.Visible || ProjectDialogOpen;

    /// <summary>The modifier keys held (host state, or tracked from key events when the host has none).</summary>
    public EditorModifiers Modifiers => Host.ReportsModifiers && !TrackKeyModifiers ? Host.Modifiers : Host.Modifiers | _trackedModifiers;

    /// <summary>
    /// Also take modifiers from key events when the host reads the keyboard itself: scripted QA input pushes synthetic
    /// key events the physical keyboard state never sees.
    /// </summary>
    public bool TrackKeyModifiers { get; set; }

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
        SongPanel = new SongPanel(this) { Name = "Song" };
        Splitters = new SplittersOverlay(this) { Name = "Splitters" };
        PanelLayer.AddChild(ViewportPanel);
        PanelLayer.AddChild(SongPanel);
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
        TreePicker = new TreePickerDialog(this) { Name = "TreePicker" };
        Message = new MessageDialog(this) { Name = "Message" };
        SignalDialog = new ConnectSignalDialog(this) { Name = "ConnectSignal" };
        DialogLayer.AddChild(Popup);
        DialogLayer.AddChild(FilePicker);
        DialogLayer.AddChild(ListPicker);
        DialogLayer.AddChild(TreePicker);
        DialogLayer.AddChild(SignalDialog);
        DialogLayer.AddChild(Message);
        CreateProjectUi();

        // Tooltips: above the dialogs, below the splash; the overlay never takes the mouse.
        TooltipLayer = new UiLayer { Name = "EditorTooltips", Layer = 90 };
        Tooltips = new TooltipOverlay(this) { Name = "Tooltips" };
        TooltipLayer.AddChild(Tooltips);
        Tooltips.Watch(DialogLayer);
        Tooltips.Watch(PanelLayer);

        SplashLayer = new UiLayer { Name = "EditorSplash", Layer = 100 };
        Splash = new SplashScreen(this) { Name = "Splash" };
        SplashLayer.AddChild(Splash);

        Viewport = new ViewportController(this) { Name = "ViewportController" };
        AddChild(Viewport);
        SongView = new Music.SongView(this) { Name = "SongView" };
        AddChild(SongView);
        AddChild(PanelLayer);
        AddChild(ProjectLayer);
        AddChild(DialogLayer);
        AddChild(TooltipLayer);
        AddChild(SplashLayer);

        Session.ScenesChanged += OnScenesChanged;
        Session.ActiveChanged += OnActiveChanged;
        Session.SceneEdited += OnSceneEdited;
        Session.SelectionChanged += OnSelectionChanged;

        SyncLayout();
        if (Options.ShowSplash)
            Splash.Show("Starting…", 0.15f, minimumSeconds: _firstLaunch ? 1.0 : 0);
        ApplyAccent();
        StartUp();
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
        Session.ProjectChanged -= OnProjectChanged;
        base.OnExitTree();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Updates?.Dispose();
            AudioPreview.Stop();
            Midi.Dispose();
            Play.Dispose();
            Session.Dispose();
            Project.Dispose();
            Output.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override void OnProcess(in GameTime gameTime)
    {
        RunPendingLoad();
        if (_closeRequested && _pendingLoad is null)
            OnCloseRequested();
        Splash.Tick(gameTime.DeltaTime);
        AudioPreview.Tick();
        Midi.Update();
        ProcessProjects(gameTime.DeltaTime);
        if (Output.Drain())
            OutputPanel.Refresh();
        OutputPanel.Tick();
        TreePicker.Tick();
        Tooltips?.Tick(gameTime.DeltaTime);
        SyncLayout();
        Session.Tick();
        PlacePreview();
        ViewportPanel.Tick();
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
        SongPanel.SetRect(Layout.ViewportImage);
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

    /// <summary>The view area a UI preview's document is laid out in (below the preview bar), in window pixels.</summary>
    public System.Drawing.Rectangle PreviewRegion
    {
        get
        {
            var rect = Layout.PreviewImage;
            var scale = Host.PixelScale;
            var x = (int)MathF.Round(rect.X * scale);
            var y = (int)MathF.Round(rect.Y * scale);
            return new System.Drawing.Rectangle(x, y, Math.Max(1, (int)MathF.Round(rect.Right * scale) - x),
                Math.Max(1, (int)MathF.Round(rect.Bottom * scale) - y));
        }
    }

    // The active preview's layer follows the view area (panels resize, splitters move).
    private void PlacePreview()
    {
        if (Session.ActivePreview is not { } preview)
        {
            _previewSize = default;
            return;
        }

        var region = PreviewRegion;
        if (preview.Layer.Region != region)
            preview.Layer.Region = region;
        var rect = Layout.PreviewImage;
        var size = ((int)MathF.Round(rect.Width), (int)MathF.Round(rect.Height));
        if (size == _previewSize)
            return;
        _previewSize = size;
        ViewportPanel.SetPreviewSize(string.Create(CultureInfo.InvariantCulture, $"{size.Item1}×{size.Item2}"));
    }

    private (int, int) _previewSize;

    // The title only changes with the active tab, its file or its dirty state (and the project): compare those.
    private IEditorTab? _titleScene;
    private string? _titleFile;
    private bool _titleDirty;

    private void UpdateTitle()
    {
        var active = Session.ActiveTab;
        var dirty = active?.IsDirty == true;
        if (_title.Length > 0 && ReferenceEquals(active, _titleScene) && ReferenceEquals(active?.FilePath, _titleFile) && dirty == _titleDirty)
            return;
        _titleScene = active;
        _titleFile = active?.FilePath;
        _titleDirty = dirty;
        var projectName = Session.Project?.Name;
        var suffix = projectName is null ? EditorBrand.NameWithVersion : $"{projectName} — {EditorBrand.NameWithVersion}";
        var title = active is null ? suffix : $"{active.Title} — {suffix}";
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
        FileSystem.UpdateUnsaved();
        FileSystem.Refresh();
    }

    private void OnActiveChanged() => RefreshAll();

    // A preview started or ended: the Play/Stop icons and the FileSystem badge follow it.
    private void OnAudioPreviewChanged()
    {
        if (!IsInsideTree)
            return;
        FileSystem.RefreshPreviewBadges();
        Inspector.RefreshPreviewButtons();
    }

    private void OnSceneEdited(EditedScene scene)
    {
        SceneTree.Refresh();
        Inspector.OnSceneEdited();
    }

    private void OnSelectionChanged(EditedScene scene)
    {
        SceneTree.Refresh();
        if (Inspector.InspectedResource is not null)
            Inspector.CloseResource(); // rebuilds for the selection
        else
            Inspector.Rebuild();
    }

    private void RefreshAll()
    {
        Toolbar.RefreshPlay();
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
        // Keys and clicks the UI did not take (shortcuts, the 3D view) also hide the tooltip.
        if (inputEvent is InputEventKey { Pressed: true } or InputEventMouseButton { Pressed: true } or InputEventMouseWheel)
            Tooltips?.Dismiss();
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

        // A song tab's own keys (Space, L, Home, Del, Cmd+C/V/D, arrows, Q) come first; F5–F8 and the rest stay global.
        if (SongView.HandleKey(key.Key, Modifiers))
        {
            GetViewport()?.SetInputAsHandled();
            return;
        }

        var command = ProjectShortcutFor(key.Key, Modifiers) ?? ShortcutFor(key.Key, Modifiers);
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

    /// <summary>
    /// The window's close button or Cmd+Q: asks about unsaved scenes first, then quits. <paramref name="beforeQuit"/> runs
    /// once quitting is confirmed (after saving); returning false keeps the editor open (Update &amp; restart).
    /// </summary>
    public void RequestQuit(Func<bool>? beforeQuit = null)
    {
        void Quit()
        {
            if (beforeQuit is not null && !beforeQuit())
                return;
            SaveLayout();
            Host.Quit();
        }

        var dirty = Session.DirtyTabs;
        if (dirty.Count == 0)
        {
            Quit();
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
                if (button == 1)
                {
                    Quit();
                    return;
                }

                // Save All: untitled scenes get a Save As dialog each; a failure or cancel keeps the editor open.
                Commands.SaveAll(saved =>
                {
                    if (saved)
                        Quit();
                });
            },
        });
    }

    /// <summary>
    /// The window's close button or the Dock's Quit (intercepted by the host): always ends in <see cref="RequestQuit"/>.
    /// Open dialogs are closed first — Project Settings asks about its unsaved edits — except a message box, which is
    /// the question to answer (it may be the unsaved-changes one). During a load the request waits for it to finish.
    /// </summary>
    public void OnCloseRequested()
    {
        if (_pendingLoad is not null)
        {
            _closeRequested = true; // retried by OnProcess once the load has run
            return;
        }

        _closeRequested = false;
        if (Message.Visible)
        {
            Log.Info("[Editor] Close requested while a message is open; answer it first.");
            return;
        }

        // Nothing in these is kept: pickers and wizards cancel (a running creation or download is cancelled too).
        if (FilePicker.Visible)
            FilePicker.Cancel();
        if (ListPicker.Visible)
            ListPicker.Cancel();
        if (TreePicker.Visible)
            TreePicker.Cancel();
        if (SignalDialog.Visible)
            SignalDialog.Cancel();
        if (NewProject.Visible)
            NewProject.Cancel();
        if (DownloadDemo.Visible)
            DownloadDemo.Cancel();
        if (EditorSettingsDialog.Visible)
            EditorSettingsDialog.Cancel();
        if (UpdateDialog.Visible)
            UpdateDialog.Close();
        if (ProjectManager.Visible)
            ProjectManager.Close();
        if (Popup.Visible)
            Popup.Close();
        if (ProjectSettings.Visible)
            ProjectSettings.RequestClose(() => RequestQuit());
        else
            RequestQuit();
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
                // Same names in the same second (two "Untitled" tabs, two Main.mscene of different folders) must not
                // overwrite each other: number them.
                var path = Path.Combine(Options.RecoveryDirectory, $"{name}-{stamp}.mscene");
                for (var n = 2; File.Exists(path) || written.Contains(path); n++)
                    path = Path.Combine(Options.RecoveryDirectory, $"{name}-{stamp}-{n}.mscene");
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

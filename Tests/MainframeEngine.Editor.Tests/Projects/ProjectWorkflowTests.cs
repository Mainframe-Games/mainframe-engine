using MainframeEngine.EditorLink;
using MainframeEngine.Serialization;
using Silk.NET.Input;

namespace MainframeEngine.Editor.Tests.Projects;

/// <summary>A small game project on disk (no game code): project file, two scenes, a desktop project with a fake build output.</summary>
internal sealed class TestProject : IDisposable
{
    public TestProject(string name = "Workflow")
    {
        Name = name;
        Root = GameProjectLayout.RealPath(Directory.CreateTempSubdirectory("mf-project").FullName);
        var settings = new ProjectSettings { Name = name, MainScene = "Content/Scenes/Main.mscene" };
        settings.Input.AddAction("jump");
        settings.Save(Root);
        Directory.CreateDirectory(Abs("Content/Scenes"));
        File.WriteAllText(Abs("Content/Scenes/Main.mscene"), """
            {
              "format": 1,
              "uid": "scn_00000000a001",
              "root": { "type": "Node3D", "name": "Main", "children": [ { "type": "Node3D", "name": "Child" } ] }
            }
            """);
        File.WriteAllText(Abs("Content/Scenes/Level.mscene"), """
            { "format": 1, "uid": "scn_00000000a002", "root": { "type": "Node3D", "name": "Level" } }
            """);
        var desktop = Abs($"{name}.Desktop");
        Directory.CreateDirectory(Path.Combine(desktop, "bin", "Debug", "net10.0"));
        File.WriteAllText(Path.Combine(desktop, $"{name}.Desktop.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>\n");
        File.WriteAllText(Path.Combine(desktop, "bin", "Debug", "net10.0", $"{name}.Desktop.dll"), "fake");
    }

    public string Name { get; }
    public string Root { get; }

    public string Abs(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>A trash that records instead of touching the OS trash; can pretend to be unavailable.</summary>
internal sealed class RecordingTrash : ITrash
{
    public List<string> Trashed { get; } = [];
    public bool Available { get; set; } = true;
    public bool IsSupported => Available;

    public bool TryMoveToTrash(string path, out string? error)
    {
        if (!Available)
        {
            error = "no trash on this volume";
            return false;
        }

        Trashed.Add(path);
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
        else
            File.Delete(path);
        error = null;
        return true;
    }
}

/// <summary>
/// E4 workflow headless: the Project Manager (recents, missing projects, open), Project Settings (every section,
/// input capture, autoloads, undo, save round trip), the FileSystem panel (create, rename with fixups, trash, drags),
/// Play through the PlayService with fake builds and games (Output streaming, pause, stop, F5–F8), editor settings
/// (accent overlay, code editor command), resource files and the AudioBusLayout custom inspector, autosave.
/// </summary>
[Collection(nameof(SerialEditor))]
public sealed class ProjectWorkflowTests : IDisposable
{
    private readonly TestProject _project = new();
    private readonly FakeGameBuilder _builder = new();
    private readonly FakeGameLauncher _launcher = new();
    private readonly List<System.Diagnostics.ProcessStartInfo> _opened = [];
    private HeadlessEditor? _editor;

    public void Dispose()
    {
        _editor?.Dispose();
        _project.Dispose();
    }

    private HeadlessEditor Open(Func<EditorWorkspaceOptions, EditorWorkspaceOptions>? more = null)
    {
        _editor = new HeadlessEditor(configure: o =>
        {
            var options = o with
            {
                InitialProject = _project.Root,
                GameBuilder = _builder,
                GameLauncher = _launcher,
                StartCodeEditor = start =>
                {
                    _opened.Add(start);
                    return true;
                },
            };
            return more?.Invoke(options) ?? options;
        });
        _editor.Tick(3);
        return _editor;
    }

    private void TickUntil(Func<bool> condition, int seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            _editor!.Tick();
            Thread.Sleep(5);
        }

        Assert.True(condition(), "Timed out. Output: " + string.Join(" | ", _editor!.Workspace.Output.Messages.TakeLast(12).Select(m => $"[{m.Category}] {m.Text}")));
    }

    // ── Project Manager ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheProjectManagerListsRecentsAndOpensAProjectWithItsMainScene()
    {
        _editor = new HeadlessEditor(configure: o => o with { ShowProjectManager = true, GameBuilder = _builder, GameLauncher = _launcher });
        var w = _editor.Workspace;
        w.RecentProjects.Touch(Path.Combine(_project.Root, "gone"), "Gone");
        w.RecentProjects.Touch(_project.Root, _project.Name);
        w.ProjectManager.Open();
        _editor.Tick(3);

        Assert.True(w.ProjectManager.Visible);
        Assert.True(w.IsDialogOpen);
        Assert.Empty(w.Session.Scenes); // no scene until a project opens
        Assert.Equal([_project.Name, "Gone"], w.ProjectManager.VisibleProjects.Select(p => p.Name));
        Assert.Empty(_editor.RmlMessages);

        // A missing project offers to remove it from the list.
        Assert.True(w.ProjectManager.Select(Path.Combine(_project.Root, "gone")));
        w.ProjectManager.OpenSelected();
        Assert.Equal("Project Missing", w.Message.Current?.Title);
        w.Message.Answer(0);
        Assert.Single(w.RecentProjects.Items);

        Assert.True(w.ProjectManager.Select(_project.Root));
        w.ProjectManager.OpenSelected();
        _editor.Tick(3);
        Assert.False(w.ProjectManager.Visible);
        Assert.Equal(_project.Root, w.Session.ProjectRoot);
        Assert.Equal("Main.mscene", Path.GetFileName(Assert.Single(w.Session.Scenes).FilePath));
        Assert.Equal($"Main.mscene — {_project.Name} — {EditorBrand.NameWithVersion}", _editor.Host.Title);
        Assert.Equal(_project.Root, w.RecentProjects.Items[0].Path);
    }

    [Fact]
    public void ClosingTheWindowFromTheProjectManagerQuits()
    {
        _editor = new HeadlessEditor(configure: o => o with { ShowProjectManager = true, GameBuilder = _builder, GameLauncher = _launcher });
        var w = _editor.Workspace;
        w.ProjectManager.Open();
        _editor.Tick(3);
        Assert.True(w.IsDialogOpen);

        w.OnCloseRequested();
        Assert.True(_editor.Host.QuitRequested);
        Assert.False(w.ProjectManager.Visible);
    }

    [Fact]
    public void ClosingTheWindowAsksAboutUnsavedProjectSettingsFirst()
    {
        var w = Open().Workspace;
        Assert.True(w.ProjectSettings.Open());
        _editor!.Tick();
        Assert.Null(w.ProjectSettings.Model!.Set("window.title", "Changed"));

        w.OnCloseRequested();
        Assert.Equal("Unsaved Project Settings", w.Message.Current?.Title);
        w.Message.Answer(2); // cancel: the dialog and the editor stay
        Assert.True(w.ProjectSettings.Visible);
        Assert.False(_editor.Host.QuitRequested);

        w.OnCloseRequested();
        w.Message.Answer(1); // don't save
        Assert.False(w.ProjectSettings.Visible);
        Assert.True(_editor.Host.QuitRequested);
    }

    [Fact]
    public void OpenProjectAcceptsOnlyAProjectFileNeverAFolder()
    {
        _editor = new HeadlessEditor(configure: o => o with { ShowProjectManager = true, GameBuilder = _builder, GameLauncher = _launcher });
        var w = _editor.Workspace;
        File.WriteAllText(_project.Abs("Other.mfproj"), "{}");
        File.WriteAllText(_project.Abs("notes.txt"), "");
        _editor.Tick(3);

        w.Commands.ChooseProjectFile(Path.GetDirectoryName(_project.Root)!);
        var picker = Assert.IsType<FilePickerModel>(w.FilePicker.Model);
        Assert.True(w.FilePicker.Visible);
        Assert.Equal(FilePickerMode.Open, picker.Mode);

        // A folder — even the project's own — is not a result: the picker stays open.
        picker.FileName = _project.Root;
        w.FilePicker.Accept();
        Assert.True(w.FilePicker.Visible);
        Assert.Null(w.Session.ProjectRoot);

        // Inside the project only .mfproj files are listed (folders still are, to navigate).
        Assert.True(picker.Navigate(_project.Root));
        Assert.Equal(["Other.mfproj", ProjectSettings.FileName], picker.Entries.Where(e => !e.IsDirectory).Select(e => e.Name));
        picker.FileName = "notes.txt";
        w.FilePicker.Accept();
        Assert.True(w.FilePicker.Visible);

        // A .mfproj that is not project.mfproj is refused with a message.
        picker.FileName = "Other.mfproj";
        w.FilePicker.Accept();
        Assert.Equal("Not a Project", w.Message.Current?.Title);
        w.Message.Answer(0);
        Assert.Null(w.Session.ProjectRoot);

        w.Commands.ChooseProjectFile(_project.Root);
        w.FilePicker.Model!.FileName = ProjectSettings.FileName;
        w.FilePicker.Accept();
        _editor.Tick(3);
        Assert.Equal(_project.Root, w.Session.ProjectRoot);
    }

    // The project's window icon: the brand logo copied to Content/icon.png and named by window.icon.
    private string WriteIcon()
    {
        var icon = _project.Abs("Content/icon.png");
        Directory.CreateDirectory(Path.GetDirectoryName(icon)!);
        File.Copy(ContentPaths.Resolve("Content/Brand/logo-48.png"), icon);
        var settings = ProjectSettings.Load(_project.Abs(ProjectSettings.FileName));
        settings.Window.Icon = "Content/icon.png";
        settings.Save(_project.Root);
        return icon;
    }

    [Fact]
    public void ProjectManagerShowsTheProjectIcon()
    {
        var icon = WriteIcon();
        _editor = new HeadlessEditor(configure: o => o with { ShowProjectManager = true, GameBuilder = _builder, GameLauncher = _launcher });
        var w = _editor.Workspace;
        w.RecentProjects.Touch(Path.Combine(_project.Root, "gone"), "Gone");
        w.RecentProjects.Touch(_project.Root, _project.Name);
        w.ProjectManager.Open();
        _editor.Tick(3);

        Assert.Equal(icon, w.ProjectManager.VisibleProjects.Single(p => p.Name == _project.Name).Icon);
        Assert.Equal("", w.ProjectManager.VisibleProjects.Single(p => p.Name == "Gone").Icon); // a missing project has none
        Assert.Empty(_editor.RmlMessages);
    }

    [Fact]
    public void ProjectSettingsPreviewsTheWindowIconWhenItIsAnExistingPng()
    {
        WriteIcon();
        var w = Open().Workspace;
        Assert.True(w.ProjectSettings.Open());
        w.ProjectSettings.SelectSection("Window");
        _editor!.Tick();
        Assert.Contains("ps-preview", w.ProjectSettings.Document.GetElementById("ps-body").InnerRml, StringComparison.Ordinal);

        Assert.Null(w.ProjectSettings.Model!.Set("window.icon", "Content/missing.png"));
        _editor.Tick();
        Assert.DoesNotContain("ps-preview", w.ProjectSettings.Document.GetElementById("ps-body").InnerRml, StringComparison.Ordinal);
        Assert.Empty(_editor.RmlMessages);
    }

    [Fact]
    public void TheNewProjectWizardValidatesAsYouType()
    {
        var w = Open().Workspace;
        w.NewProject.Open();
        _editor!.Tick();
        w.NewProject.Location = _project.Root;
        w.NewProject.EnginePath = TemplateLocator.FindEngineCheckout() ?? "";
        w.NewProject.ProjectName = "1bad name";
        Assert.False(w.NewProject.Validate());
        Assert.NotEqual("", w.NewProject.Error);
        w.NewProject.ProjectName = "Content"; // an existing, non-empty folder
        Assert.False(w.NewProject.Validate());
        w.NewProject.ProjectName = "FreshGame";
        w.NewProject.EnginePath = _project.Root; // not an engine checkout
        Assert.False(w.NewProject.Validate());
        Assert.Contains("MainframeEngine", w.NewProject.Error, StringComparison.Ordinal);
        w.NewProject.Cancel();
        Assert.False(w.NewProject.Visible);
    }

    // ── Project Settings ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EveryProjectSettingRoundTripsThroughTheModel()
    {
        var model = new ProjectSettingsModel(ProjectSettings.Load(_project.Root), _project.Abs(ProjectSettings.FileName));
        var values = new Dictionary<string, string>
        {
            ["name"] = "Renamed",
            ["mainScene"] = "Content/Scenes/Level.mscene",
            ["assemblies"] = "One, Two",
            ["isDemo"] = "true",
            ["steam.appId"] = "480",
            ["steam.demoAppId"] = "481",
            ["steam.devAppIdFile"] = "false",
            ["steam.restartThroughSteam"] = "true",
            ["window.title"] = "Title",
            ["window.width"] = "1920",
            ["window.height"] = "1080",
            ["window.vsync"] = "false",
            ["window.maxFps"] = "144",
            ["window.icon"] = "Content/icon.png",
            ["physics.ticksPerSecond"] = "120",
            ["physics.maxStepsPerFrame"] = "8",
            ["physics.3d.gravity"] = "0, -20, 0",
            ["physics.3d.substeps"] = "2",
            ["physics.3d.solverIterations"] = "8",
            ["physics.3d.relaxationIterations"] = "3",
            ["physics.3d.allowDeactivation"] = "false",
            ["physics.3d.multiThreaded"] = "false",
            ["physics.3d.deterministic"] = "true",
            ["physics.2d.gravity"] = "0, -500",
            ["physics.2d.pixelsPerMeter"] = "32",
            ["physics.2d.substeps"] = "6",
            ["physics.2d.allowSleep"] = "false",
            ["physics.2d.continuous"] = "false",
            ["audio.enabled"] = "false",
            ["audio.busLayout"] = "Content/Settings/Buses.mres",
            ["audio.sampleRate"] = "48000",
            ["audio.bufferMs"] = "40",
            ["localization.defaultLocale"] = "es",
            ["localization.sourceLocale"] = "en",
            ["localization.fallbacks"] = "es, fr",
            ["localization.directory"] = "Content/i18n",
            ["localization.domain"] = "game",
            ["rendering.exposure"] = "1.25",
            ["rendering.shadows"] = "Low",
        };
        foreach (var setting in ProjectSettingsModel.Settings)
            if (setting.Set is not null)
                Assert.True(values.ContainsKey(setting.Key), $"no test value for {setting.Key}");
        foreach (var (key, value) in values)
        {
            Assert.Null(model.Set(key, value));
            Assert.Equal(value, model.Get(key));
        }

        Assert.True(model.Save());
        var reloaded = ProjectSettings.Load(_project.Root);
        Assert.Equal(model.Current.ToJson(), reloaded.ToJson());
        foreach (var (key, value) in values)
            Assert.Equal(value, ProjectSettingsModel.Find(key)!.Get(reloaded));
        Assert.False(model.IsDirty);

        // Everything undoes back to the original file.
        var original = model.History.Actions.Count;
        model.History.GoTo(0);
        Assert.Equal("Workflow", model.Get("name"));
        Assert.Equal(original, model.History.Actions.Count);
    }

    [Fact]
    public void InvalidValuesAreRejectedWithAMessage()
    {
        var model = new ProjectSettingsModel(ProjectSettings.Load(_project.Root), _project.Abs(ProjectSettings.FileName));
        Assert.NotNull(model.Set("window.width", "wide"));
        Assert.NotNull(model.Set("window.width", "-5"));
        Assert.NotNull(model.Set("physics.3d.gravity", "1, 2"));
        Assert.NotNull(model.Set("name", "  "));
        Assert.NotNull(model.Set("engineVersion", "9"));
        Assert.Empty(model.History.Actions);
        Assert.Null(model.Set("window.width", "1280")); // unchanged: no entry
        Assert.Empty(model.History.Actions);
    }

    [Fact]
    public void TheDialogEditsTheInputMapAndAutoloadsWithUndoAndSaves()
    {
        var w = Open().Workspace;
        Assert.True(w.ProjectSettings.Open());
        _editor!.Tick();
        var model = w.ProjectSettings.Model!;
        w.ProjectSettings.SelectSection("Input Map");
        _editor.Tick();
        Assert.Empty(_editor.RmlMessages);

        Assert.Null(model.AddAction("fire"));
        Assert.NotNull(model.AddAction("fire"));
        w.ProjectSettings.BeginCapture("fire");
        Assert.Equal("fire", w.ProjectSettings.CapturingAction);
        w.ProjectSettings.CompleteCapture(ProjectSettingsModel.Capture(Key.Space));
        w.ProjectSettings.BeginCapture("fire");
        w.ProjectSettings.CompleteCapture(ProjectSettingsModel.Capture(MouseButton.Left));
        Assert.Null(model.AddBinding("fire", InputBinding.Parse("pad:A")));
        Assert.Equal(["key:Space", "mouse:Left", "pad:A"], model.Current.Input.GetAction("fire")!.Bindings.Select(b => b.ToString()));
        Assert.Null(ProjectSettingsModel.Capture(Key.Escape));
        Assert.Null(model.RenameAction("fire", "shoot"));
        Assert.Null(model.SetDeadzone("shoot", "0.3"));
        Assert.Null(model.RemoveBinding("shoot", InputBinding.Parse("mouse:Left")));

        w.ProjectSettings.SelectSection("Autoloads");
        Assert.Null(model.AddAutoload("Music", "Content/Scenes/Level.mscene"));
        Assert.Null(model.AddAutoload("Stats", "Node3D"));
        Assert.Null(model.MoveAutoload(1, -1));
        Assert.Equal(["Stats", "Music"], model.Current.Autoloads.Select(a => a.Name));
        Assert.Null(model.SetAutoloadEnabled(0, false));
        _editor.Tick();
        Assert.Empty(_editor.RmlMessages);

        Assert.True(w.ProjectSettings.Save());
        var saved = ProjectSettings.Load(_project.Root);
        var shoot = saved.Input.GetAction("shoot")!;
        Assert.Equal(0.3f, shoot.Deadzone, 3);
        Assert.Equal(["key:Space", "pad:A"], shoot.Bindings.Select(b => b.ToString()));
        Assert.Equal(["Stats", "Music"], saved.Autoloads.Select(a => a.Name));
        Assert.False(saved.Autoloads[0].Enabled);
        Assert.Same(model.Current, w.Session.Project);

        // Undo after saving makes it dirty; closing asks.
        model.History.Undo();
        Assert.True(model.IsDirty);
        w.ProjectSettings.RequestClose();
        Assert.Equal("Unsaved Project Settings", w.Message.Current?.Title);
        w.Message.Answer(1); // Don't Save
        Assert.False(w.ProjectSettings.Visible);
        Assert.Equal(0.3f, ProjectSettings.Load(_project.Root).Input.GetAction("shoot")!.Deadzone, 3);
    }

    // ── FileSystem ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheFileSystemPanelShowsTheProjectAndRenamesWithReferenceFixups()
    {
        var w = Open().Workspace;
        var fs = w.FileSystem;
        fs.Trash = new RecordingTrash();
        Assert.NotNull(fs.Files);
        Assert.Contains(fs.Rows, r => r.Name == "Content");
        Assert.DoesNotContain(fs.Rows, r => r.Name.EndsWith(".meta", StringComparison.Ordinal));
        Assert.Empty(_editor!.RmlMessages);

        foreach (var view in new[] { FileSystemView.List, FileSystemView.Grid, FileSystemView.Tree })
        {
            fs.SetView(view);
            _editor.Tick();
            Assert.Equal(view, fs.View);
        }

        // Rename the open main scene: the session follows, project.mfproj's mainScene is fixed.
        var main = _project.Abs("Content/Scenes/Main.mscene");
        Assert.True(fs.Rename(main, "Start.mscene"));
        _editor.Tick();
        Assert.Equal(_project.Abs("Content/Scenes/Start.mscene"), w.Session.Active!.FilePath);
        Assert.Equal("Content/Scenes/Start.mscene", ProjectSettings.Load(_project.Root).MainScene);
        Assert.Equal(_project.Abs("Content/Scenes/Start.mscene"), fs.SelectedPath);
    }

    [Fact]
    public void FilesGoToTheTrashAndAPermanentDeleteIsAskedFirst()
    {
        var w = Open().Workspace;
        var trash = new RecordingTrash();
        w.FileSystem.Trash = trash;
        var level = _project.Abs("Content/Scenes/Level.mscene");
        w.FileSystem.Delete(level);
        Assert.Equal("Move to Trash", w.Message.Current?.Title);
        w.Message.Answer(0);
        Assert.Equal([level], trash.Trashed);
        Assert.False(File.Exists(level));

        trash.Available = false;
        File.WriteAllText(level, "{ \"format\": 1, \"root\": { \"type\": \"Node3D\", \"name\": \"Level\" } }");
        w.FileSystem.Rescan();
        w.FileSystem.Delete(level);
        w.Message.Answer(0); // Move to Trash → not available
        Assert.Equal("Delete Permanently?", w.Message.Current?.Title);
        Assert.True(File.Exists(level));
        w.Message.Answer(1); // Cancel keeps it
        Assert.True(File.Exists(level));
    }

    [Fact]
    public void DraggingASceneOntoTheTreeInstancesItAndCreateCommandsMakeFiles()
    {
        var w = Open().Workspace;
        var scene = w.Session.Active!;
        var child = scene.Root.GetNode("Child");
        w.FileSystem.BeginDrag(_project.Abs("Content/Scenes/Level.mscene"));
        Assert.NotNull(w.FileDrag);
        Assert.True(w.DropFileOnNode(w.FileDrag!, child));
        Assert.Null(w.FileDrag);
        var instance = Assert.Single(child.Children);
        Assert.Equal("Level", instance.Name);
        Assert.NotNull(instance.SceneFilePath);
        Assert.Equal("Instance Level", scene.History.UndoAction!.Name);

        // New folder / scene through the panel's commands (name prompts).
        w.FileSystem.Select(_project.Abs("Content"));
        Assert.True(w.Commands.Execute("fs.new_folder"));
        w.Message.Answer(0); // default name "NewFolder"
        Assert.True(Directory.Exists(_project.Abs("Content/NewFolder")));
        w.FileSystem.Select(_project.Abs("Content/NewFolder"));
        Assert.True(w.Commands.Execute("fs.new_scene_2d"));
        w.Message.Answer(0);
        Assert.True(File.Exists(_project.Abs("Content/NewFolder/NewScene.mscene")));
        Assert.True(w.Session.Active!.Camera.Is2D); // opened, a Node2D scene
    }

    // ── Play ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void PlayBuildsLaunchesStreamsLogsAndStops()
    {
        var w = Open().Workspace;
        Assert.NotNull(w.Project.DesktopProject);
        Assert.False(w.Toolbar.Document.GetElementById("play").IsClassSet("disabled"));
        Assert.True(w.Toolbar.Document.GetElementById("stop").IsClassSet("disabled"));

        _editor!.Key(Key.F5);
        TickUntil(() => w.Play.Service.Instances.Any(i => i.State == PlayInstanceState.Running));
        Assert.Equal(1, _builder.Calls);
        var game = Assert.Single(_launcher.Games);
        Assert.Contains("--editor-port", game.Request.Arguments);
        TickUntil(() => w.Output.Messages.Any(m => m.Category == "game" && m.Text.Contains("log 1", StringComparison.Ordinal)));
        Assert.Contains(w.Output.Messages, m => m.Category.StartsWith("game", StringComparison.Ordinal) && m.Text.Contains("log 2", StringComparison.Ordinal));
        Assert.True(w.Play.IsPlaying);
        _editor.Tick();
        Assert.False(w.Toolbar.Document.GetElementById("stop").IsClassSet("disabled"));
        Assert.Contains("instance", w.Toolbar.Document.GetElementById("instances").InnerRml, StringComparison.Ordinal);

        _editor.Key(Key.F7);
        TickUntil(() => game.Paused);
        _editor.Key(Key.F7);
        TickUntil(() => !game.Paused);

        _editor.Key(Key.F8);
        TickUntil(() => !w.Play.IsPlaying);
        Assert.Equal(PlayInstanceState.Exited, w.Play.Service.Instances[0].State);
        Assert.Empty(_editor.RmlMessages);
    }

    [Fact]
    public void PlaySceneRunsTheOpenSceneByUidAndBuildErrorsAreClickable()
    {
        var w = Open().Workspace;
        _builder.Result = FakeGameBuilder.Failure(new BuildDiagnostic(BuildDiagnosticSeverity.Error, "CS1002", "; expected",
            _project.Abs("project.mfproj"), 3, 7, null));
        _editor!.Key(Key.F6);
        TickUntil(() => !w.Play.IsBuilding && w.Output.Messages.Any(m => m.Text.Contains("CS1002", StringComparison.Ordinal)));
        Assert.Empty(_launcher.Games);
        var error = w.Output.Messages.First(m => m.Text.Contains("CS1002", StringComparison.Ordinal));
        Assert.Equal(OutputLevel.Error, error.Level);
        Assert.True(error.HasSource);
        Assert.Equal(3, error.CallerLine);
        Assert.True(w.SourceOpener(error.CallerFile, error.CallerLine));
        Assert.NotEmpty(_opened);

        _builder.Result = new GameBuildResult(true, [], TimeSpan.FromMilliseconds(1), false, null, []);
        _editor.Key(Key.F6);
        TickUntil(() => _launcher.Games.Count == 1);
        var arguments = _launcher.Games[0].Request.Arguments;
        Assert.Equal("scn_00000000a001", arguments[arguments.ToList().IndexOf("--scene") + 1]);
        w.Play.PlayAnotherInstance();
        TickUntil(() => _launcher.Games.Count == 2);
        TickUntil(() => w.Play.Service.Instances.Count(i => i.State == PlayInstanceState.Running) == 2);
        w.Commands.Execute("play.stop");
        TickUntil(() => !w.Play.IsPlaying);
    }

    // ── Editor settings, resources, autosave ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void EditorSettingsPersistApplyTheAccentAndChooseTheCodeEditor()
    {
        var settingsPath = Path.Combine(_project.Root, "settings.json");
        var overlay = Path.Combine(_project.Root, "overlay");
        var w = Open(o => o with { EditorSettingsPath = settingsPath, ThemeOverlayDirectory = overlay }).Workspace;
        w.EditorSettingsDialog.Open();
        w.EditorSettingsDialog.SetAccent("#22c55e");
        w.EditorSettingsDialog.SetCodeEditorPreset(2); // Rider
        w.EditorSettingsDialog.Working.AutosaveMinutes = 5;
        w.EditorSettingsDialog.Apply();

        var saved = EditorSettings.Load(settingsPath);
        Assert.Equal("#22c55e", saved.Accent);
        Assert.Equal(5, saved.AutosaveMinutes);
        Assert.StartsWith("rider", saved.CodeEditorCommand, StringComparison.Ordinal);
        var theme = File.ReadAllText(Path.Combine(overlay, "Editor", "theme.rcss"));
        Assert.Contains("#22c55e", theme, StringComparison.Ordinal);
        Assert.DoesNotContain("#3b82f6", theme, StringComparison.Ordinal);

        Assert.True(w.CodeEditor.Open(_project.Abs("project.mfproj"), 12));
        var start = _opened[^1];
        Assert.EndsWith("rider", start.FileName, StringComparison.Ordinal);
        Assert.Equal(["--line", "12", _project.Abs("project.mfproj")], start.ArgumentList);

        // Back to the default accent removes the overlay.
        w.ApplySettings(new EditorSettings());
        Assert.False(File.Exists(Path.Combine(overlay, "Editor", "theme.rcss")));
    }

    [Fact]
    public void ResourceFilesOpenInTheInspectorAndTheBusLayoutInspectorEditsWithUndo()
    {
        var w = Open().Workspace;
        var file = _project.Abs("Content/Buses.mres");
        ResourceSaver.Save(AudioBusLayout.CreateDefault(), file);
        Assert.True(w.Inspector.InspectResourceFile(file));
        _editor!.Tick();
        var resource = w.Inspector.InspectedResource!;
        var layout = (AudioBusLayout)resource.Resource;
        var buses = layout.Buses.Count;
        Assert.Contains("mixer", w.Inspector.Document.GetElementById("inspector-body").InnerRml, StringComparison.Ordinal);
        Assert.DoesNotContain(w.Inspector.Rows, r => r.Name == "Buses");

        new AudioBusLayoutInspector().OnAction(layout, "bus-add", resource);
        Assert.Equal(buses + 1, layout.Buses.Count);
        new AudioBusLayoutInspector().OnAction(layout, "bus-up:1", resource);
        Assert.Equal(1f, layout.Buses[1].VolumeDb);
        Assert.True(resource.IsDirty);
        resource.History.Undo();
        Assert.Equal(0f, layout.Buses[1].VolumeDb);
        Assert.True(w.Inspector.SaveResource());
        Assert.Equal(buses + 1, ((AudioBusLayout)PackedSceneFree.LoadResource(file)).Buses.Count);

        // Selecting a node returns to the scene.
        w.Session.Active!.Selection.Set(w.Session.Active.Root);
        _editor.Tick();
        Assert.Null(w.Inspector.InspectedResource);
        Assert.Same(w.Session.Active.Root, w.Inspector.Target);
    }

    [Fact]
    public void AutosaveWritesDirtyScenesWithAFile()
    {
        var w = Open().Workspace;
        w.ApplySettings(new EditorSettings { AutosaveMinutes = 1 });
        var scene = w.Session.Active!;
        scene.AddNode(new Node3D { Name = "Autosaved" }, scene.Root);
        Assert.True(scene.IsDirty);
        // 61 s of 50 ms frames (one 61 s frame would count only the physics steps it ran, as in Godot).
        for (var i = 0; i < 1220; i++)
            _editor!.Tree.Tick(new GameTime { DeltaTime = 0.05f });
        Assert.False(scene.IsDirty);
        Assert.Contains("Autosaved", File.ReadAllText(scene.FilePath!), StringComparison.Ordinal);
    }

    [Fact]
    public void TheInspectorNameColumnFitsNamesAndKeepsADraggedWidth()
    {
        var w = Open().Workspace;
        w.Session.Active!.Selection.Set(w.Session.Active.Root);
        _editor!.Tick();
        var auto = w.Inspector.LabelWidth;
        Assert.InRange(auto, InspectorPanel.MinLabelWidth, 330f * 0.5f);
        w.Inspector.SetLabelWidth(90f);
        Assert.Equal(90f, w.Inspector.LabelWidth);
        Assert.Equal(90f, w.Layout.Settings.InspectorLabelWidth);
        Assert.Equal("Unique Name In…", InspectorPanel.Fit("Unique Name In Owner", 15 * 6.2f));
        Assert.Equal("Scale", InspectorPanel.Fit("Scale", 100f));
        w.Inspector.SetLabelWidth(0);
        Assert.Equal(auto, w.Inspector.LabelWidth);
    }
}

/// <summary>Loads a resource file fresh from disk (bypassing the cache) for assertions.</summary>
internal static class PackedSceneFree
{
    public static Resource LoadResource(string file)
    {
        ResourceLoader.ClearCache();
        return ResourceLoader.Load(AssetDatabase.Current.ToProjectPath(file));
    }
}

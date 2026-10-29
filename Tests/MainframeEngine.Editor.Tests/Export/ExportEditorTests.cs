using MainframeEngine.Editor.Tests.Projects;
using MainframeEngine.Tests;
using Silk.NET.Input;

namespace MainframeEngine.Editor.Tests.Export;

/// <summary>A runner that records its runs and finishes when the test says so.</summary>
internal sealed class FakeExportRunner : IExportRunner
{
    private TaskCompletionSource<IReadOnlyList<ExportResult>> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public List<IReadOnlyList<ExportPreset>> Runs { get; } = [];
    public IProgress<ExportProgress>? Progress { get; private set; }
    public Action<string>? OnLine { get; private set; }
    public CancellationToken Token { get; private set; }
    public IReadOnlyList<ExportIssue> Issues { get; set; } = [];
    public ManualResetEventSlim Started { get; } = new();

    public IReadOnlyList<ExportIssue> Validate(string projectDirectory, ProjectSettings project, ExportPreset preset, AssetDatabase? database = null) => Issues;

    public Task<IReadOnlyList<ExportResult>> RunAllAsync(string projectDirectory, IReadOnlyList<ExportPreset> presets, IProgress<ExportProgress>? progress,
        Action<string>? onLine, CancellationToken cancellationToken)
    {
        lock (Runs)
            Runs.Add(presets);
        Progress = progress;
        OnLine = onLine;
        Token = cancellationToken;
        cancellationToken.Register(() => _completion.TrySetResult([.. presets.Select(p => Result(p.Name, cancelled: true))]));
        Started.Set();
        return _completion.Task;
    }

    public void Finish(params ExportResult[] results)
    {
        _completion.TrySetResult(results);
        _completion = new TaskCompletionSource<IReadOnlyList<ExportResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public static ExportResult Result(string preset, bool cancelled = false, string? artifact = "/out/G-1.0.0-linux-x64.tar.gz",
        IReadOnlyList<BuildDiagnostic>? diagnostics = null) =>
        new(preset, !cancelled && artifact is not null, cancelled, cancelled ? null : artifact, null, TimeSpan.FromSeconds(2), [], diagnostics ?? [],
            cancelled ? "The export was cancelled." : artifact is null ? "dotnet publish failed (exit code 1)." : null);
}

/// <summary>The editor's export: <see cref="ExportService"/> threading, the presets model, File › Export, the dialog, Play exclusion.</summary>
[Collection(nameof(SerialEditor))]
public sealed class ExportEditorTests : IDisposable
{
    private readonly TestProject _project = new();
    private readonly FakeExportRunner _runner = new();
    private readonly FakeGameBuilder _builder = new();
    private HeadlessEditor? _editor;

    public void Dispose()
    {
        _editor?.Dispose();
        _project.Dispose();
    }

    private EditorWorkspace Open()
    {
        _editor = new HeadlessEditor(configure: o => o with
        {
            InitialProject = _project.Root,
            ExportRunner = _runner,
            GameBuilder = _builder,
            GameLauncher = new FakeGameLauncher(),
        });
        _editor.Tick(3);
        return _editor.Workspace;
    }

    private void TickUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            _editor!.Tick();
            Thread.Sleep(5);
        }

        Assert.True(condition());
    }

    // ── ExportService ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheServiceReportsOnlyFromUpdateOnTheMainThread()
    {
        var building = false;
        using var service = new ExportService(_runner, () => building);
        var mainThread = Environment.CurrentManagedThreadId;
        var events = new List<string>();
        service.Changed += () => events.Add("changed@" + (Environment.CurrentManagedThreadId == mainThread));
        service.Line += line => events.Add(line + "@" + (Environment.CurrentManagedThreadId == mainThread));
        IReadOnlyList<ExportResult>? finished = null;
        service.Finished += results => finished = results;

        Assert.True(service.Start(_project.Root, [ExportPreset.Create("Linux", ExportPlatform.Linux)]));
        Assert.False(service.Start(_project.Root, [ExportPreset.Create("Again", ExportPlatform.Linux)])); // one at a time
        Assert.True(_runner.Started.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        events.Clear();

        var worker = new Thread(() =>
        {
            _runner.OnLine!("dotnet publish …");
            _runner.Progress!.Report(new ExportProgress(0, 1, ExportPhase.Publish, null, "Publishing for linux-x64"));
        });
        worker.Start();
        worker.Join();
        Assert.Empty(events); // queued, not raised
        Assert.True(service.IsRunning);

        service.Update();
        Assert.Equal(["dotnet publish …@True", "changed@True"], events);
        Assert.Equal("Exporting 1/1 · Linux · Publishing for linux-x64", service.Status);
        Assert.Equal(["dotnet publish …"], service.RecentLines);

        _runner.Finish(FakeExportRunner.Result("Linux"));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (finished is null && DateTime.UtcNow < deadline)
        {
            service.Update();
            Thread.Sleep(2);
        }

        Assert.NotNull(finished);
        Assert.True(Assert.Single(finished).Succeeded);
        Assert.False(service.IsRunning);
        Assert.Equal("", service.Status);
    }

    [Fact]
    public void AnIdleUpdateAllocatesNothing()
    {
        using var service = new ExportService(_runner, () => false);
        service.Update();
        Assert.Equal(0, AllocationGate.SmallestWindow(() =>
        {
            for (var i = 0; i < 1000; i++)
                service.Update();
        }));
    }

    [Fact]
    public void AnExportWaitsForARunningBuildAndCanBeCancelled()
    {
        var building = true;
        using var service = new ExportService(_runner, () => building);
        Assert.True(service.Start(_project.Root, [ExportPreset.Create("Linux", ExportPlatform.Linux)]));
        service.Update();
        Assert.True(service.IsWaiting);
        Assert.Equal("Waiting for the build…", service.Status);
        Assert.Empty(_runner.Runs);

        building = false;
        service.Update();
        Assert.False(service.IsWaiting);
        Assert.True(_runner.Started.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        service.Cancel();
        Assert.True(_runner.Token.IsCancellationRequested);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (service.IsRunning && DateTime.UtcNow < deadline)
        {
            service.Update();
            Thread.Sleep(2);
        }

        Assert.True(Assert.Single(service.LastResults).Cancelled);
    }

    // ── The presets model ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ThePresetsModelEditsWithUndo()
    {
        var model = new ExportPresetsModel(ProjectSettings.Load(_project.Root), Path.Combine(_project.Root, ProjectSettings.FileName));

        Assert.Null(model.Add(ExportPlatform.Linux, ExportHost.Linux, "linux-x64"));
        Assert.Null(model.Add(ExportPlatform.Linux, ExportHost.Linux, "linux-x64"));
        Assert.Equal(["Linux", "Linux 2"], model.Presets.Select(p => p.Name));
        Assert.Equal("linux-x64", model.Presets[0].Desktop.Rid);

        Assert.Null(model.Set(0, "demo", "true"));
        Assert.Null(model.Set(0, "symbols", "separate"));
        Assert.Null(model.Set(0, "output", "builds/linux"));
        Assert.Equal("Another preset is named 'Linux 2'.", model.Set(0, "name", "Linux 2"));
        Assert.Equal("The output folder must not be inside Assets/.", model.Set(0, "output", "Assets/out"));
        Assert.Contains("not true or false", model.Set(0, "archive", "maybe"), StringComparison.Ordinal);
        Assert.Null(model.AddGlob(0, include: false, "Assets/Audio/**"));
        Assert.Contains("must be project-relative", model.AddGlob(0, include: true, "/abs"), StringComparison.Ordinal);
        Assert.Null(model.Duplicate(0));
        Assert.Equal("Linux copy", model.Presets[1].Name);
        Assert.True(model.Presets[1].Demo);
        Assert.Null(model.Remove(2));

        var linux = model.Presets[0];
        Assert.Equal((true, ExportSymbols.Separate, "builds/linux"), (linux.Demo, linux.Desktop.Symbols, linux.Output));
        Assert.Equal([.. ExportPreset.DefaultExclude, "Assets/Audio/**"], linux.Exclude);
        Assert.True(model.IsDirty);

        model.History.Undo(); // the removal
        Assert.Equal(3, model.Presets.Count);
        Assert.True(model.Save());
        Assert.Equal(["Linux", "Linux copy", "Linux 2"], ProjectSettings.Load(_project.Root).ExportPresets.Select(p => p.Name));
    }

    [Fact]
    public void TheAddMenuPutsThisMachineFirstAndMobileLater()
    {
        var items = ExportDialog.AddMenuItems(ExportHost.Windows);
        var commands = items.Where(i => i.Command is not null).Select(i => i.Command).ToArray();
        Assert.Equal(["add:windows", "add:macos", "add:linux"], commands);
        Assert.Contains(items, i => i.Label == "Android" && !i.Enabled);
        Assert.Contains("need a Mac", items.Single(i => i.Command == "add:macos").Tooltip, StringComparison.Ordinal);
        Assert.Equal("add:macos", ExportDialog.AddMenuItems(ExportHost.MacOS).First(i => i.Command is not null).Command);
    }

    // ── The editor ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FileExportIsInTheFileMenuWithItsShortcut()
    {
        var w = Open();
        var item = w.Commands.MenuItems("file").Single(i => i.Command == "file.export");
        Assert.Equal(("Export…", "Ctrl+Shift+E", "package-export", true), (item.Label, item.Shortcut, item.Icon, item.Enabled));
        Assert.Equal("file.export", EditorWorkspace.ProjectShortcutFor(Key.E, EditorModifiers.Command | EditorModifiers.Shift));
        Assert.Equal("icon icon-sm icon-package-export icon-resource", OutputCategories.IconOf("export"));

        w.Project.Close();
        Assert.False(w.Commands.MenuItems("file").Single(i => i.Command == "file.export").Enabled);
    }

    [Fact]
    public void TheDialogEditsSavesAndExports()
    {
        var w = Open();
        Assert.True(w.Commands.Execute("file.export"));
        _editor!.Tick(2);
        var dialog = w.ExportDialog;
        Assert.True(dialog.Visible);
        Assert.Equal(-1, dialog.Selected);

        Assert.Null(dialog.AddPreset(ExportPlatform.Linux));
        _editor.Tick();
        Assert.Equal(0, dialog.Selected);
        Assert.Null(dialog.Model!.Set(0, "version", "2.0.0"));
        Assert.True(dialog.StartExport(all: false)); // saves the presets first
        Assert.Equal("2.0.0", ProjectSettings.Load(_project.Root).ExportPresets.Single().Version);
        Assert.True(_runner.Started.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal(["Linux"], _runner.Runs.Single().Select(p => p.Name));
        Assert.True(w.Export.IsRunning);
        Assert.Empty(_editor.RmlMessages);

        // While it runs, Play and Build & Reload wait.
        w.Play.PlayMain();
        Assert.Equal(0, _builder.Calls);
        Assert.False(w.Commands.MenuItems("project").Single(i => i.Command == "project.build_reload").Enabled);
        Assert.False(w.Project.BuildAndReload());

        _runner.OnLine!("dotnet publish x");
        _runner.OnLine!($"{_project.Abs("Workflow/A.cs")}(1,2): error CS1002: ; expected"); // reported once, parsed, at the end
        _runner.Finish(FakeExportRunner.Result("Linux"));
        TickUntil(() => !w.Export.IsRunning);
        var output = w.Output.Messages.Where(m => m.Category == "export").Select(m => m.Text).ToArray();
        Assert.Contains("dotnet publish x", output);
        Assert.DoesNotContain(output, t => t.Contains("CS1002", StringComparison.Ordinal));
        Assert.Contains(output, t => t.StartsWith("Linux: exported in", StringComparison.Ordinal));
        Assert.Empty(_editor.RmlMessages);

        // Closing with unsaved edits asks; Don't Save keeps the file.
        Assert.Null(dialog.Model!.Set(0, "demo", "true"));
        dialog.RequestClose();
        Assert.Equal("Unsaved Export Presets", w.Message.Current?.Title);
        w.Message.Answer(1);
        Assert.False(dialog.Visible);
        Assert.False(ProjectSettings.Load(_project.Root).ExportPresets.Single().Demo);
    }

    [Fact]
    public void BuildDiagnosticsOfAFailedExportGoToTheOutputWithTheirSource()
    {
        var w = Open();
        w.ExportDialog.Open();
        w.ExportDialog.AddPreset(ExportPlatform.Linux);
        Assert.True(w.ExportDialog.StartExport(all: true));
        Assert.True(_runner.Started.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        var file = _project.Abs("Workflow/A.cs");
        _runner.Finish(FakeExportRunner.Result("Linux", artifact: null,
            diagnostics: [new BuildDiagnostic(BuildDiagnosticSeverity.Error, "CS1002", "; expected", file, 3, 5, null)]));
        TickUntil(() => !w.Export.IsRunning);

        var error = w.Output.Messages.Single(m => m.Category == "export" && m.Text.Contains("CS1002", StringComparison.Ordinal));
        Assert.Equal((OutputLevel.Error, file, 3), (error.Level, error.CallerFile, error.CallerLine));
        Assert.Contains(w.Output.Messages, m => m.Category == "export" && m.Level == OutputLevel.Error && m.Text.StartsWith("Linux: failed", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidationShowsInTheDialog()
    {
        _runner.Issues = [new ExportIssue(ExportIssueSeverity.Error, "steam-app-id", "Steam is on, but the project has no app id.", "Set it.")];
        var w = Open();
        w.ExportDialog.Open();
        w.ExportDialog.AddPreset(ExportPlatform.Windows);
        _editor!.Tick();

        Assert.Equal("steam-app-id", Assert.Single(w.ExportDialog.IssuesOf(0)).Code);
        Assert.Empty(_editor.RmlMessages);
    }
}

namespace MainframeEngine.Editor.Tests.Updates;

[Collection(nameof(SerialEditor))]
public sealed class UpdateUiTests : IDisposable
{
    private readonly FakeUpdateService _service = new();
    private readonly List<string> _tempDirectories = [];
    private HeadlessEditor? _editor;

    public void Dispose()
    {
        _editor?.Dispose();
        foreach (var directory in _tempDirectories)
            Directory.Delete(directory, recursive: true);
    }

    private HeadlessEditor Start(Func<EditorWorkspaceOptions, EditorWorkspaceOptions>? configure = null)
    {
        _editor = new HeadlessEditor(configure: o => (configure?.Invoke(o) ?? o) with { Updates = _service });
        _editor.Tick(2);
        return _editor;
    }

    private EditorWorkspace W => _editor!.Workspace;

    [Fact]
    public void WithoutAServiceThereIsNoBadgeAndNoCheck()
    {
        _editor = new HeadlessEditor();
        _editor.Tick(2);
        Assert.False(W.Updates.IsEnabled);
        Assert.False(W.Toolbar.UpdateBadgeVisible);
        W.Commands.Execute("help.check_updates");
        Assert.Equal("Updates are not available in this run.", W.Message.Current!.Message);
    }

    [Fact]
    public void AnAvailableUpdateShowsBothBadges()
    {
        _service.CheckResult = FakeUpdateService.Update();
        Start();
        Assert.Equal(1, _service.Checks);
        Assert.True(W.Toolbar.UpdateBadgeVisible);
        Assert.True(W.ProjectManager.UpdateBadgeVisible);
        Assert.Null(W.Message.Current); // quiet: no dialog at start-up
        Assert.False(W.UpdateDialog.Visible);
    }

    [Fact]
    public void OfflineAtStartUpIsQuiet()
    {
        _service.CheckResult = new UpdateCheckResult(UpdateCheckStatus.Failed, Error: "Could not reach GitHub (offline).");
        Start();
        Assert.False(W.Toolbar.UpdateBadgeVisible);
        Assert.Null(W.Message.Current);
        Assert.Contains(W.Output.Messages, m => m.Text.Contains("Could not reach GitHub", StringComparison.Ordinal));
    }

    [Fact]
    public void TheSettingTurnsOffOnlyTheStartUpCheck()
    {
        var directory = Directory.CreateTempSubdirectory("mf-update-settings").FullName;
        _tempDirectories.Add(directory);
        var settings = Path.Combine(directory, "editor_settings.json");
        new EditorSettings { CheckForUpdates = false }.Save(settings);
        _service.CheckResult = new UpdateCheckResult(UpdateCheckStatus.UpToDate);
        Start(o => o with { EditorSettingsPath = settings });
        Assert.Equal(0, _service.Checks);

        W.Commands.Execute("help.check_updates");
        _editor!.Tick();
        Assert.Equal(1, _service.Checks);
        Assert.Equal("Mainframe Engine v1.0.0 is the latest version.", W.Message.Current!.Message);
    }

    [Fact]
    public void AManualCheckDuringTheStartUpCheckSendsOneRequest()
    {
        _service.PendingCheck = new TaskCompletionSource<UpdateCheckResult>();
        Start();
        W.Commands.Execute("help.check_updates");
        _service.PendingCheck.SetResult(FakeUpdateService.Update());
        _editor!.Tick();
        Assert.Equal(1, _service.Checks);
        Assert.True(W.UpdateDialog.Visible); // the manual check opens the dialog for an update
    }

    [Fact]
    public void UpdateAndRestartDownloadsThenQuitsIntoTheApplier()
    {
        _service.CheckResult = FakeUpdateService.Update();
        Start();
        W.Commands.Execute("help.update");
        _editor!.Tick();
        Assert.True(W.UpdateDialog.Visible);
        Assert.Equal("Update & restart", W.UpdateDialog.PrimaryLabel);

        W.Updates.Primary();
        _editor.Tick();
        Assert.Equal(UpdateState.Downloading, W.Updates.State);
        Assert.Equal(0.5, W.Updates.Progress);
        W.Updates.Primary(); // a second click while downloading does nothing
        Assert.Equal(1, _service.Downloads);

        _service.Download.SetResult(FakeUpdateService.Staged());
        _editor.Tick();
        var (staged, project) = Assert.Single(_service.Started);
        Assert.Equal(FakeUpdateService.Staged(), staged);
        Assert.Null(project);
        Assert.True(_editor.Host.QuitRequested);
        Assert.Empty(_editor.RmlMessages);
    }

    [Fact]
    public void UnsavedScenesAreAskedAboutBeforeRestarting()
    {
        _service.CheckResult = FakeUpdateService.Update();
        Start();
        _editor!.Scene.AddNode(new Node3D { Name = "X" }, _editor.Scene.Root);
        _editor.Tick();
        W.Updates.Primary();
        _service.Download.SetResult(FakeUpdateService.Staged());
        _editor.Tick();

        Assert.Equal("Unsaved changes", W.Message.Current!.Title);
        W.Message.Answer(2); // cancel: no update, staged files kept for the next click
        Assert.Empty(_service.Started);
        Assert.Equal(UpdateState.Ready, W.Updates.State);
        Assert.Equal("Restart now", W.UpdateDialog.PrimaryLabel);

        W.Updates.Primary();
        W.Message.Answer(1); // don't save
        Assert.Single(_service.Started);
        Assert.True(_editor.Host.QuitRequested);
    }

    [Fact]
    public void AnInstallThatCannotBeReplacedDownloadsAndReveals()
    {
        _service.CheckResult = FakeUpdateService.Update();
        _service.Install = new InstallLocation("/private/var/x/AppTranslocation/y/Mainframe Engine.app", InstallKind.Translocated);
        Start();
        W.UpdateDialog.Open();
        Assert.Equal("Download", W.UpdateDialog.PrimaryLabel);
        Assert.Contains("Applications", W.Updates.InstallHint, StringComparison.Ordinal);

        W.Updates.Primary();
        _service.Download.SetResult(FakeUpdateService.Staged());
        _editor!.Tick();
        Assert.Single(_service.Revealed);
        Assert.Empty(_service.Started);
        Assert.False(_editor.Host.QuitRequested);
    }

    [Fact]
    public void AFailedDownloadOffersRetry()
    {
        _service.CheckResult = FakeUpdateService.Update();
        Start();
        W.UpdateDialog.Open();
        W.Updates.Primary();
        _service.Download.SetException(new UpdateException("The download is damaged (its checksum does not match the release). Try again."));
        _editor!.Tick();
        Assert.Equal(UpdateState.Failed, W.Updates.State);
        Assert.StartsWith("The download is damaged", W.Updates.Error, StringComparison.Ordinal);
        Assert.Equal("Retry", W.UpdateDialog.PrimaryLabel);

        W.Updates.Primary();
        Assert.Equal(UpdateState.Downloading, W.Updates.State);
        Assert.Null(W.Updates.Error);
        Assert.Equal(2, _service.Downloads);
    }

    [Fact]
    public void CancelStopsTheDownload()
    {
        _service.CheckResult = FakeUpdateService.Update();
        Start();
        W.Updates.Primary();
        W.Updates.Cancel();
        _editor!.Tick();
        Assert.Equal(UpdateState.Idle, W.Updates.State);
        Assert.Null(W.Updates.Error);
    }

    [Fact]
    public void AnApplierThatCannotStartKeepsTheEditorOpen()
    {
        _service.CheckResult = FakeUpdateService.Update();
        _service.StartError = new UpdateException("The new version could not be started (denied).");
        Start();
        W.Updates.Primary();
        _service.Download.SetResult(FakeUpdateService.Staged());
        _editor!.Tick();
        Assert.False(_editor.Host.QuitRequested);
        Assert.Equal(UpdateState.Failed, W.Updates.State);
        Assert.Contains("denied", W.Updates.Error, StringComparison.Ordinal);
        Assert.True(W.UpdateDialog.Visible);
    }

    [Fact]
    public void TheLastUpdateIsReportedInTheOutput()
    {
        _service.LastResult = new UpdateResult("1.0.0", "1.1.0", false, "Disk full");
        Start();
        Assert.Contains(W.Output.Messages, m => m.Level == OutputLevel.Error && m.Text.Contains("v1.1.0", StringComparison.Ordinal)
                                                 && m.Text.Contains("Disk full", StringComparison.Ordinal));
    }

    [Fact]
    public void ReleaseNotesWithMarkupAreShownAsText()
    {
        _service.CheckResult = FakeUpdateService.Update(notes: "<b>bold</b> & <script>alert(1)</script> {{title}}");
        Start();
        W.UpdateDialog.Open();
        _editor!.Tick(2);
        Assert.Empty(_editor.RmlMessages);
        var shown = W.UpdateDialog.Document.GetElementById("up-notes").InnerRml;
        Assert.DoesNotContain("<script", shown, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>", shown, StringComparison.Ordinal);
        Assert.Contains("&lt;b&gt;bold", shown, StringComparison.Ordinal);
        Assert.Contains("<b>bold</b>", W.Updates.Available!.Release!.Notes, StringComparison.Ordinal);
    }
}

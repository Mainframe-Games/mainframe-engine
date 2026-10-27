namespace MainframeEngine.Editor.Tests;

/// <summary>G15: the Output panel's crash rows and the editor's own next-start crash dialog, headless.</summary>
[Collection(nameof(SerialEditor))]
public sealed class CrashUiTests : IDisposable
{
    private readonly List<Uri> _opened = [];
    private readonly HeadlessEditor _editor;

    public CrashUiTests() => _editor = new HeadlessEditor(configure: o => o with { OpenUrl = _opened.Add });

    public void Dispose() => _editor.Dispose();

    private EditorWorkspace W => _editor.Workspace;

    [Fact]
    public void ACrashRowExpandsToItsFramesAndOpensGameFrames()
    {
        var opened = new List<(string File, int Line)>();
        W.SourceOpener = (file, line) =>
        {
            opened.Add((file, line));
            return true;
        };
        var gameFile = typeof(CrashUiTests).Assembly.Location;
        W.Output.Clear();
        W.Output.AddCrash(new OutputCrash
        {
            Title = "Game #1 crashed: NullReferenceException in PlayerController.OnPhysicsProcess",
            Detail = "/root/Player · Assets/Scenes/Main.mscene",
            Frames =
            [
                new OutputCrashFrame("at Game.PlayerController.OnPhysicsProcess(Single) in Player.cs:42", gameFile, 42, IsGame: true),
                new OutputCrashFrame("at MainframeEngine.SceneTree.RunPhysicsStepCore(Single)", "/engine/SceneTree.cs", 9, IsGame: false),
            ],
        });
        _editor.Tick(2);
        var panel = W.OutputPanel;
        var index = panel.Rows.Count - 1;
        var row = panel.Rows[index];
        Assert.NotNull(row.Message.Crash);
        Assert.Equal(OutputLevel.Error, row.Message.Level);
        Assert.Empty(row.Frames); // folded

        Assert.True(panel.ToggleCrash(index));
        _editor.Tick(2);
        Assert.Equal(2, row.Frames.Count);
        var document = panel.Document.AsElement();
        // data-if hides elements (they stay in the DOM): the shown link has a size, the engine frame's has none.
        Assert.All(document.QuerySelectorAll(".crash-frames .frame.engine .source"), s => Assert.Equal(0f, s.Bounds.Width));
        var link = document.QuerySelectorAll(".crash-frames .frame.game .source").Single(s => s.Bounds.Width > 0);
        var line = document.QuerySelectorAll(".line.crash").Last();
        Assert.True(link.Bounds.X + link.Bounds.Width <= line.Bounds.X + line.Bounds.Width + 0.5f,
            $"the frame's link ({link.Bounds}) is inside its row ({line.Bounds})");
        Assert.True(panel.OpenCrashFrame(index, 0));
        Assert.False(panel.OpenCrashFrame(index, 1)); // engine frame: muted, not a link
        Assert.Equal([(gameFile, 42)], opened);
        Assert.False(panel.OpenCrashFolder(index)); // no bundle
        Assert.False(panel.OpenCrashDump(index));
        Assert.Contains("PlayerController.OnPhysicsProcess", panel.CopyCrash(index), StringComparison.Ordinal);
        Assert.False(panel.ToggleCrash(-1));
        Assert.Empty(_editor.RmlMessages);
    }

    [Fact]
    public void TheCrashDialogOffersTheRecoveryCopiesAndAnIssueLinkWithOnlyTheVersion()
    {
        var folder = Path.Combine(_editor.Directory, "crashes", "2026-10-11T10-42-07-0123");
        Directory.CreateDirectory(folder);
        var copy = Path.Combine(_editor.Directory, "recovery", "Main-20261011-104207.mscene");
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        File.WriteAllBytes(copy, SceneSaver.ToJson(new Node3D { Name = "Recovered" }));
        var report = new CrashReport { Id = "0123456789abcdef0123456789abcdef", Kind = CrashKind.ThreadException, Timestamp = DateTime.UtcNow };
        report.App.Version = "9.8.7";
        report.Exception.Add(new CrashException { Type = "System.InvalidOperationException", Message = "secret details" });
        EditorCrashReporting.ListRecoveryCopies(report, [copy, Path.Combine(_editor.Directory, "gone.mscene")]);
        File.WriteAllBytes(Path.Combine(folder, CrashReport.FileName), report.ToJson());
        var info = new CrashReportInfo(folder, report.Id, report.Kind, report.Timestamp, report.Summary);

        Assert.True(W.CrashDialog.Open([info]));
        _editor.Tick(2);

        Assert.True(W.CrashDialog.Visible);
        Assert.Equal([copy], W.CrashDialog.RecoveryCopies); // the missing one is not offered
        var tabs = W.Session.Scenes.Count;
        Assert.True(W.CrashDialog.OpenCopy(0));
        _editor.Tick(2);
        Assert.Equal(tabs + 1, W.Session.Scenes.Count);

        var issue = W.CrashDialog.ReportIssue();
        Assert.Equal(issue, Assert.Single(_opened));
        Assert.Contains("v9.8.7", Uri.UnescapeDataString(issue.Query), StringComparison.Ordinal);
        Assert.DoesNotContain("secret", issue.AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains("secret details", _editor.Server.ClipboardText, StringComparison.Ordinal); // the report went to the clipboard

        W.CrashDialog.Close();
        Assert.False(W.CrashDialog.Visible);
        Assert.True(File.Exists(Path.Combine(folder, CrashReporter.HandledFileName))); // offered once
        Assert.Empty(_editor.RmlMessages);
    }
}

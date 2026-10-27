namespace MainframeEngine.Editor;

/// <summary>
/// G15 samples for QA captures (<c>crash-row</c>, <c>crash-dialog</c>): a played game's crash row in the Output panel and
/// the editor's own next-start crash dialog, built from the same types a real crash produces.
/// </summary>
internal static class QaCrashSamples
{
    /// <summary>Adds an expanded crash row whose first frame points at a C# file of the open project (click-to-source).</summary>
    public static void AddCrashRow(EditorWorkspace workspace)
    {
        var source = workspace.Session.ProjectRoot is { } root && Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && !f.Contains("Tests", StringComparison.Ordinal))
                .OrderBy(f => f.Contains("Player", StringComparison.Ordinal) ? 0 : 1).ThenBy(f => f, StringComparer.Ordinal).FirstOrDefault()
            : null;
        var report = new CrashReport { Id = "0123456789abcdef0123456789abcdef", Kind = CrashKind.ManagedException, Timestamp = DateTime.UtcNow };
        report.Session.Node = "/root/Main/Player";
        report.Session.NodeType = "Demo.PlayerController";
        report.Session.Callback = "OnPhysicsProcess";
        report.Session.Scene = "Assets/Scenes/basic_3d.mscene";
        report.Exception.Add(new CrashException
        {
            Type = "System.NullReferenceException",
            Message = "Object reference not set to an instance of an object.",
            Frames =
            [
                new CrashFrame { Method = "Demo.PlayerController.OnPhysicsProcess(Single)", File = source, Line = source is null ? null : 12 },
                new CrashFrame { Method = "MainframeEngine.SceneTree.RunPhysicsStepCore(Single)", File = "SceneTree.cs", Line = 559 },
                new CrashFrame { Method = "MainframeEngine.SceneTree.Tick(GameTime&)", File = "SceneTree.cs", Line = 470 },
                new CrashFrame { Method = "MainframeEngine.Engine.OnUpdate(Double)", File = "Engine.cs", Line = 512 },
            ],
        });
        var instance = new PlayInstance(1, "Game", null, DateTime.UtcNow, []) { Crash = report, ExitCode = 1, ProcessId = 4242 };
        var crash = OutputCrash.From(instance);
        crash.Expanded = true;
        workspace.Output.AddCrash(crash);
        workspace.Output.Drain();
        workspace.OutputPanel.Refresh();
    }

    /// <summary>Opens the crash dialog for a sample editor crash whose report lists a recovery copy of the open scene.</summary>
    public static void ShowCrashDialog(EditorWorkspace workspace, string outputDirectory)
    {
        var folder = Path.Combine(outputDirectory, "qa-crash", "2026-10-28T10-42-07-0123");
        Directory.CreateDirectory(folder);
        var copy = Path.Combine(outputDirectory, "qa-crash", "recovery", "basic_3d-20261028-104207.mscene");
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        if (workspace.Session.Active is { } scene)
            File.WriteAllBytes(copy, SceneSaver.ToJson(scene.Root));
        else
            File.WriteAllBytes(copy, SceneSaver.ToJson(new Node3D { Name = "Recovered" }));
        var report = new CrashReport { Id = "0123456789abcdef0123456789abcdef", Kind = CrashKind.ManagedException, Timestamp = DateTime.UtcNow };
        report.App.Name = "Mainframe Editor";
        report.App.Version = EngineInfo.Version;
        report.App.Editor = true;
        report.Exception.Add(new CrashException
        {
            Type = "System.InvalidOperationException",
            Message = "Collection was modified; enumeration operation may not execute.",
            Frames = [new CrashFrame { Method = "MainframeEngine.Editor.InspectorPanel.Rebuild()" }],
        });
        EditorCrashReporting.ListRecoveryCopies(report, [copy]);
        File.WriteAllBytes(Path.Combine(folder, CrashReport.FileName), report.ToJson());
        workspace.CrashDialog.Open([new CrashReportInfo(folder, report.Id, report.Kind, report.Timestamp, report.Summary)]);
    }
}

using System.Text.Json;

namespace MainframeEngine.Tests.Crash;

[Collection(nameof(CrashReporterState))]
public sealed class CrashSessionTests
{
    private const int DeadPid = 999_999_001;

    private static void WriteSentinel(string crashes, int pid, string? logFile, string? reportId = null)
    {
        Directory.CreateDirectory(crashes);
        var file = new CrashSessionFile
        {
            ProcessId = pid,
            StartedUtc = DateTime.UtcNow.AddMinutes(-3),
            App = new CrashAppInfo { Name = "CrashTest", Version = "0.9" },
            Os = new CrashOsInfo { Description = "TestOS" },
            LogFile = logFile,
            ReportId = reportId,
            Args = ["--scene", "Assets/Scenes/A.mscene"],
        };
        File.WriteAllBytes(CrashSession.SessionPath(crashes, pid), JsonSerializer.SerializeToUtf8Bytes(file, CrashJsonContext.Default.CrashSessionFile));
        File.WriteAllBytes(CrashSession.LockPath(crashes, pid), []); // left behind, unlocked: the process is gone
    }

    [Fact]
    public void ACleanExitDeletesTheSentinel()
    {
        using var scratch = new CrashScratch();
        CrashReporter.Install(scratch.Options());
        var sentinel = CrashSession.SessionPath(scratch.Crashes, Environment.ProcessId);
        Assert.True(File.Exists(sentinel));
        Assert.True(CrashSession.IsAlive(scratch.Crashes, Environment.ProcessId));

        CrashReporter.Shutdown();

        Assert.False(File.Exists(sentinel));
        Assert.False(File.Exists(CrashSession.LockPath(scratch.Crashes, Environment.ProcessId)));
    }

    [Fact]
    public void ADeadSentinelBecomesAnUncleanExitWithTheLogTail()
    {
        using var scratch = new CrashScratch();
        var log = Path.Combine(scratch.Root, "old.log");
        File.WriteAllText(log, "2026-10-11T10:42:07.123Z [INFO] engine Test: the last words\n");
        WriteSentinel(scratch.Crashes, DeadPid, log);

        CrashReporter.Install(scratch.Options());

        Assert.False(File.Exists(CrashSession.SessionPath(scratch.Crashes, DeadPid)));
        var pending = Assert.Single(CrashReporter.PendingReports);
        Assert.Equal(CrashKind.UncleanExit, pending.Kind);
        var report = CrashReporter.Load(pending)!;
        Assert.Equal(DeadPid, report.Session.ProcessId);
        Assert.Equal("0.9", report.App.Version);
        Assert.Equal(["--scene", "Assets/Scenes/A.mscene"], report.Session.Args);
        Assert.Equal(CrashReport.LogTailFileName, report.Log);
        Assert.Contains("the last words", File.ReadAllText(Path.Combine(pending.Folder, CrashReport.LogTailFileName)), StringComparison.Ordinal);
        Assert.Equal("The game stopped unexpectedly", report.Summary);
    }

    [Fact]
    public void ARotatedLogIsFoundUnderItsNewName()
    {
        using var scratch = new CrashScratch();
        var logs = Path.Combine(scratch.Root, "logs");
        Directory.CreateDirectory(logs);
        var current = Path.Combine(logs, "game.log");
        File.WriteAllText(current, "first run\n");
        WriteSentinel(scratch.Crashes, DeadPid, current);
        using var sink = new FileLogSink(logs, "game"); // rotates the first run's file to game.1.log, like GameHost does

        CrashReporter.Install(scratch.Options(log: sink));

        var pending = Assert.Single(CrashReporter.PendingReports);
        Assert.Equal("first run\n", File.ReadAllText(Path.Combine(pending.Folder, CrashReport.LogTailFileName)));
    }

    [Fact]
    public void ASentinelWithAReportMakesNoDuplicate()
    {
        using var scratch = new CrashScratch();
        WriteSentinel(scratch.Crashes, DeadPid, null, reportId: "0123456789abcdef0123456789abcdef");

        CrashReporter.Install(scratch.Options());

        Assert.Empty(CrashReporter.PendingReports);
        Assert.False(File.Exists(CrashSession.SessionPath(scratch.Crashes, DeadPid)));
    }

    [Fact]
    public void ALiveInstanceIsLeftAlone()
    {
        using var scratch = new CrashScratch();
        WriteSentinel(scratch.Crashes, DeadPid, null);
        using (new FileStream(CrashSession.LockPath(scratch.Crashes, DeadPid), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            CrashReporter.Install(scratch.Options());
            Assert.Empty(CrashReporter.PendingReports);
            Assert.True(File.Exists(CrashSession.SessionPath(scratch.Crashes, DeadPid)));
        }
    }

    [Fact]
    public void AFatalReportMarksTheSentinelSoTheNextStartAddsNothing()
    {
        using var scratch = new CrashScratch();
        CrashReporter.Install(scratch.Options());
        var folder = CrashReporter.WriteReport(new InvalidOperationException("fatal"), CrashKind.ManagedException)!;
        var sentinel = JsonSerializer.Deserialize(File.ReadAllBytes(CrashSession.SessionPath(scratch.Crashes, Environment.ProcessId)),
            CrashJsonContext.Default.CrashSessionFile)!;

        Assert.Equal(CrashReporter.LoadFolder(folder)!.Id, sentinel.ReportId);
    }

    [Fact]
    public void ADumpForTheDeadProcessGoesIntoItsBundle()
    {
        using var scratch = new CrashScratch();
        WriteSentinel(scratch.Crashes, DeadPid, null);
        var dump = NativeDumps.PathFor(scratch.Crashes, DeadPid);
        File.WriteAllBytes(dump, [1, 2, 3]);
        File.WriteAllText(dump + ".crashreport.json", "{}");

        var folder = CrashReporter.WriteExitedProcessReport(scratch.Options(), DeadPid, 139);

        Assert.NotNull(folder);
        var report = CrashReporter.LoadFolder(folder!)!;
        Assert.Equal(CrashKind.NativeDump, report.Kind);
        Assert.Equal(139, report.ExitCode);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(Path.Combine(folder!, CrashReport.DumpFileName)));
        Assert.True(File.Exists(Path.Combine(folder!, "crashreport.json")));
        Assert.False(File.Exists(dump));
    }

    [Fact]
    public void TheDumpVariablesNameTheCrashFolder()
    {
        var environment = NativeDumps.Environment("/tmp/crashes", NativeDumpMode.Triage);

        Assert.Equal("1", environment["DOTNET_DbgEnableMiniDump"]);
        Assert.Equal("3", environment["DOTNET_DbgMiniDumpType"]);
        Assert.Equal(Path.Combine("/tmp/crashes", "dump-%p.dmp"), environment["DOTNET_DbgMiniDumpName"]);
        Assert.Equal("1", NativeDumps.Environment("/tmp/crashes", NativeDumpMode.Mini)["DOTNET_DbgMiniDumpType"]);
        Assert.Equal("2", NativeDumps.Environment("/tmp/crashes", NativeDumpMode.Heap)["DOTNET_DbgMiniDumpType"]);
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeDumps.Environment("/tmp/crashes", NativeDumpMode.Off));
    }

    [Fact]
    public void TheSupervisorStaysOutOfEditorDebuggerAndHeadlessRuns()
    {
        var settings = new ProjectSettings { Name = "Supervised" };
        Assert.False(CrashSupervisor.ShouldSupervise(settings, new GameHostOptions())); // nativeDumps off by default
        settings.Crash.NativeDumps = NativeDumpMode.Mini;
        Assert.False(CrashSupervisor.ShouldSupervise(settings, new GameHostOptions { EditorPort = 4000 }));
        Assert.False(CrashSupervisor.ShouldSupervise(settings, new GameHostOptions { Headless = true }));
        settings.Crash.Enabled = false;
        Assert.False(CrashSupervisor.ShouldSupervise(settings, new GameHostOptions()));
    }
}

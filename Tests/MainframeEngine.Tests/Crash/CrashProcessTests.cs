using System.Diagnostics;
using System.Text;
using MainframeEngine.EditorLink;
using MainframeEngine.Tests.Project;

namespace MainframeEngine.Tests.Crash;

/// <summary>
/// End-to-end crash capture (G15): the fixture game (Tests/MainframeEngine.CrashTests.Fixture) runs headless in its own
/// process and crashes for real — a throw in a node callback, a thread, an unobserved task, a fail fast, a stack
/// overflow, a native fault — so the test process never does. Each run gets its own user data folder.
/// </summary>
public sealed class CrashProcessTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    private sealed record Run(int ExitCode, string Output, string Crashes);

    private sealed class Game : IDisposable
    {
        public Game() => UserData = Path.Combine(Path.GetTempPath(), "mf-crashgame-" + Guid.NewGuid().ToString("N")[..10]);

        public string UserData { get; }

        public string Crashes => Path.Combine(UserData, "CrashFixture", "crashes");

        public Run Start(string crash, IReadOnlyDictionary<string, string>? environment = null, params string[] extra)
        {
            var dll = Path.Combine(AppContext.BaseDirectory, "MainframeEngine.CrashTests.Fixture.dll");
            var psi = new ProcessStartInfo(DotnetPath())
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            psi.ArgumentList.Add(dll);
            foreach (var arg in (string[])["--project", Path.Combine(AppContext.BaseDirectory, "CrashFixture", "project.mfproj"),
                         "--headless", "--fixed-fps", "60", "--max-frames", "20", .. extra, "++", "crash=" + crash])
                psi.ArgumentList.Add(arg);
            psi.Environment[UserDataPaths.OverrideVariable] = UserData;
            // Dumps only when a test asks: the runtime writes one for every unhandled exception otherwise.
            foreach (var name in (string[])["DOTNET_DbgEnableMiniDump", "DOTNET_EnableCrashReport", "DOTNET_DbgMiniDumpName"])
                psi.Environment.Remove(name);
            if (environment is not null)
                foreach (var (name, value) in environment)
                    psi.Environment[name] = value;

            var output = new StringBuilder();
            using var process = new Process { StartInfo = psi };
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (!process.WaitForExit(Timeout))
            {
                process.Kill(entireProcessTree: true);
                Assert.Fail($"The fixture (crash={crash}) did not exit:\n{output}");
            }

            process.WaitForExit();
            lock (output)
                return new Run(process.ExitCode, output.ToString(), Crashes);
        }

        public List<CrashReport> Reports()
        {
            if (!Directory.Exists(Crashes))
                return [];
            return Directory.GetDirectories(Crashes)
                .Where(d => !d.EndsWith(".tmp", StringComparison.Ordinal))
                .Select(CrashReporter.LoadFolder)
                .OfType<CrashReport>()
                .OrderBy(r => r.Timestamp)
                .ToList();
        }

        public string[] Sentinels() => Directory.Exists(Crashes) ? Directory.GetFiles(Crashes, "session-*.json") : [];

        public void Dispose()
        {
            try
            {
                Directory.Delete(UserData, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static string DotnetPath()
    {
        var path = Environment.ProcessPath;
        return path is not null && Path.GetFileNameWithoutExtension(path).Equals("dotnet", StringComparison.OrdinalIgnoreCase) ? path : "dotnet";
    }

    [Fact]
    public void AThrowInANodeCallbackWritesAReportNamingTheNode()
    {
        using var game = new Game();

        var run = game.Start("managed");

        Assert.True(run.ExitCode == 1, run.Output);
        var report = Assert.Single(game.Reports());
        Assert.Equal(CrashKind.ManagedException, report.Kind);
        Assert.Equal("/root/Crasher", report.Session.Node);
        Assert.Equal("CrashFixture.Crasher", report.Session.NodeType);
        Assert.Equal("OnPhysicsProcess", report.Session.Callback);
        Assert.Equal("System.InvalidOperationException", report.Exception[0].Type);
        Assert.Contains(report.Exception[0].Frames, f => f.Method.StartsWith("CrashFixture.Crasher.OnPhysicsProcess", StringComparison.Ordinal));
        Assert.True(report.Session.ProcessFrames > 0);
        Assert.True(report.App.Headless);
        Assert.Equal("CrashFixture", report.App.Name);
        Assert.Equal("1.0.0", report.App.Version);
        Assert.Equal("crash=managed", report.Context["fixture"]);
        Assert.Contains("Crashing now: managed", File.ReadAllText(Path.Combine(report.Folder!, CrashReport.LogFileName)), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(report.Folder!, CrashReport.SettingsFileName)));
        Assert.Empty(game.Sentinels()); // the run ended through GameHost: a clean end of the session

        // The next start offers it (headless: logged) and adds no unclean exit.
        var next = game.Start("none");
        Assert.Equal(0, next.ExitCode);
        Assert.Single(game.Reports());
        Assert.Contains("crash report(s) from earlier runs", next.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void AThrowInAChildsReadyNamesTheChild()
    {
        using var game = new Game();

        var run = game.Start("ready");

        Assert.True(run.ExitCode == 1, run.Output);
        var report = Assert.Single(game.Reports());
        Assert.Equal("/root/Crasher/Child", report.Session.Node);
        Assert.Equal("OnReady", report.Session.Callback);
        Assert.Equal("System.ArgumentException", report.Exception[0].Type);
    }

    [Fact]
    public void AThreadExceptionIsReportedAndTheProcessStillDies()
    {
        using var game = new Game();

        var run = game.Start("thread");

        Assert.NotEqual(0, run.ExitCode);
        var report = Assert.Single(game.Reports());
        Assert.Equal(CrashKind.ThreadException, report.Kind);
        Assert.Equal("System.FormatException", report.Exception[0].Type);

        // Its sentinel names the report: the next start adds no unclean exit.
        game.Start("none");
        Assert.Single(game.Reports());
    }

    [Fact]
    public void AnUnobservedTaskIsANonFatalReport()
    {
        using var game = new Game();

        var run = game.Start("task", extra: ["--max-frames", "120"]);

        Assert.True(run.ExitCode == 0, run.Output);
        var report = Assert.Single(game.Reports());
        Assert.Equal(CrashKind.UnobservedTask, report.Kind);
        Assert.Contains(report.Exception, e => e.Type == "System.TimeoutException");
    }

    [Theory]
    [InlineData("failfast")]
    [InlineData("stackoverflow")]
    [InlineData("native")]
    public void AnEndWithoutAReportIsAnUncleanExitOnTheNextStart(string crash)
    {
        if (crash == "native" && OperatingSystem.IsWindows())
            return; // RaiseException is covered by the same sentinel path; raise() is Unix-only in the fixture
        using var game = new Game();

        var run = game.Start(crash, extra: ["--max-frames", "200"]);

        Assert.True(run.ExitCode != 0, run.Output);
        Assert.Empty(game.Reports());
        Assert.Single(game.Sentinels());

        var next = game.Start("none");
        Assert.Equal(0, next.ExitCode);
        var report = Assert.Single(game.Reports());
        Assert.Equal(CrashKind.UncleanExit, report.Kind);
        Assert.Equal(CrashReport.LogTailFileName, report.Log);
        var tail = File.ReadAllText(Path.Combine(report.Folder!, CrashReport.LogTailFileName));
        Assert.Contains("Crasher ready: crash=" + crash, tail, StringComparison.Ordinal); // flushed; the last second may not be
        Assert.Empty(game.Sentinels());
    }

    [Fact]
    public void WithTheDumpVariablesANativeFaultLeavesADump()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var game = new Game();
        Directory.CreateDirectory(game.Crashes);

        var run = game.Start("native", NativeDumps.Environment(game.Crashes, NativeDumpMode.Triage), "--max-frames", "200");

        Assert.True(run.ExitCode != 0, run.Output);
        Assert.Single(game.Sentinels());
        game.Start("none");
        var report = Assert.Single(game.Reports());
        Assert.True(report.Kind == CrashKind.NativeDump, run.Output);
        Assert.True(new FileInfo(Path.Combine(report.Folder!, CrashReport.DumpFileName)).Length > 0);
    }

    [Fact]
    public void TheEditorGetsTheCrashReportBeforeTheGoodbye()
    {
        using var game = new Game();
        using var server = new EditorLinkServer();

        var run = game.Start("managed", extra: ["--editor-port", server.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)]);

        Assert.True(run.ExitCode == 1, run.Output);
        var seen = new List<EditorLinkMessage>();
        Wait.For(server, seen, m => m.Type == EditorLinkMessageType.Goodbye);
        var crashIndex = seen.FindIndex(m => m.Type == EditorLinkMessageType.CrashReport);
        Assert.True(crashIndex >= 0, string.Join(", ", seen.Select(m => m.Type)));
        Assert.True(crashIndex < seen.FindIndex(m => m.Type == EditorLinkMessageType.Goodbye));
        var linkReport = CrashReport.FromJson(seen[crashIndex].CrashReport.Json);
        Assert.Equal("/root/Crasher", linkReport.Session.Node);
        Assert.Equal(Assert.Single(game.Reports()).Folder, seen[crashIndex].CrashReport.Folder);
        Assert.Equal(1, seen.First(m => m.Type == EditorLinkMessageType.Goodbye).ExitCode);
    }
}

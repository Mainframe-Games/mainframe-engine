using System.Diagnostics;
using MainframeEngine.EditorLink;

namespace MainframeEngine.Editor.Tests;

/// <summary>A <see cref="PlayService"/> with fake builder/launcher, the real link, and every event recorded.</summary>
public sealed class PlayHarness : IDisposable
{
    public PlayHarness(Func<DateTime>? clock = null)
    {
        Service = new PlayService(Builder, Launcher, clock);
        Service.Changed += () => ChangedCount++;
        Service.GameLog += (instance, entry) => Logs.Add((instance, entry));
        Service.GameOutput += (instance, line) => Output.Add((instance, line));
        Service.BuildOutput += BuildLines.Add;
        Service.BuildFinished += Builds.Add;
    }

    public FakeGameBuilder Builder { get; } = new();
    public FakeGameLauncher Launcher { get; } = new();
    public PlayService Service { get; }
    public int ChangedCount { get; private set; }
    public List<(PlayInstance Instance, LogEntry Entry)> Logs { get; } = [];
    public List<(PlayInstance Instance, string Line)> Output { get; } = [];
    public List<string> BuildLines { get; } = [];
    public List<GameBuildResult> Builds { get; } = [];

    /// <summary>Calls <see cref="PlayService.Update"/> until <paramref name="condition"/> holds (fails after <paramref name="seconds"/>).</summary>
    public void Pump(Func<bool> condition, string what, double seconds = 10)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            Service.Update();
            if (condition())
                return;
            if (stopwatch.Elapsed.TotalSeconds > seconds)
                Assert.Fail($"Timed out waiting for {what}; instances: {string.Join(", ", Service.Instances)}; logs: {string.Join(" | ", Logs.Select(l => l.Entry.Message))}");
            Thread.Sleep(2);
        }
    }

    public IEnumerable<string> LogMessages(PlayInstance instance) =>
        Logs.Where(l => l.Instance == instance).Select(l => l.Entry.Message);

    public PlayInstance LaunchRunning(string label = "Game", string? scene = null, IReadOnlyList<string>? extra = null)
    {
        var instance = Service.Launch("/fake/MyGame.Desktop", "/fake", scene, label, extra);
        Pump(() => instance.State == PlayInstanceState.Running, $"{label} to connect");
        return instance;
    }

    public FakeGame Game(PlayInstance instance) => Launcher.Games.Single(g => g.Id == instance.ProcessId);

    public void Dispose()
    {
        Service.Dispose();
        foreach (var game in Launcher.Games)
            game.Dispose();
    }
}

public sealed class PlayServiceTests
{
    [Fact]
    public async Task FailedBuildReportsDiagnosticsAndLaunchesNothing()
    {
        using var play = new PlayHarness();
        var diagnostic = new BuildDiagnostic(BuildDiagnosticSeverity.Error, "CS1002", "; expected", "/p/Foo.cs", 3, 9, "/p/MyGame.csproj");
        play.Builder.Result = FakeGameBuilder.Failure(diagnostic);

        var task = play.Service.BuildAndLaunchAsync(new PlayRequest("/p/MyGame.sln", "/p/MyGame.Desktop/MyGame.Desktop.csproj", null), TestContext.Current.CancellationToken);
        Assert.True(play.Service.IsBuilding);
        play.Pump(() => task.IsCompleted, "the build");

        Assert.Null(await task);
        Assert.False(play.Service.IsBuilding);
        Assert.Empty(play.Launcher.Games);
        Assert.Empty(play.Service.Instances);
        Assert.Equal([diagnostic], play.Service.LastBuild!.Diagnostics);
        Assert.Same(play.Service.LastBuild, Assert.Single(play.Builds));
        Assert.Equal(play.Builder.Lines, play.BuildLines);
        Assert.Equal(["/p/MyGame.sln"], play.Builder.Paths);
        Assert.True(play.ChangedCount > 0);
    }

    [Fact]
    public async Task OneBuildAtATime()
    {
        using var play = new PlayHarness();
        using var gate = new ManualResetEventSlim(false);
        play.Builder.Gate = gate;

        var first = play.Service.BuildAsync("/p/A.sln", TestContext.Current.CancellationToken);
        var second = play.Service.BuildAsync("/p/B.sln", TestContext.Current.CancellationToken);
        Assert.Same(first, second);
        for (var i = 0; i < 5; i++)
            play.Service.Update();
        Assert.False(first.IsCompleted);
        Assert.True(play.Service.IsBuilding);

        gate.Set();
        play.Pump(() => first.IsCompleted, "the build");
        Assert.True((await first).Succeeded);
        Assert.Equal(1, play.Builder.Calls);
        Assert.Single(play.Builds);
    }

    [Fact]
    public async Task CancelledBuildLaunchesNothing()
    {
        using var play = new PlayHarness();
        using var gate = new ManualResetEventSlim(false);
        using var cancel = new CancellationTokenSource();
        play.Builder.Gate = gate;

        var task = play.Service.BuildAndLaunchAsync(new PlayRequest("/p/A.sln", "/p/L.csproj", null), cancel.Token);
        cancel.Cancel();
        play.Pump(() => task.IsCompleted, "the cancelled build");

        Assert.Null(await task);
        Assert.True(play.Service.LastBuild!.Cancelled);
        Assert.Empty(play.Launcher.Games);
    }

    [Fact]
    public void LaunchedGameConnectsAndStreamsLogsAndStatus()
    {
        using var play = new PlayHarness();
        const string Scene = "Content/Scenes/Level.mscene";

        var instance = play.Service.Launch("/fake/MyGame.Desktop", "/fake", Scene, "Game");
        Assert.Equal(PlayInstanceState.Launching, instance.State);
        Assert.Equal(1, instance.Number);
        Assert.Equal(["--editor-port", play.Service.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), "--scene", Scene], instance.Arguments);
        var game = play.Game(instance);
        Assert.Equal(play.Service.Port, game.Port);
        Assert.Equal(Scene, game.Scene);
        Assert.Equal("/fake", game.Request.WorkingDirectory);

        play.Pump(() => instance.State == PlayInstanceState.Running, "the hello");
        Assert.True(instance.HasConnected);
        Assert.NotNull(instance.GameId);
        Assert.NotNull(instance.ConnectTime);
        Assert.True(instance.ConnectTime >= TimeSpan.Zero);

        play.Pump(() => play.Logs.Count >= 3 && instance.Frame > 0 && instance.CurrentScene == Scene, "logs and status");
        Assert.Equal(["Game log 1", "Game log 2", "Game log 3"], play.LogMessages(instance).Take(3));
        Assert.Equal([PlayService.GameCategory, "Audio", PlayService.GameCategory], play.Logs.Take(3).Select(l => l.Entry.Category));
        Assert.Equal(60f, instance.FramesPerSecond);
        Assert.Contains(play.Output, o => o.Instance == instance && o.Line.Contains("starting", StringComparison.Ordinal));

        var frame = instance.Frame;
        play.Pump(() => instance.Frame > frame, "frames to advance");
        Assert.True(instance.IsAlive);
    }

    [Fact]
    public void PauseResumeAndReloadReachTheGame()
    {
        using var play = new PlayHarness();
        var instance = play.LaunchRunning(scene: "Content/Scenes/A.mscene");
        var game = play.Game(instance);

        play.Service.Pause(instance);
        Assert.True(play.Service.IsPaused(instance)); // pending until the status confirms
        play.Pump(() => instance.State == PlayInstanceState.Paused, "pause");
        Assert.True(game.Paused);
        Assert.True(play.Service.IsPaused(instance));

        play.Service.Resume(instance);
        Assert.False(play.Service.IsPaused(instance));
        play.Pump(() => instance.State == PlayInstanceState.Running, "resume");
        Assert.False(game.Paused);

        play.Service.ReloadScene(instance, "Content/Scenes/B.mscene");
        play.Pump(() => play.LogMessages(instance).Contains("reloaded Content/Scenes/B.mscene"), "reload B");
        play.Pump(() => instance.CurrentScene == "Content/Scenes/B.mscene", "scene B in status");
        play.Service.ReloadScene(instance);
        play.Pump(() => play.LogMessages(instance).Count(m => m == "reloaded Content/Scenes/B.mscene") == 2, "reload current");

        Assert.Equal([EditorCommandKind.Pause, EditorCommandKind.Resume, EditorCommandKind.ReloadScene, EditorCommandKind.ReloadScene],
            game.Commands.Select(c => c.Kind));
        Assert.Equal("", game.Commands.Last().Argument);
    }

    [Fact]
    public void StopSaysGoodbyeAndExitsWithZero()
    {
        using var play = new PlayHarness();
        var instance = play.LaunchRunning();

        play.Service.Stop(instance);
        Assert.Equal(PlayInstanceState.Stopping, instance.State);
        play.Pump(() => instance.State == PlayInstanceState.Exited, "exit");

        Assert.Equal(0, instance.ExitCode);
        Assert.False(instance.IsAlive);
        Assert.False(play.Game(instance).WasKilled);
        Assert.Contains(play.Output, o => o.Line.Contains("exiting", StringComparison.Ordinal));
        play.Pump(() => play.LogMessages(instance).Contains("Stop requested by the editor."), "the last log");
        Assert.Equal([instance], play.Service.Instances);

        var changed = play.ChangedCount;
        play.Service.ClearExited();
        Assert.Empty(play.Service.Instances);
        play.Service.Update();
        Assert.Equal(changed + 1, play.ChangedCount);
    }

    [Fact]
    public void TwoInstancesStreamAndStopIndependently()
    {
        using var play = new PlayHarness();
        var server = play.LaunchRunning("Server", extra: ["--server"]);
        var client = play.LaunchRunning("Client", extra: ["--client"]);
        Assert.Equal((1, 2), (server.Number, client.Number));
        Assert.NotEqual(server.GameId, client.GameId);
        Assert.Equal("--server", play.Game(server).Request.Arguments[0]);
        Assert.Equal("Client", play.Game(client).Request.Label);

        play.Pump(() => play.LogMessages(server).Count() >= 3 && play.LogMessages(client).Count() >= 3, "both logs");
        Assert.All(play.LogMessages(server), m => Assert.StartsWith("Server", m, StringComparison.Ordinal));
        Assert.All(play.LogMessages(client), m => Assert.StartsWith("Client", m, StringComparison.Ordinal));

        play.Service.Stop(server);
        play.Pump(() => server.State == PlayInstanceState.Exited, "server exit");
        Assert.Equal(PlayInstanceState.Running, client.State);
        var frame = client.Frame;
        play.Pump(() => client.Frame > frame, "client still running");

        play.Service.Pause(); // all connected: only the client
        play.Pump(() => client.State == PlayInstanceState.Paused, "client paused");
        Assert.DoesNotContain(play.Game(server).Commands, c => c.Kind == EditorCommandKind.Pause);

        play.Service.StopAll();
        play.Pump(() => client.State == PlayInstanceState.Exited, "client exit");
        Assert.Equal(0, client.ExitCode);
    }

    [Fact]
    public void CrashIsReportedWithStderr()
    {
        using var play = new PlayHarness();
        var instance = play.LaunchRunning();

        play.Game(instance).TriggerCrash();
        play.Pump(() => instance.State == PlayInstanceState.Crashed, "crash");

        Assert.Equal(FakeGame.CrashExitCode, instance.ExitCode);
        Assert.Contains(play.Output, o => o.Instance == instance && o.Line.Contains("boom", StringComparison.Ordinal));
        Assert.False(instance.IsAlive);
    }

    [Fact]
    public void CrashBeforeConnectingStillShowsOutput()
    {
        using var play = new PlayHarness();
        play.Launcher.ModeFor = _ => FakeGameMode.NeverConnects;
        var instance = play.Service.Launch("/fake/MyGame.Desktop", "/fake", null, "Game");

        play.Game(instance).TriggerCrash();
        play.Pump(() => !instance.IsAlive, "crash");

        Assert.Equal(PlayInstanceState.Crashed, instance.State);
        Assert.False(instance.HasConnected);
        Assert.Equal(["[stdout] Game starting", "[stderr] Unhandled exception. System.InvalidOperationException: boom"],
            play.Output.Where(o => o.Instance == instance).Select(o => o.Line));
    }

    [Fact]
    public void GameThatNeverConnectsIsKilledByStop()
    {
        using var play = new PlayHarness();
        play.Launcher.ModeFor = _ => FakeGameMode.NeverConnects;
        var instance = play.Service.Launch("/fake/MyGame.Desktop", "/fake", null, "Game");
        for (var i = 0; i < 10; i++)
            play.Service.Update();
        Assert.Equal(PlayInstanceState.Launching, instance.State);

        play.Service.Stop();
        play.Pump(() => !instance.IsAlive, "kill");

        Assert.True(play.Game(instance).WasKilled);
        Assert.Equal(PlayInstanceState.Exited, instance.State); // the editor stopped it: not a crash
        Assert.Equal(FakeGame.KilledExitCode, instance.ExitCode);
    }

    [Fact]
    public void GameIgnoringStopIsKilledAfterTheTimeout()
    {
        var now = new DateTime(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
        using var play = new PlayHarness(() => now);
        play.Launcher.ModeFor = _ => FakeGameMode.IgnoresStop;
        var instance = play.LaunchRunning();
        var game = play.Game(instance);

        play.Service.Stop(instance);
        play.Pump(() => game.Commands.Any(c => c.Kind == EditorCommandKind.Stop), "the stop command");
        now += PlayService.StopTimeout - TimeSpan.FromMilliseconds(1);
        play.Service.Update();
        Assert.False(game.WasKilled);
        Assert.Equal(PlayInstanceState.Stopping, instance.State);

        now += TimeSpan.FromMilliseconds(1);
        play.Pump(() => !instance.IsAlive, "the kill");
        Assert.True(game.WasKilled);
        Assert.Equal(PlayInstanceState.Exited, instance.State);
    }

    [Fact]
    public void DisposeStopsOrKillsEverything()
    {
        var play = new PlayHarness();
        var running = play.LaunchRunning();
        play.Launcher.ModeFor = _ => FakeGameMode.NeverConnects;
        var stuck = play.Service.Launch("/fake/MyGame.Desktop", "/fake", null, "Stuck");
        var games = play.Launcher.Games;

        play.Service.Dispose();

        Assert.All(games, g => Assert.True(g.HasExited));
        Assert.False(play.Game(running).WasKilled); // it got Stop and quit
        Assert.True(play.Game(stuck).WasKilled);
        play.Service.Update(); // no-op once disposed
        play.Dispose();
    }

    [Fact]
    public async Task BuildAndLaunchRunsTheDesktopOutputFromItsFolder()
    {
        using var project = new DesktopProject();
        using var play = new PlayHarness();

        var task = play.Service.BuildAndLaunchAsync(new PlayRequest("/p/MyGame.sln", project.ProjectFile, "Content/Scenes/Main.mscene", "Game", ["--hidden"]), TestContext.Current.CancellationToken);
        play.Pump(() => task.IsCompleted, "build and launch");

        var instance = (await task)!;
        Assert.NotNull(instance);
        var game = play.Game(instance);
        Assert.Equal(project.AppHost, game.Request.ProgramPath);
        Assert.Equal(Path.GetDirectoryName(project.AppHost), game.Request.WorkingDirectory);
        Assert.Equal(["--hidden", "--editor-port", play.Service.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), "--scene", "Content/Scenes/Main.mscene"],
            game.Request.Arguments);
        play.Pump(() => instance.State == PlayInstanceState.Running, "connect");
        Assert.True(play.Service.LastBuild!.Succeeded);
    }

    [Fact]
    public async Task SkipBuildWithoutOutputReturnsNull()
    {
        using var play = new PlayHarness();
        var task = play.Service.BuildAndLaunchAsync(new PlayRequest("/p/MyGame.sln", "/no/such/Game.Desktop.csproj", null, SkipBuild: true), TestContext.Current.CancellationToken);

        Assert.True(task.IsCompleted);
        Assert.Null(await task);
        Assert.Equal(0, play.Builder.Calls);
        Assert.Empty(play.Launcher.Games);
    }

    [Fact]
    public void ProgramThatCannotStartIsACrashWithTheReason()
    {
        using var service = new PlayService(new FakeGameBuilder(), new ProcessGameLauncher(null));
        var output = new List<string>();
        service.GameOutput += (_, line) => output.Add(line);
        var missing = Path.Combine(Path.GetTempPath(), "mf-no-such-game-" + Guid.NewGuid().ToString("N"));

        var instance = service.Launch(missing, Path.GetTempPath(), null, "Game");
        service.Update();

        Assert.Equal(PlayInstanceState.Crashed, instance.State);
        Assert.Null(instance.ProcessId);
        Assert.Contains(output, l => l.Contains(missing, StringComparison.Ordinal));
    }

    [Fact]
    public void RealProcessOutputAndExitCodeAreReported()
    {
        var launcher = new ProcessGameLauncher(null);
        var request = OperatingSystem.IsWindows()
            ? new GameLaunchRequest("cmd.exe", ["/c", "echo out-line & echo err-line 1>&2 & exit 4"], Path.GetTempPath(), "Shell")
            : new GameLaunchRequest("/bin/sh", ["-c", "echo out-line; echo err-line >&2; exit 4"], Path.GetTempPath(), "Shell");
        using var process = launcher.Launch(request);
        Assert.True(process.Id > 0);

        var lines = new List<string>();
        var stopwatch = Stopwatch.StartNew();
        while (!process.HasExited)
        {
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(20), "the shell did not exit");
            Thread.Sleep(5);
        }

        while (process.TryReadOutput(out var line))
            lines.Add(line.Trim());
        Assert.Equal(4, process.ExitCode);
        Assert.Contains("out-line", lines);
        Assert.Contains("err-line", lines);
    }

    [Fact]
    public void FindLauncherProgramPrefersTheAppHost()
    {
        using var project = new DesktopProject();
        Assert.Equal(project.AppHost, ProcessGameLauncher.FindLauncherProgram(project.ProjectFile));
        Assert.Equal(project.AppHost, ProcessGameLauncher.FindLauncherProgram(Path.GetDirectoryName(project.ProjectFile)!));

        File.Delete(project.AppHost);
        Assert.Equal(project.Dll, ProcessGameLauncher.FindLauncherProgram(project.ProjectFile));
        Assert.Null(ProcessGameLauncher.FindLauncherProgram(project.ProjectFile, "Release"));
        Assert.Null(ProcessGameLauncher.FindLauncherProgram("/no/such/Launcher.csproj"));
    }

    [Fact]
    public void IdleUpdateDoesNotAllocate()
    {
        using var play = new PlayHarness();
        play.Launcher.ModeFor = _ => FakeGameMode.NeverConnects;
        var crashed = play.Service.Launch("/fake/MyGame.Desktop", "/fake", null, "Crashed");
        play.Game(crashed).TriggerCrash();
        play.Pump(() => !crashed.IsAlive, "crash");
        var waiting = play.Service.Launch("/fake/MyGame.Desktop", "/fake", null, "Waiting");
        play.Pump(() => play.Output.Any(o => o.Instance == waiting), "the waiting game's output");
        for (var i = 0; i < 200; i++)
            play.Service.Update();

        var smallest = long.MaxValue;
        for (var window = 0; window < 3 && smallest != 0; window++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 1000; i++)
                play.Service.Update();
            smallest = Math.Min(smallest, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        Assert.Equal(0, smallest);
        Assert.Equal(PlayInstanceState.Launching, waiting.State);
    }
}

/// <summary>A temporary <c>MyGame.Desktop.csproj</c> with a fake <c>bin/Debug/net10.0</c> build output.</summary>
public sealed class DesktopProject : IDisposable
{
    public DesktopProject()
    {
        Directory = System.IO.Directory.CreateTempSubdirectory("mf-play-desktop").FullName;
        ProjectFile = Path.Combine(Directory, "MyGame.Desktop.csproj");
        File.WriteAllText(ProjectFile, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>");
        var bin = System.IO.Directory.CreateDirectory(Path.Combine(Directory, "bin", "Debug", "net10.0")).FullName;
        Dll = Path.Combine(bin, "MyGame.Desktop.dll");
        AppHost = Path.Combine(bin, OperatingSystem.IsWindows() ? "MyGame.Desktop.exe" : "MyGame.Desktop");
        File.WriteAllText(Dll, "");
        File.WriteAllText(AppHost, "");
    }

    public string Directory { get; }
    public string ProjectFile { get; }
    public string Dll { get; }
    public string AppHost { get; }

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

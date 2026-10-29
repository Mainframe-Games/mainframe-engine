using MainframeEngine.EditorLink;

namespace MainframeEngine.Editor.Tests;

/// <summary>G15: game crashes in the editor — the link's report, the decoded exit, the dump, the Output crash row.</summary>
public sealed class GameCrashTests : IDisposable
{
    private readonly string _crashes = Directory.CreateTempSubdirectory("mf-play-crash").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_crashes, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static CrashReport Report()
    {
        var report = new CrashReport
        {
            Id = "0123456789abcdef0123456789abcdef",
            Kind = CrashKind.ManagedException,
            Timestamp = DateTime.UtcNow,
        };
        report.Session.Node = "/root/Forest/Player";
        report.Session.NodeType = "Game.PlayerController";
        report.Session.Callback = "OnPhysicsProcess";
        report.Session.Scene = "Assets/Scenes/Forest.mscene";
        report.Exception.Add(new CrashException
        {
            Type = "System.NullReferenceException",
            Message = "Object reference not set to an instance of an object.",
            Frames =
            [
                new CrashFrame { Method = "Game.PlayerController.OnPhysicsProcess(Single)", File = typeof(GameCrashTests).Assembly.Location, Line = 42 },
                new CrashFrame { Method = "MainframeEngine.SceneTree.RunPhysicsStepCore(Single)", File = "/engine/SceneTree.cs", Line = 7 },
                new CrashFrame { Method = "Game.Missing.Method()", File = "/nowhere/Missing.cs", Line = 3 },
            ],
        });
        return report;
    }

    [Fact]
    public void LaunchedGamesWriteNativeDumpsIntoTheProjectsCrashFolder()
    {
        using var play = new PlayHarness();
        play.Service.CrashDirectory = () => _crashes;

        var instance = play.LaunchRunning();

        var environment = play.Game(instance).Request.Environment!;
        Assert.Equal("1", environment["DOTNET_DbgEnableMiniDump"]);
        Assert.Equal(Path.Combine(_crashes, "dump-%p.dmp"), environment["DOTNET_DbgMiniDumpName"]);
    }

    [Fact]
    public void ACrashWithALinkReportIsReportedOnceWithIt()
    {
        using var play = new PlayHarness();
        var crashed = new List<PlayInstance>();
        play.Service.Crashed += crashed.Add;
        var instance = play.LaunchRunning();
        var folder = Path.Combine(_crashes, "bundle");
        play.Game(instance).CrashReport = new EditorLinkCrashReport(System.Text.Encoding.UTF8.GetString(Report().ToJson()), folder);

        play.Game(instance).TriggerCrash();
        play.Pump(() => crashed.Count > 0, "the crash");
        play.Pump(() => instance.GameId is null, "the link to close");
        for (var i = 0; i < 20; i++)
            play.Service.Update();

        Assert.Single(crashed);
        Assert.Equal(PlayInstanceState.Crashed, instance.State);
        Assert.Equal("NullReferenceException in PlayerController.OnPhysicsProcess", instance.Crash!.Summary);
        Assert.Equal(folder, instance.CrashFolder);
        Assert.Equal("exit code 3", ExitCodes.Describe(FakeGame.CrashExitCode, windows: false));
        Assert.Contains("crashed (", ToolbarPanel.InstanceTooltip(instance), StringComparison.Ordinal);
        Assert.Contains("NullReferenceException in PlayerController.OnPhysicsProcess", ToolbarPanel.InstanceTooltip(instance), StringComparison.Ordinal);
    }

    [Fact]
    public void ACrashWithoutAReportNamesTheExitAndTheDump()
    {
        using var play = new PlayHarness();
        play.Service.CrashDirectory = () => _crashes;
        var crashed = new List<PlayInstance>();
        play.Service.Crashed += crashed.Add;
        var instance = play.LaunchRunning();
        File.WriteAllBytes(NativeDumps.PathFor(_crashes, instance.ProcessId!.Value), [1]);

        play.Game(instance).TriggerCrash();
        play.Pump(() => crashed.Count > 0, "the crash (after the report grace)");

        Assert.Null(instance.Crash);
        Assert.Equal(NativeDumps.PathFor(_crashes, instance.ProcessId!.Value), instance.DumpPath);
        var row = OutputCrash.From(instance);
        Assert.Equal($"Game #{instance.Number} crashed: {instance.ExitDescription}", row.Title);
        Assert.Contains("native dump: ", row.Detail, StringComparison.Ordinal);
        Assert.Contains("No crash report arrived", row.Detail, StringComparison.Ordinal);
        Assert.Empty(row.Frames);
    }

    [Fact]
    public void TheCrashRowHasTheNodeSceneAndFramesWithGameFramesClickable()
    {
        var instance = new PlayInstance(2, "Game", null, DateTime.UtcNow, []) { Crash = Report(), CrashFolder = "/tmp/none", ProcessId = 7 };

        var row = OutputCrash.From(instance);

        Assert.Equal("Game #2 crashed: NullReferenceException in PlayerController.OnPhysicsProcess", row.Title);
        Assert.StartsWith("/root/Forest/Player · Assets/Scenes/Forest.mscene", row.Detail, StringComparison.Ordinal);
        Assert.Equal(3, row.Frames.Count);
        Assert.True(row.Frames[0].IsGame);
        Assert.True(row.Frames[0].HasSource);   // a game frame whose file exists here
        Assert.False(row.Frames[1].IsGame);     // engine frames are muted
        Assert.False(row.Frames[1].HasSource);
        Assert.False(row.Frames[2].HasSource);  // no such file on this machine
        Assert.Contains("PlayerController.OnPhysicsProcess", row.CopyText, StringComparison.Ordinal);
    }
}

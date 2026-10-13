using MainframeEngine.Editor.Tests.Projects;
using MainframeEngine.EditorLink;
using Silk.NET.Input;

namespace MainframeEngine.Editor.Tests.Play;

public sealed class PlayRecordingStatusTests
{
    [Theory]
    [InlineData(PlayRecordingState.None, 0, "")]
    [InlineData(PlayRecordingState.Recording, 83, "● REC 1:23")]
    [InlineData(PlayRecordingState.Recording, 3725, "● REC 62:05")]
    [InlineData(PlayRecordingState.Saving, 10, "Saving recording…")]
    public void RecordStatusFormatsMinutesAndSeconds(PlayRecordingState state, int seconds, string expected)
    {
        var start = new DateTime(2026, 10, 13, 10, 0, 0, DateTimeKind.Utc);
        Assert.Equal(expected, PlayController.RecordStatus(state, start, start.AddSeconds(seconds)));
    }

    [Fact]
    public void RecordStatusWithoutAStartTimeShowsZero()
    {
        var now = new DateTime(2026, 10, 13, 10, 0, 0, DateTimeKind.Utc);
        Assert.Equal("● REC 0:00", PlayController.RecordStatus(PlayRecordingState.Recording, null, now));
    }
}

/// <summary>Play and Record / Start / Stop Recording through the real controller, with fake games on the real editor link.</summary>
[Collection(nameof(SerialEditor))]
public sealed class PlayRecordingControllerTests : IDisposable
{
    private readonly string? _previousUserData = Environment.GetEnvironmentVariable(UserDataPaths.OverrideVariable);
    private readonly string _userData = Directory.CreateTempSubdirectory("mf-user-data").FullName;
    private readonly TestProject _project = new();
    private readonly FakeGameBuilder _builder = new();
    private readonly FakeGameLauncher _launcher = new();
    private readonly HeadlessEditor _editor;

    public PlayRecordingControllerTests()
    {
        Environment.SetEnvironmentVariable(UserDataPaths.OverrideVariable, _userData);
        _editor = new HeadlessEditor(configure: o => o with { InitialProject = _project.Root, GameBuilder = _builder, GameLauncher = _launcher });
        _editor.Tick(3);
    }

    public void Dispose()
    {
        _editor.Dispose();
        Environment.SetEnvironmentVariable(UserDataPaths.OverrideVariable, _previousUserData);
        _project.Dispose();
        try
        {
            Directory.Delete(_userData, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private PlayController Play => _editor.Workspace.Play;

    private void TickUntil(Func<bool> condition, string what, int seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            _editor.Tick();
            Thread.Sleep(5);
        }

        Assert.True(condition(), $"Timed out waiting for {what}.");
    }

    private PlayInstance[] StartTwoGames()
    {
        Play.PlayMain();
        TickUntil(() => Play.Service.Instances.Any(i => i.State == PlayInstanceState.Running), "the first game");
        Play.PlayAnotherInstance();
        TickUntil(() => Play.Service.Instances.Count(i => i.State == PlayInstanceState.Running) == 2, "the second game");
        return [.. Play.Service.Instances];
    }

    private static int Count(FakeGame game, EditorCommandKind kind) => game.Commands.Count(c => c.Kind == kind);

    [Fact]
    public void PlayAndRecordLaunchesWithRecordArgs()
    {
        Assert.Equal("Play and Record", Play.RecordingLabel);
        Play.PlayAndRecord();
        TickUntil(() => _launcher.Games.Count == 1, "the launch");

        var arguments = _launcher.Games[0].Request.Arguments;
        var record = arguments.ToList().IndexOf("--record");
        Assert.True(record >= 0, "no --record argument");
        var path = arguments[record + 1];
        Assert.StartsWith(Path.Combine(_userData, ""), path, StringComparison.Ordinal);
        Assert.Equal("recordings", Path.GetFileName(Path.GetDirectoryName(path)));
        Assert.EndsWith(".mp4", path, StringComparison.Ordinal);
        Assert.True(Directory.Exists(Path.GetDirectoryName(path)), "the recordings folder is created");
        Assert.True(record + 1 < arguments.ToList().IndexOf("--editor-port"), "--record belongs to the host flags, before --editor-port");
    }

    [Fact]
    public void ToggleRecordingWithNothingRunningPlaysAndRecords()
    {
        Play.ToggleRecording();
        TickUntil(() => _launcher.Games.Count == 1, "the launch");
        Assert.Contains("--record", _launcher.Games[0].Request.Arguments);
    }

    [Fact]
    public void ToggleRecordingTargetsTheFirstRunningInstance()
    {
        var instances = StartTwoGames();
        var first = _launcher.Games[0];
        var second = _launcher.Games[1];
        Assert.Equal("Start Recording", Play.RecordingLabel);

        Play.ToggleRecording();
        TickUntil(() => instances[0].Recording == PlayRecordingState.Recording, "the first game to start recording");
        Assert.Equal(1, Count(first, EditorCommandKind.StartRecording));
        Assert.Equal(0, Count(second, EditorCommandKind.StartRecording));
        var command = first.Commands.Single(c => c.Kind == EditorCommandKind.StartRecording);
        Assert.StartsWith(Path.Combine(_userData, ""), command.Argument, StringComparison.Ordinal);
        Assert.Equal("Stop Recording", Play.RecordingLabel);

        Play.ToggleRecording();
        TickUntil(() => Count(first, EditorCommandKind.StopRecording) == 1, "the stop command");
        Assert.Equal(0, Count(second, EditorCommandKind.StopRecording));
        Assert.Equal(0, Count(second, EditorCommandKind.StartRecording));
    }

    [Fact]
    public void ToggleRecordingDoesNotSendASecondStartBeforeTheFirstIsConfirmed()
    {
        var instances = StartTwoGames();
        Play.ToggleRecording();
        Play.ToggleRecording(); // double F10 before the game answered Started
        TickUntil(() => instances[0].Recording == PlayRecordingState.Recording, "recording to start");
        _editor.Tick(5);

        Assert.Equal(1, _launcher.Games.Sum(g => Count(g, EditorCommandKind.StartRecording)));
        Assert.Equal(0, _launcher.Games.Sum(g => Count(g, EditorCommandKind.StopRecording)));
    }

    [Fact]
    public void ToggleRecordingStartsAgainAfterAFailedStart()
    {
        var instances = StartTwoGames();
        _launcher.Games[0].FailRecording = true;
        Play.ToggleRecording();
        TickUntil(() => Count(_launcher.Games[0], EditorCommandKind.StartRecording) == 1, "the first start");
        _editor.Tick(10); // the Failed reply arrives and clears the pending start
        Assert.Equal(PlayRecordingState.None, instances[0].Recording);

        _launcher.Games[0].FailRecording = false;
        Play.ToggleRecording();
        TickUntil(() => instances[0].Recording == PlayRecordingState.Recording, "a second start to succeed");
        Assert.Equal(2, Count(_launcher.Games[0], EditorCommandKind.StartRecording));
    }

    [Fact]
    public void TheToolbarShowsTheRecordButtonStateAndTheRecStatus()
    {
        var toolbar = _editor.Workspace.Toolbar.Document;
        var instances = StartTwoGames();
        Assert.False(toolbar.GetElementById("record").IsClassSet("disabled"));
        Assert.False(toolbar.GetElementById("record").IsClassSet("recording"));

        Play.ToggleRecording();
        TickUntil(() => instances[0].Recording == PlayRecordingState.Recording, "recording to start");
        _editor.Tick(2);
        Assert.True(toolbar.GetElementById("record").IsClassSet("recording"));
        Assert.Contains("● REC 0:0", toolbar.GetElementById("play-status").InnerRml, StringComparison.Ordinal);
        Assert.Contains("Stop Recording", toolbar.GetElementById("record").GetAttribute("data-tooltip"), StringComparison.Ordinal);

        Play.ToggleRecording();
        TickUntil(() => instances[0].Recording == PlayRecordingState.None, "the recording to finish");
        _editor.Tick(2);
        Assert.False(toolbar.GetElementById("record").IsClassSet("recording"));
        Assert.Equal("", toolbar.GetElementById("play-status").InnerRml);
        Assert.Empty(_editor.RmlMessages);
    }

    [Fact]
    public void F10TogglesRecording()
    {
        _editor.Key(Key.F10);
        TickUntil(() => _launcher.Games.Count == 1, "the launch");
        Assert.Contains("--record", _launcher.Games[0].Request.Arguments);
    }
}

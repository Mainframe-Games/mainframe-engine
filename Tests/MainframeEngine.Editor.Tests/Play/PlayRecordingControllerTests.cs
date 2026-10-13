using MainframeEngine.Editor.Tests.Projects;
using MainframeEngine.EditorLink;
using Silk.NET.Input;

namespace MainframeEngine.Editor.Tests.Play;

public sealed class PlayRecordingStatusTests
{
    [Theory]
    [InlineData(PlayRecordingState.None, 0, "")]
    [InlineData(PlayRecordingState.Recording, 83, "REC 1:23")]
    [InlineData(PlayRecordingState.Recording, 3725, "REC 62:05")]
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
        Assert.Equal("REC 0:00", PlayController.RecordStatus(PlayRecordingState.Recording, null, now));
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
    private readonly List<(string App, string File)> _launched = [];
    private readonly List<string> _revealed = [];
    private readonly HeadlessEditor _editor;

    public PlayRecordingControllerTests()
    {
        Environment.SetEnvironmentVariable(UserDataPaths.OverrideVariable, _userData);
        try
        {
            _editor = new HeadlessEditor(configure: o => o with
            {
                InitialProject = _project.Root,
                GameBuilder = _builder,
                GameLauncher = _launcher,
                LaunchCutback = (app, file) =>
                {
                    _launched.Add((app, file));
                    return true;
                },
                Reveal = _revealed.Add,
            });
            _editor.Tick(3);
        }
        catch
        {
            Dispose(); // xunit does not dispose a test class whose constructor threw
            throw;
        }
    }

    public void Dispose()
    {
        _editor?.Dispose();
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
        Assert.StartsWith("Main-", Path.GetFileName(path), StringComparison.Ordinal); // named after the main scene
        Assert.True(record + 1 < arguments.ToList().IndexOf("--editor-port"), "--record belongs to the host flags, before --editor-port");
    }

    [Fact]
    public void PlayAndRecordPassesTheFfmpegSetting()
    {
        // Without the setting the game finds ffmpeg itself.
        Play.PlayAndRecord();
        TickUntil(() => _launcher.Games.Count == 1, "the first launch");
        Assert.DoesNotContain("--ffmpeg", _launcher.Games[0].Request.Arguments);

        // The setting names a folder holding ffmpeg and ffprobe: the game is handed the encoder inside it.
        var folder = Directory.CreateTempSubdirectory("mf-ffmpeg-setting").FullName;
        try
        {
            var exe = Path.Combine(folder, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
            File.WriteAllBytes(exe, []);
            File.WriteAllBytes(Path.Combine(folder, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe"), []);
            var settings = _editor.Workspace.Settings.Clone();
            settings.VideoFfmpegPath = folder;
            _editor.Workspace.ApplySettings(settings);

            Play.PlayAnotherInstance();
            TickUntil(() => _launcher.Games.Count == 2, "the second launch");
            var arguments = _launcher.Games[1].Request.Arguments.ToList();
            var flag = arguments.IndexOf("--ffmpeg");
            Assert.True(flag >= 0, "no --ffmpeg argument");
            Assert.Equal(exe, arguments[flag + 1]);
            Assert.True(flag + 1 < arguments.IndexOf("--editor-port"), "--ffmpeg belongs to the host flags, before --editor-port");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
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
        var failures = 0;
        Play.Service.RecordingFinished += (_, recording) =>
        {
            if (recording.State == EditorLinkRecordingState.Failed)
                failures++;
        };
        _launcher.Games[0].FailRecording = true;
        Play.ToggleRecording();
        TickUntil(() => failures == 1, "the Failed reply to be processed"); // it also clears the pending start
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
        Assert.False(toolbar.GetElementById("rec-dot").IsClassSet("on"));
        Assert.StartsWith("Start Recording (F10)", toolbar.GetElementById("record").GetAttribute("data-tooltip"), StringComparison.Ordinal);

        Play.ToggleRecording();
        TickUntil(() => instances[0].Recording == PlayRecordingState.Recording, "recording to start");
        _editor.Tick(2);
        Assert.True(toolbar.GetElementById("record").IsClassSet("recording"));
        Assert.True(toolbar.GetElementById("rec-dot").IsClassSet("on"));
        Assert.Contains("REC 0:0", toolbar.GetElementById("play-status").InnerRml, StringComparison.Ordinal);
        Assert.DoesNotContain("●", toolbar.GetElementById("play-status").InnerRml, StringComparison.Ordinal); // not in the UI font
        Assert.Contains("Stop Recording", toolbar.GetElementById("record").GetAttribute("data-tooltip"), StringComparison.Ordinal);

        _launcher.Games[0].SwallowStopRecording = true; // stays "saving"
        Play.ToggleRecording();
        TickUntil(() => instances[0].Recording == PlayRecordingState.Saving, "the recording to be saving");
        _editor.Tick(2);
        Assert.False(toolbar.GetElementById("record").IsClassSet("recording")); // red only while actually recording
        Assert.False(toolbar.GetElementById("rec-dot").IsClassSet("on"));
        Assert.Equal("Saving recording…", toolbar.GetElementById("play-status").InnerRml);
        Assert.Equal("Saving recording…", toolbar.GetElementById("record").GetAttribute("data-tooltip"));

        Play.Stop();
        TickUntil(() => !Play.IsPlaying, "the games to stop");
        _editor.Tick(2);
        Assert.False(toolbar.GetElementById("record").IsClassSet("recording"));
        Assert.False(toolbar.GetElementById("rec-dot").IsClassSet("on"));
        Assert.Equal("", toolbar.GetElementById("play-status").InnerRml);
        Assert.StartsWith("Play and Record (F10)", toolbar.GetElementById("record").GetAttribute("data-tooltip"), StringComparison.Ordinal);
        Assert.Empty(_editor.RmlMessages);
    }

    private MenuItem RecordMenuItem() => _editor.Workspace.Commands.MenuItems("run").Single(i => i.Command == "play.record");

    [Fact]
    public void TheRunMenuItemFollowsTheRecordingState()
    {
        var item = RecordMenuItem();
        Assert.Equal(("Play and Record", true), (item.Label, item.Enabled));

        var instances = StartTwoGames();
        item = RecordMenuItem();
        Assert.Equal(("Start Recording", true), (item.Label, item.Enabled));

        Play.ToggleRecording();
        TickUntil(() => instances[0].Recording == PlayRecordingState.Recording, "recording to start");
        item = RecordMenuItem();
        Assert.Equal(("Stop Recording", true), (item.Label, item.Enabled));

        _launcher.Games[0].SwallowStopRecording = true; // stays "saving"
        Play.ToggleRecording();
        TickUntil(() => instances[0].Recording == PlayRecordingState.Saving, "the recording to be saving");
        item = RecordMenuItem();
        Assert.Equal(("Saving recording…", false), (item.Label, item.Enabled));
        _editor.Tick(2);
        Assert.True(_editor.Workspace.Toolbar.Document.GetElementById("record").IsClassSet("disabled"));
        Assert.Equal(1, _launcher.Games.Sum(g => Count(g, EditorCommandKind.StartRecording)));
        Play.ToggleRecording(); // does nothing while saving
        Assert.Equal(1, _launcher.Games.Sum(g => Count(g, EditorCommandKind.StartRecording)));
    }

    [Fact]
    public void RecordingStaysAvailableWhileAGameRunsAndAnotherBuildIsRunning()
    {
        StartTwoGames();
        _builder.Gate = new ManualResetEventSlim(false);
        try
        {
            Play.PlayMain(); // a fresh Play builds while the two games keep running
            Assert.True(Play.IsBuilding);
            Assert.True(Play.IsPlaying);
            var item = RecordMenuItem();
            Assert.Equal(("Start Recording", true), (item.Label, item.Enabled));
        }
        finally
        {
            _builder.Gate.Set();
        }
    }

    [Fact]
    public void RefusedPlayAndRecordCreatesNoRecordingsFolder()
    {
        _builder.Gate = new ManualResetEventSlim(false);
        try
        {
            Play.PlayMain(); // a build is now running
            Assert.True(Play.IsBuilding);
            Play.PlayAndRecord();
            Assert.False(Directory.Exists(Path.Combine(_userData, "recordings")) ||
                         Directory.EnumerateDirectories(_userData, "recordings", SearchOption.AllDirectories).Any());
        }
        finally
        {
            _builder.Gate.Set();
        }
    }

    [Fact]
    public void F10TogglesRecording()
    {
        _editor.Key(Key.F10);
        TickUntil(() => _launcher.Games.Count == 1, "the launch");
        Assert.Contains("--record", _launcher.Games[0].Request.Arguments);
    }

    [Fact]
    public void AStoppedRecordingShowsTheSavedDialogAndOpensItInCutback()
    {
        var cutback = Path.Combine(_userData, "cutback-fake");
        File.WriteAllText(cutback, "");
        _editor.Workspace.Settings.CutbackPath = cutback;
        var instances = StartTwoGames();
        Play.ToggleRecording();
        TickUntil(() => instances[0].Recording == PlayRecordingState.Recording, "recording to start");

        Play.ToggleRecording();
        TickUntil(() => _editor.Workspace.Message.Current is not null, "the Recording saved dialog");

        var dialog = _editor.Workspace.Message.Current!;
        Assert.Equal("Recording saved", dialog.Title);
        Assert.Equal(["Open in Cutback", "Show in Folder", "Close"], dialog.Buttons);

        _editor.Workspace.Message.Answer(0);
        var launched = Assert.Single(_launched);
        Assert.Equal(cutback, launched.App);
        Assert.StartsWith(Path.Combine(_userData, ""), launched.File, StringComparison.Ordinal);
        Assert.Null(_editor.Workspace.Message.Current);
    }

    [Fact]
    public void AFailedStartShowsTheFailedDialogAndShowInFolderUsesTheSeam()
    {
        var instances = StartTwoGames();
        _launcher.Games[0].FailRecording = true;
        Play.ToggleRecording();
        TickUntil(() => _editor.Workspace.Message.Current is not null, "the Recording failed dialog");

        var dialog = _editor.Workspace.Message.Current!;
        Assert.Equal("Recording failed", dialog.Title);
        Assert.Contains("no ffmpeg", dialog.Message, StringComparison.Ordinal);
        Assert.Equal(PlayRecordingState.None, instances[0].Recording);
        _editor.Workspace.Message.Answer(0); // "Close": the fake's error names no .partial folder
        Assert.Empty(_revealed);
    }

    [Fact]
    public void RunAnotherInstanceAfterPlayAndRecordDoesNotRecord()
    {
        Play.PlayAndRecord();
        TickUntil(() => Play.Service.Instances.Any(i => i.State == PlayInstanceState.Running), "the recorded game");

        Play.PlayAnotherInstance();
        TickUntil(() => _launcher.Games.Count == 2, "the second launch");

        Assert.Contains("--record", _launcher.Games[0].Request.Arguments);
        Assert.DoesNotContain("--record", _launcher.Games[1].Request.Arguments);
    }

    [Fact]
    public void StopWhileRecordingShowsSavingUntilTheGameHasSaved()
    {
        var instances = StartTwoGames();
        Play.ToggleRecording();
        TickUntil(() => instances[0].Recording == PlayRecordingState.Recording, "recording to start");

        Play.Stop();

        Assert.Equal(PlayRecordingState.Saving, instances[0].Recording);
        Assert.True(Play.IsSavingRecording);
        Assert.Equal("Saving recording…", Play.RecordingLabel);
    }

    [Fact]
    public void StartRecordingOnAnOlderGameExplainsWhy()
    {
        _launcher.ProtocolVersion = 3;
        StartTwoGames();

        Play.ToggleRecording();

        var dialog = _editor.Workspace.Message.Current;
        Assert.NotNull(dialog);
        Assert.Contains("older MainframeEngine", dialog.Message, StringComparison.Ordinal);
        _editor.Tick(5);
        Assert.Equal(0, _launcher.Games.Sum(g => Count(g, EditorCommandKind.StartRecording)));
    }

    [Fact]
    public void PlayAndRecordWithAnOlderGameExplainsWhyOnce()
    {
        _launcher.ProtocolVersion = 3;
        Play.PlayAndRecord();
        TickUntil(() => _editor.Workspace.Message.Current is not null, "the explanation");
        var dialog = _editor.Workspace.Message.Current!;
        Assert.Contains("older MainframeEngine", dialog.Message, StringComparison.Ordinal);

        _editor.Workspace.Message.Answer(0);
        for (var i = 0; i < 20; i++)
        {
            _editor.Tick();
            Thread.Sleep(2);
        }

        Assert.Null(_editor.Workspace.Message.Current);
    }
}

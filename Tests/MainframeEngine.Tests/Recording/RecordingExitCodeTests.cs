namespace MainframeEngine.Tests.Recording;

/// <summary>When a failed recording makes a <c>--record</c> run exit with an error.</summary>
public sealed class RecordingExitCodeTests
{
    private static RecordingResult Saved() => new("out.mp4", 60, 0, null);

    private static RecordingResult Failed() => new("out.mp4", 12, 0, "Recording stopped: the encoder exited.");

    [Fact]
    public void AnOfflineRunWhoseRecordingFailedQuitsWithAnError() =>
        Assert.True(GameHost.ShouldQuitOnRecordingError(offline: true, Failed()));

    [Fact]
    public void AnOfflineRunWhoseRecordingSucceededKeepsItsExitCode() =>
        Assert.False(GameHost.ShouldQuitOnRecordingError(offline: true, Saved()));

    [Fact]
    public void ALiveRunKeepsPlayingAfterAFailedRecording() =>
        Assert.False(GameHost.ShouldQuitOnRecordingError(offline: false, Failed()));

    [Fact]
    public void ALiveRunWhoseRecordingSucceededKeepsItsExitCode() =>
        Assert.False(GameHost.ShouldQuitOnRecordingError(offline: false, Saved()));
}

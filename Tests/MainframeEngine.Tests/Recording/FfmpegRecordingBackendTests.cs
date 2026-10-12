namespace MainframeEngine.Tests.Recording;

/// <summary>Lifecycle of the ffmpeg backend that needs no ffmpeg: a missing binary must fail cleanly and leave nothing behind.</summary>
public sealed class FfmpegRecordingBackendTests
{
    private static RecordingFormat FormatAt(string output) => new(output, 64, 64, 30, PixelLayout.Bgra, 48_000, 2, false);

    [Fact]
    public void BeginWithMissingFfmpegThrowsReadablyAndLeavesNothingRunningOrOnDisk()
    {
        var dir = Directory.CreateTempSubdirectory("mfrec");
        try
        {
            var output = Path.Combine(dir.FullName, "clip.mp4");
            using var backend = new FfmpegRecordingBackend(Path.Combine(dir.FullName, "no-such-ffmpeg"));

            var ex = Assert.Throws<FfmpegNotFoundException>(() => backend.Begin(FormatAt(output)));

            Assert.Contains("no-such-ffmpeg", ex.Message);
            Assert.False(Directory.Exists(output + ".partial"));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void DisposeIsSafeBeforeBeginAndWhenCalledTwice()
    {
        var backend = new FfmpegRecordingBackend("ffmpeg-not-started");
        backend.Dispose();
        backend.Dispose();
    }

    [Fact]
    public async Task FinishAndWritesBeforeBeginFailWithAnInvalidOperation()
    {
        using var backend = new FfmpegRecordingBackend("ffmpeg-not-started");

        Assert.Throws<InvalidOperationException>(() => backend.WriteSilence(10));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            backend.FinishAsync(0, null!, null!, CancellationToken.None));
    }

    [Fact]
    public void QueuingAFrameAfterDisposeReportsFailureInsteadOfThrowing()
    {
        var backend = new FfmpegRecordingBackend("ffmpeg-not-started");
        backend.Dispose();

        Assert.False(backend.TryWriteVideo(new byte[16], 1, wait: false));
    }
}

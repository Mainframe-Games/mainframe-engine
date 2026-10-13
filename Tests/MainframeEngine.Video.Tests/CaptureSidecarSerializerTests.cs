using MainframeEngine.Video.Capture;

namespace MainframeEngine.Video.Tests;

public sealed class CaptureSidecarSerializerTests
{
    private static string Fixture() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.mfcap.json"));

    [Fact]
    public void ReadsTheSharedFixture()
    {
        var capture = CaptureSidecarSerializer.Deserialize(Fixture());

        Assert.Equal(CaptureMode.Live, capture.Mode);
        Assert.Equal(60.0, capture.DurationSeconds);
        Assert.Equal(60, capture.FrameRate);
        Assert.Equal(5, capture.Input.Count);
        Assert.Equal([new CaptureSpan(14.5, 19.0)], capture.Loading);
        Assert.Equal([new CaptureSpan(32.0, 35.0, "cutscene")], capture.Keep);
        Assert.Empty(capture.Skip);
        Assert.Equal([new CaptureMarker(26.0, "boss intro"), new CaptureMarker(41.0, "chest opens")], capture.Markers);
    }

    [Fact]
    public void MissingListsReadAsEmpty()
    {
        const string json = """{ "format": "mainframe-capture", "version": 1, "mode": "offline", "durationSeconds": 5 }""";

        var capture = CaptureSidecarSerializer.Deserialize(json);

        Assert.Equal(CaptureMode.Offline, capture.Mode);
        Assert.Empty(capture.Input);
        Assert.Empty(capture.Markers);
    }

    [Fact]
    public void UnknownFieldsAreIgnored()
    {
        const string json = """{ "format": "mainframe-capture", "version": 1, "mode": "live", "durationSeconds": 5, "futureThing": { "a": 1 } }""";

        Assert.Equal(5, CaptureSidecarSerializer.Deserialize(json).DurationSeconds);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{ "format": "something-else", "version": 1, "mode": "live", "durationSeconds": 5 }""")]
    [InlineData("""{ "format": "mainframe-capture", "mode": "live", "durationSeconds": 5 }""")]
    [InlineData("""{ "format": "mainframe-capture", "version": 1, "durationSeconds": 5 }""")]
    [InlineData("""{ "format": "mainframe-capture", "version": 1, "mode": "sideways", "durationSeconds": 5 }""")]
    [InlineData("""{ "format": "mainframe-capture", "version": 1, "mode": "live", "durationSeconds": 0 }""")]
    [InlineData("""{ "format": "mainframe-capture", "version": 1, "mode": "live", "durationSeconds": 5, "input": [ { "start": 3, "end": 2 } ] }""")]
    [InlineData("""{ "format": "mainframe-capture", "version": 1, "mode": "live", "durationSeconds": 5, "markers": [ { "time": -1, "label": "x" } ] }""")]
    [InlineData("""{ "format": "mainframe-capture", "version": 1, "mode": "live", "durationSeconds": 5, "input": [ null ] }""")]
    [InlineData("""{ "format": "mainframe-capture", "version": 1, "mode": "live", "durationSeconds": 5, "markers": [ null ] }""")]
    public void MalformedSidecarsAreRejectedWithAReadableMessage(string json)
    {
        var act = () => CaptureSidecarSerializer.Deserialize(json);

        var e = Assert.Throws<CaptureSidecarFormatException>(act);
        Assert.False(string.IsNullOrWhiteSpace(e.Message));
    }

    [Fact]
    public void ANewerVersionIsRefusedWithAnUpdateHint()
    {
        const string json = """{ "format": "mainframe-capture", "version": 2, "mode": "live", "durationSeconds": 5 }""";

        var act = () => CaptureSidecarSerializer.Deserialize(json);

        var e = Assert.Throws<CaptureSidecarFormatException>(act);
        Assert.Matches("newer.*Update the editor", e.Message);
    }

    [Fact]
    public void PathForAppendsTheSuffixToTheFullVideoName()
    {
        Assert.Equal("/r/forest.mp4.mfcap.json", CaptureSidecar.PathFor("/r/forest.mp4"));
    }

    [Fact]
    public async Task TryLoadReturnsNullWhenThereIsNoSidecar()
    {
        var video = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".mp4");

        var capture = await CaptureSidecarSerializer.TryLoadForVideoAsync(video, TestContext.Current.CancellationToken);

        Assert.Null(capture);
    }

    [Fact]
    public async Task TryLoadReadsTheSidecarNextToTheVideo()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var video = Path.Combine(dir.FullName, "clip.mp4");
            await File.WriteAllTextAsync(CaptureSidecar.PathFor(video), Fixture(), TestContext.Current.CancellationToken);

            var capture = await CaptureSidecarSerializer.TryLoadForVideoAsync(video, TestContext.Current.CancellationToken);

            Assert.Equal(2, capture!.Markers.Count);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}

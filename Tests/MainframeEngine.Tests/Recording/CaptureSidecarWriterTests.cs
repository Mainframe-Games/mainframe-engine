using System.Text.Json.Nodes;

namespace MainframeEngine.Tests.Recording;

/// <summary>The sidecar contract (spec §1.7): writing the fixture's data reproduces the fixture Cutback reads.</summary>
public sealed class CaptureSidecarWriterTests
{
    private static readonly CaptureInfo Info = new("forest-20261012-143005.mp4", "0.42.0", "Forest", "res://Scenes/Forest.scn",
        Offline: false, FrameRate: 60, Width: 1920, Height: 1080, DurationSeconds: 60.0, DroppedFrames: 0);

    private static readonly CaptureSpans Spans = new(
        Input: [new(0.0, 8.0), new(8.5, 14.0), new(22.0, 31.0), new(44.0, 52.0), new(53.0, 56.0)],
        Loading: [new(14.5, 19.0)],
        Paused: [new(56.0, 60.0)],
        Keep: [new(32.0, 35.0, "cutscene")],
        Skip: [],
        Markers: [new(26.0, "boss intro"), new(41.0, "chest opens")]);

    [Fact]
    public void WritingTheFixtureDataReproducesTheSharedFixture()
    {
        var expected = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Recording", "Fixtures", "sample.mfcap.json")));
        var actual = JsonNode.Parse(CaptureSidecarWriter.ToJson(Info, Spans));
        Assert.True(JsonNode.DeepEquals(expected, actual), CaptureSidecarWriter.ToJson(Info, Spans));
    }

    [Fact]
    public void InputAndLoadingSpansHaveNoLabelKey()
    {
        var json = JsonNode.Parse(CaptureSidecarWriter.ToJson(Info, Spans))!;
        Assert.Null(json["input"]![0]!["label"]);
        Assert.Equal("cutscene", (string?)json["keep"]![0]!["label"]);
    }

    [Fact]
    public void OfflineModeIsWrittenAsOffline()
    {
        var json = JsonNode.Parse(CaptureSidecarWriter.ToJson(Info with { Offline = true }, Spans))!;
        Assert.Equal("offline", (string?)json["mode"]);
    }

    [Fact]
    public void WriteIsAtomicAndNamedAfterTheVideo()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var video = Path.Combine(dir.FullName, "clip.mp4");
            CaptureSidecarWriter.Write(video, Info, Spans);
            Assert.True(File.Exists(video + ".mfcap.json"));
            Assert.Single(Directory.GetFiles(dir.FullName)); // no temp file left behind
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}

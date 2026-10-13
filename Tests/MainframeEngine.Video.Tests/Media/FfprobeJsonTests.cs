using MainframeEngine.Video.Media;

namespace MainframeEngine.Video.Tests.Media;

public sealed class FfprobeJsonTests
{
    private const string Landscape = """
        { "streams": [
            { "codec_type": "video", "codec_name": "h264", "width": 1920, "height": 1080, "r_frame_rate": "60/1", "avg_frame_rate": "60/1" },
            { "codec_type": "audio", "codec_name": "aac" } ],
          "format": { "duration": "61.250000" } }
        """;

    [Fact]
    public void ReadsDurationSizeRateAndStreams()
    {
        var info = FfprobeJson.Parse(Landscape, "/r/a.mp4");
        Assert.Equal(new MediaInfo(61.25, 1920, 1080, 60, HasAudio: true, VideoCodec: "h264", AudioCodec: "aac"), info);
    }

    [Fact]
    public void ARotatedPhoneRecordingReportsItsDisplayedSize()
    {
        const string json = """
            { "streams": [ { "codec_type": "video", "codec_name": "hevc", "width": 1920, "height": 1080, "avg_frame_rate": "30000/1001",
                             "side_data_list": [ { "rotation": -90 } ] } ],
              "format": { "duration": "5" } }
            """;
        var info = FfprobeJson.Parse(json, "/r/p.mov");
        Assert.Equal((1080, 1920), (info.Width, info.Height));
        Assert.Equal(29.97, info.FrameRate, 2);
        Assert.False(info.HasAudio);
    }

    [Fact]
    public void ABogusRealFrameRateFallsBackToTheAverage()
    {
        const string json = """
            { "streams": [ { "codec_type": "video", "width": 640, "height": 360, "r_frame_rate": "90000/1", "avg_frame_rate": "24/1" } ],
              "format": { "duration": "2" } }
            """;
        Assert.Equal(24, FfprobeJson.Parse(json, "/r/v.mkv").FrameRate);
    }

    [Theory]
    [InlineData("""{ "streams": [ { "codec_type": "audio" } ], "format": { "duration": "3" } }""", "no video stream")]
    [InlineData("""{ "streams": [ { "codec_type": "video", "width": 2, "height": 2 } ], "format": { } }""", "no duration")]
    [InlineData("not json", "could not read")]
    public void UnusableOutputIsAMediaProbeException(string json, string message)
    {
        var e = Assert.Throws<MediaProbeException>(() => FfprobeJson.Parse(json, "/r/x.mp4"));
        Assert.Contains(message, e.Message, StringComparison.Ordinal);
    }
}

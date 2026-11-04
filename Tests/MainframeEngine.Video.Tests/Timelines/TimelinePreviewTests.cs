using MainframeEngine.Video.Media;
using MainframeEngine.Video.Timelines;
using MainframeEngine.Video.Timelines.Export;
using static MainframeEngine.Video.Tests.Timelines.TimelineTestData;

namespace MainframeEngine.Video.Tests.Timelines;

public sealed class TimelinePreviewTests
{
    [Fact]
    public void PreviewSizeKeepsTheAspectWithinTheLongSide()
    {
        Assert.Equal((540, 960), TimelineGraphBuilder.PreviewSize(new TimelineSettings(1080, 1920, Rate30)));
        Assert.Equal((960, 540), TimelineGraphBuilder.PreviewSize(new TimelineSettings(3840, 2160, Rate30)));
        Assert.Equal((640, 360), TimelineGraphBuilder.PreviewSize(new TimelineSettings(640, 360, Rate30)));
        Assert.Equal(Rate30, TimelineGraphBuilder.PreviewRate(new TimelineSettings(1920, 1080, new FrameRate(60, 1))));
        Assert.Equal(new FrameRate(24, 1), TimelineGraphBuilder.PreviewRate(new TimelineSettings(1920, 1080, new FrameRate(24, 1))));
    }

    [Fact]
    public void TheVideoGraphIsComposedAtPreviewSizeWithScaledPositions()
    {
        var t = Append(Vertical(), 0, 2);
        var v = On(t, V1(t), 0);
        t = TimelineEdits.SetTransform(t, v.Id, v.Transform with { Fit = FrameFit.Fit, Scale = 0.5, Y = -500 });
        var g = TimelineGraphBuilder.BuildPreview(t, 0, 2, 540, 960, Rate30, audio: false);
        Assert.False(g.HasAudio);
        Assert.DoesNotContain("anullsrc", g.Graph, StringComparison.Ordinal);
        Assert.Contains("s=540x960:r=30", g.Graph, StringComparison.Ordinal);
        // 1080×1920 fit at 0.5 → 270×152 at preview size, centred, 250 px (500 × 0.5) above the middle.
        Assert.Contains("scale=270:152", g.Graph, StringComparison.Ordinal);
        Assert.Contains("pad=540:960:135:154", g.Graph, StringComparison.Ordinal);
        Assert.EndsWith("format=rgba[outv]", g.Graph, StringComparison.Ordinal);
        Assert.Single(g.InputArguments, a => a == "-i"); // the audio clip is not read
    }

    [Fact]
    public void TheAudioGraphHasOnlyTheMix()
    {
        var t = Append(Vertical(), 0, 2);
        var args = TimelinePreviewArguments.Audio(t, 0, 2, 44100, null);
        var graph = args[args.ToList().IndexOf("-filter_complex") + 1];
        Assert.DoesNotContain("overlay", graph, StringComparison.Ordinal);
        Assert.StartsWith("anullsrc=r=44100", graph, StringComparison.Ordinal);
        Assert.Contains("aresample=44100", graph, StringComparison.Ordinal);
        Assert.Equal(["-map", "[outa]", "-vn", "-ac", "2", "-ar", "44100", "-f", "f32le", "-"], args.TakeLast(10));
    }

    [Fact]
    public void AFrameReadsOneFrameAtTheTime()
    {
        var t = Append(Vertical(), 0, 4);
        var args = TimelinePreviewArguments.Frame(t, 1.01, 540, 960, Rate30, p => "/abs/" + p.Path);
        Assert.Equal(["-ss", "1", "-t", "0.033333", "-i", "/abs/Assets/Video/Media/rec.mp4"], args.Take(6));
        Assert.Equal(["-pix_fmt", "rgba", "-f", "rawvideo", "-frames:v", "1", "-"], args.TakeLast(7));
    }

    [Fact]
    public void AudioFilesProbeWithoutVideo()
    {
        const string json = """{ "streams": [ { "codec_type": "audio", "codec_name": "vorbis", "duration": "12.5" } ], "format": { "duration": "12.5" } }""";
        Assert.Throws<MediaProbeException>(() => FfprobeJson.Parse(json, "music.ogg"));
        var info = FfprobeJson.Parse(json, "music.ogg", requireVideo: false);
        Assert.False(info.HasVideo);
        Assert.True(info.HasAudio);
        Assert.Equal(12.5, info.DurationSeconds);
        var media = MediaRefs.From(info, "Assets/Video/Media/music.ogg", "aud_0123456789ab", "h");
        Assert.Equal((false, true, 0), (media.HasVideo, media.HasAudio, media.Width));
    }

    [Fact]
    public void PosterArguments() =>
        Assert.Equal(["-ss", "1", "-i", "/v/a.mp4", "-frames:v", "1", "-vf", "scale=160:-2", "-update", "1", "-y", "/c/p.png"],
            PosterExtractor.Arguments("/v/a.mp4", 60, 160, "/c/p.png"));
}

using MainframeEngine.Video.Media;
using MainframeEngine.Video.Media.Export;
using MainframeEngine.Video.Timelines;
using MainframeEngine.Video.Timelines.Export;
using static MainframeEngine.Video.Tests.Timelines.TimelineTestData;

namespace MainframeEngine.Video.Tests.Timelines;

public sealed class TimelineGraphBuilderTests
{
    private static TimelineGraph Build(Timeline t, TimelineExportPreset? preset = null) =>
        TimelineGraphBuilder.Build(t, preset ?? TimelineExportPresets.Timeline, TimelineLayout.Range(t).Start, TimelineLayout.Range(t).End);

    [Fact]
    public void OneClip()
    {
        var t = Append(Vertical(), 10, 12);
        var g = Build(t);
        Assert.Equal(["-ss", "10", "-t", "2", "-i", "Assets/Video/Media/rec.mp4", "-ss", "10", "-t", "2", "-i", "Assets/Video/Media/rec.mp4"], g.InputArguments);
        Assert.Equal(
            "color=c=0x000000:s=1080x1920:r=30:d=2,format=rgba[base];\n" +
            "[0:v]trim=start=0:end=2,setpts=PTS-STARTPTS,setpts=PTS-STARTPTS,fps=30,crop=608:1080:656:0,scale=1080:1920,setsar=1,format=rgba," +
            "pad=1080:1920:0:0:color=black@0,tpad=stop_mode=clone:stop_duration=0.033333,trim=duration=2,setpts=PTS-STARTPTS[v0];\n" +
            "[v0]concat=n=1:v=1:a=0[t1];\n" +
            "[base][t1]overlay=eof_action=pass:format=auto[o2];\n" +
            "[o2]setsar=1,format=yuv420p[outv];\n" +
            "anullsrc=r=48000:cl=stereo,atrim=duration=2[asil];\n" +
            "[1:a]atrim=start=0:end=2,asetpts=PTS-STARTPTS,afade=t=in:st=0:d=0.008,afade=t=out:st=1.992:d=0.008," +
            "asetpts=PTS-STARTPTS,aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,apad=whole_dur=2,atrim=duration=2[a3];\n" +
            "[asil][a3]amix=inputs=2:normalize=0:duration=first:dropout_transition=0[outa]",
            g.Graph);
        Assert.True(g.HasAudio);
        Assert.Equal((2.0, 1080, 1920), (g.Duration, g.Width, g.Height));
    }

    [Fact]
    public void TwoTracksWithAGapOverlayOncePerTrack()
    {
        var t = Append(Vertical(), 0, 2);
        t = TimelineEdits.Overwrite(t, Recording("Assets/Video/Media/b.mp4"), 0, 1, at: 3, V2(t), null); // V2: gap 0-3, clip 3-4
        var g = Build(t);
        Assert.Contains("color=c=black@0:s=1080x1920:r=30:d=3,format=rgba[g", g.Graph, StringComparison.Ordinal);
        Assert.Equal(2, g.Graph.Split("overlay=eof_action=pass").Length - 1);
        Assert.Contains(":d=4,format=rgba[base]", g.Graph, StringComparison.Ordinal); // the whole range
        Assert.Contains("concat=n=2:v=1:a=0", g.Graph, StringComparison.Ordinal); // gap + clip on V2
    }

    [Fact]
    public void CutsSpliceAndAudioIsDelayedToItsStart()
    {
        var t = Append(Append(Vertical(), 0, 2), 10, 20);
        t = Cut(t, On(t, V1(t), 1), 12, 15);
        var g = Build(t);
        Assert.Contains("split=2[s", g.Graph, StringComparison.Ordinal);
        Assert.Contains("trim=start=0:end=2,setpts=PTS-STARTPTS[p", g.Graph, StringComparison.Ordinal);
        Assert.Contains("trim=start=5:end=10,setpts=PTS-STARTPTS[p", g.Graph, StringComparison.Ordinal); // window-relative
        Assert.Contains("asplit=2", g.Graph, StringComparison.Ordinal);
        Assert.Contains("adelay=delays=2000:all=1", g.Graph, StringComparison.Ordinal);
        Assert.Equal(9, g.Duration, 6);
    }

    [Fact]
    public void SpeedChangesPtsAndChainsAtempo()
    {
        Assert.Equal("atempo=2,atempo=2", TimelineGraphBuilder.Atempo(4));
        Assert.Equal("atempo=0.5,atempo=0.5", TimelineGraphBuilder.Atempo(0.25));
        Assert.Equal("atempo=1.5", TimelineGraphBuilder.Atempo(1.5));
        Assert.Equal("", TimelineGraphBuilder.Atempo(1));

        var t = Append(Vertical(), 0, 10);
        t = t with { Clips = [.. t.Clips.Select(c => c with { Speed = 2 })] };
        var g = Build(t);
        Assert.Contains("setpts=(PTS-STARTPTS)/2", g.Graph, StringComparison.Ordinal);
        Assert.Contains("atempo=2", g.Graph, StringComparison.Ordinal);
        Assert.Equal(5, g.Duration, 6);
    }

    [Fact]
    public void MutedHiddenAndSoloTracksAreLeftOut()
    {
        var t = Append(Vertical(), 0, 2);
        t = TimelineEdits.Append(t, Music(), 0, 2, null, A2(t));
        Assert.Equal(3, Build(t).Graph.Split("amix=inputs=")[1][0] - '0'); // silence + two clips
        var muted = TimelineEdits.SetTrack(t, A2(t), x => x with { Muted = true });
        Assert.Contains("amix=inputs=2", Build(muted).Graph, StringComparison.Ordinal);
        var solo = TimelineEdits.SetTrack(t, A2(t), x => x with { Solo = true, GainDb = -6 });
        var soloGraph = Build(solo).Graph;
        Assert.Contains("amix=inputs=2", soloGraph, StringComparison.Ordinal);
        Assert.Contains("volume=-6dB", soloGraph, StringComparison.Ordinal);
        var hidden = TimelineEdits.SetTrack(t, V1(t), x => x with { Enabled = false });
        Assert.DoesNotContain("overlay", Build(hidden).Graph, StringComparison.Ordinal);
    }

    [Fact]
    public void RangeTrimsTheClipsToIt()
    {
        var t = TimelineEdits.SetInOut(Append(Vertical(), 0, 10), 2, 5);
        var g = Build(t);
        Assert.Equal(3, g.Duration, 6);
        Assert.Equal(["-ss", "2", "-t", "3"], g.InputArguments.Take(4));
        Assert.Throws<InvalidOperationException>(() => TimelineGraphBuilder.Build(t, TimelineExportPresets.Timeline, 5, 5));
    }

    [Fact]
    public void PresetSizeLetterboxesAndRateConverts()
    {
        var landscape = TimelineFactory.New(new TimelineSettings(1920, 1080, new FrameRate(60, 1)));
        landscape = TimelineEdits.Append(landscape, Recording(), 0, 2, V1(landscape), A1(landscape));
        var g = Build(landscape, TimelineExportPresets.Vertical30);
        Assert.Contains("scale=1080:1920:force_original_aspect_ratio=decrease:force_divisible_by=2,pad=1080:1920:(ow-iw)/2:(oh-ih)/2", g.Graph, StringComparison.Ordinal);
        Assert.Contains("fps=30,setsar=1,format=yuv420p[outv]", g.Graph, StringComparison.Ordinal);
        Assert.Equal((1080, 1920), (g.Width, g.Height));
    }

    [Fact]
    public void GifUsesPaletteAndNoAudio()
    {
        var t = Append(Vertical(), 0, 2);
        var g = Build(t, TimelineExportPresets.GifLoop);
        Assert.False(g.HasAudio);
        Assert.DoesNotContain("anullsrc", g.Graph, StringComparison.Ordinal);
        Assert.Contains("palettegen=stats_mode=diff", g.Graph, StringComparison.Ordinal);
        Assert.EndsWith("paletteuse=dither=bayer:bayer_scale=5[outv]", g.Graph, StringComparison.Ordinal);
        Assert.Equal((480, 854), (g.Width, g.Height)); // 480 wide at 9:16
        Assert.Equal(2, g.InputArguments.Count / 6 + 1); // the video clip only... and its input
    }

    [Fact]
    public void OpacityAndOffFrameClips()
    {
        var t = Append(Vertical(), 0, 2);
        var v = On(t, V1(t), 0);
        var faded = TimelineEdits.SetTransform(t, v.Id, v.Transform with { Opacity = 0.5 });
        Assert.Contains("colorchannelmixer=aa=0.5", Build(faded).Graph, StringComparison.Ordinal);
        var gone = TimelineEdits.SetTransform(t, v.Id, v.Transform with { X = 99999 });
        Assert.DoesNotContain("crop=", Build(gone).Graph, StringComparison.Ordinal); // drawn as a transparent gap
    }
}

public sealed class ExportPresetsTests
{
    [Fact]
    public void SizeCapBitrate()
    {
        Assert.Equal((long)(25 * 8 * 1024 * 1024 / 30.0 - 128_000), TimelineExportPresets.SizeCappedVideoBitrate(TimelineExportPresets.ChatSizeCap, 30, 128));
        Assert.Equal(100_000, TimelineExportPresets.SizeCappedVideoBitrate(1_000, 600, 128)); // never below 100 kbit/s
    }

    [Fact]
    public void SizesFollowTheTimelinesAspect()
    {
        var vertical = new TimelineSettings(1080, 1920, Rate30);
        var landscape = new TimelineSettings(1920, 1080, Rate30);
        Assert.Equal((720, 1280), TimelineExportPresets.Chat.SizeFor(vertical));
        Assert.Equal((1280, 720), TimelineExportPresets.Chat.SizeFor(landscape));
        Assert.Equal((1080, 1920), TimelineExportPresets.Vertical30.SizeFor(landscape)); // exact: letterboxed
        Assert.Equal((720, 406), TimelineExportPresets.WebMLoop.SizeFor(landscape));
        Assert.Equal((1920, 1080), TimelineExportPresets.Timeline.SizeFor(landscape));
        Assert.Equal(Rate30, TimelineExportPresets.YouTube1080.RateFor(vertical));
        Assert.All(TimelineExportPresets.All, p => Assert.Same(p, TimelineExportPresets.Find(p.Id)));
        Assert.Equal(".gif", TimelineExportPresets.GifLoop.Extension);
    }
}

public sealed class TimelineExportArgumentsTests
{
    private static readonly FfmpegCapabilities Ffmpeg7 = new(7);

    [Fact]
    public void Mp4UsesCrfWithLibx264AndMapsAudio()
    {
        var t = Append(Vertical(), 0, 2);
        var g = TimelineGraphBuilder.Build(t, TimelineExportPresets.Vertical30, 0, 2);
        var args = TimelineExportArguments.Build(g, TimelineExportPresets.Vertical30, DeliveryEncoder.Software, Ffmpeg7, "/tmp/g.txt", "/out/a.mp4");
        Assert.Equal(["-/filter_complex", "/tmp/g.txt", "-map", "[outv]", "-map", "[outa]", "-c:v", "libx264", "-crf", "20"], args.Skip(12).Take(10));
        Assert.Contains("+faststart", args);
        Assert.Equal(["-progress", "pipe:1", "-y", "/out/a.mp4"], args.TakeLast(4));
    }

    [Fact]
    public void SizeCappedPresetsSetTheBitrate()
    {
        var t = Append(Vertical(), 0, 30);
        var g = TimelineGraphBuilder.Build(t, TimelineExportPresets.Chat, 0, 30);
        var args = TimelineExportArguments.Build(g, TimelineExportPresets.Chat, "h264_videotoolbox", Ffmpeg7, "g.txt", "a.mp4").ToList();
        var bitrate = TimelineExportPresets.SizeCappedVideoBitrate(TimelineExportPresets.ChatSizeCap, 30, 128).ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(bitrate, args[args.IndexOf("-b:v") + 1]);
        Assert.Equal(bitrate, args[args.IndexOf("-maxrate") + 1]);
        Assert.Equal("h264_videotoolbox", args[args.IndexOf("-c:v") + 1]);
        Assert.Equal("128k", args[args.IndexOf("-b:a") + 1]);
    }

    [Fact]
    public void GifAndSilentWebMHaveNoAudio()
    {
        var t = Append(Vertical(), 0, 2);
        var gif = TimelineExportArguments.Build(TimelineGraphBuilder.Build(t, TimelineExportPresets.GifLoop, 0, 2), TimelineExportPresets.GifLoop, "", Ffmpeg7, "g", "a.gif");
        Assert.DoesNotContain("[outa]", gif);
        Assert.Contains("-loop", gif);
        var webm = TimelineExportArguments.Build(TimelineGraphBuilder.Build(t, TimelineExportPresets.WebMLoop, 0, 2), TimelineExportPresets.WebMLoop, "", Ffmpeg7, "g", "a.webm");
        Assert.Contains("libvpx-vp9", webm);
        Assert.Contains("-an", webm);
    }

    [Fact]
    public void FilmstripArguments()
    {
        var args = FilmstripExtractor.Arguments("/v/a.mp4", 60, 12, 72, "/c/strip.png");
        Assert.Equal(["-i", "/v/a.mp4", "-vf", "fps=0.2,scale=-2:72,tile=12x1", "-frames:v", "1", "-update", "1", "-y", "/c/strip.png"], args);
    }
}

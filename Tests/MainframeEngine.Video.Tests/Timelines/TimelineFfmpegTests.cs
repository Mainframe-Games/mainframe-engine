using System.Diagnostics;
using MainframeEngine.Video.Media;
using MainframeEngine.Video.Timelines;
using MainframeEngine.Video.Timelines.Export;
using static MainframeEngine.Video.Tests.Timelines.TimelineTestData;

namespace MainframeEngine.Video.Tests.Timelines;

/// <summary>
/// Runs real ffmpeg on generated graphs (<c>just test-video-ffmpeg</c>; skipped unless <c>MAINFRAME_VIDEO_FFMPEG_TESTS=1</c>,
/// so <c>just test</c> and CI never start ffmpeg). Renders lavfi test sources through a two-track vertical timeline
/// with each kind of preset and checks the result with ffprobe; the recipe keeps the files in <c>artifacts/video-ffmpeg</c>.
/// </summary>
public sealed class TimelineFfmpegTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("mf-timeline-ffmpeg-");

    public void Dispose() => _dir.Delete(recursive: true);

    private static FfmpegLocation Ffmpeg()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("MAINFRAME_VIDEO_FFMPEG_TESTS") == "1",
            "Real-ffmpeg export tests run with `just test-video-ffmpeg` (MAINFRAME_VIDEO_FFMPEG_TESTS=1).");
        return new FfmpegLocator(Environment.GetEnvironmentVariable("MAINFRAME_FFMPEG")).TryLocate()
               ?? throw new InvalidOperationException("MAINFRAME_VIDEO_FFMPEG_TESTS=1 but no ffmpeg was found.");
    }

    [Theory]
    [InlineData("vertical-30", 1080, 1920, 30.0)]
    [InlineData("chat", 720, 1280, 30.0)]
    [InlineData("gif", 480, 854, 20.0)]
    [InlineData("webm-loop", 720, 1280, 30.0)]
    public async Task ExportsATwoTrackVerticalTimeline(string presetId, int width, int height, double fps)
    {
        var ffmpeg = Ffmpeg();
        var ct = TestContext.Current.CancellationToken;
        var source = Path.Combine(_dir.FullName, "rec.mp4");
        await Run(ffmpeg.FfmpegPath, ["-f", "lavfi", "-i", "testsrc2=size=1920x1080:rate=60", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000",
            "-t", "6", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", "-y", source]);
        var info = await new MediaProbe(ffmpeg).ProbeAsync(source, ct);
        var media = new MediaRef("", source, "h", info.DurationSeconds, info.Width, info.Height, info.FrameRate, true, info.HasAudio);

        // V1: 1–4 s of the recording, filled (cropped to 9:16) and with a 0.5 s cut; V2: a picture-in-picture from 1 s.
        var t = Vertical();
        t = TimelineEdits.Append(t, media, 1, 4, V1(t), A1(t));
        t = Cut(t, On(t, V1(t), 0), 2, 2.5);
        t = TimelineEdits.Overwrite(t, media, 0, 1, at: 1, V2(t), null);
        var pip = On(t, V2(t), 0);
        t = TimelineEdits.SetTransform(t, pip.Id, pip.Transform with { Fit = FrameFit.Fit, Scale = 0.5, Y = -500 });
        var (start, end) = TimelineLayout.Range(t);
        Assert.Equal(2.5, end, 6);

        var preset = TimelineExportPresets.Find(presetId)!;
        var output = Path.Combine(_dir.FullName, "out" + preset.Extension);
        using var temp = new TempSession();
        await new TimelineExporter(ffmpeg, temp).ExportAsync(t, preset, start, end, output, null, null, ct);

        if (Environment.GetEnvironmentVariable("MAINFRAME_VIDEO_FFMPEG_OUT") is { Length: > 0 } keep) // for a look by eye
        {
            Directory.CreateDirectory(keep);
            File.Copy(output, Path.Combine(keep, presetId + preset.Extension), overwrite: true);
        }

        var result = await new MediaProbe(ffmpeg).ProbeAsync(output, ct);
        Assert.Equal((width, height), (result.Width, result.Height));
        Assert.Equal(fps, result.FrameRate, 1);
        Assert.InRange(result.DurationSeconds, 2.5 - 0.15, 2.5 + 0.15);
        Assert.Equal(preset.HasAudio, result.HasAudio);
    }

    private static async Task Run(string exe, IEnumerable<string> args)
    {
        using var process = FfmpegProcess.Start(exe, args, redirectStandardOutput: false);
        var stderr = process.StandardError.ReadToEndAsync();
        await FfmpegProcess.WaitForSuccessAsync(process, stderr, "Making a test source", CancellationToken.None);
        Debug.Assert(process.HasExited);
    }
}

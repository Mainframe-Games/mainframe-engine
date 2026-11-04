using System.Numerics;
using MainframeEngine.Editor.Tests.Projects;
using MainframeEngine.Editor.Video;
using MainframeEngine.Video.Media;
using MainframeEngine.Video.Timelines;
using MainframeEngine.Video.Timelines.Export;
using Silk.NET.Input;
using Timeline = MainframeEngine.Video.Timelines.Timeline;

namespace MainframeEngine.Editor.Tests.Video;

/// <summary>
/// A headless editor on a game project with ffmpeg faked at video mode's seams (an ffmpeg and ffprobe that only exist as
/// files, the prober, the decoders, the exporter), and the helpers the video-mode tests share.
/// </summary>
public abstract class VideoEditorTestBase : IDisposable
{
    private protected readonly TestProject _project;
    private protected readonly string _ffmpeg = Directory.CreateTempSubdirectory("mf-fake-ffmpeg").FullName;
    private protected readonly string _outside = Directory.CreateTempSubdirectory("mf-outside-media").FullName;
    private protected readonly string _models = Directory.CreateTempSubdirectory("mf-whisper-models").FullName;
    private protected readonly HeadlessEditor _editor;
    private protected readonly List<(Timeline Timeline, TimelineExportPreset Preset, double Start, double End, string Path)> _exports = [];

    protected VideoEditorTestBase(string project = "Clips")
    {
        _project = new TestProject(project);
        var locator = new FfmpegLocator();
        File.WriteAllBytes(Path.Combine(_ffmpeg, locator.ExecutableName("ffmpeg")), []);
        File.WriteAllBytes(Path.Combine(_ffmpeg, locator.ExecutableName("ffprobe")), []);
        _editor = new HeadlessEditor(configure: o => o with
        {
            InitialProject = _project.Root,
            VideoModelsDirectory = _models,
            GameBuilder = new FakeGameBuilder(),
            GameLauncher = new FakeGameLauncher(),
        });
        var settings = W.Settings.Clone();
        settings.VideoFfmpegPath = _ffmpeg;
        W.ApplySettings(settings);
        W.VideoView.DecoderFactory = static _ => new VideoGolden.Decoders();
        W.VideoView.Prober = static (_, full, stored, uid, _) => Task.FromResult(Probe(full, stored, uid));
        W.VideoView.ExporterFactory = (_, _) => (timeline, preset, start, end, path, _, _, _) =>
        {
            lock (_exports)
                _exports.Add((timeline, preset, start, end, path));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "mp4");
            return Task.CompletedTask;
        };
        _editor.Tick(3);
    }

    public virtual void Dispose()
    {
        _editor.Dispose();
        _project.Dispose();
        Directory.Delete(_ffmpeg, recursive: true);
        Directory.Delete(_outside, recursive: true);
        Directory.Delete(_models, recursive: true);
    }

    protected EditorWorkspace W => _editor.Workspace;

    // What ffprobe would say: 10 s 1080p30 video with sound, or 30 s of audio.
    protected static MediaRef Probe(string full, string stored, string uid)
    {
        if (TimelineFactory.IsImagePath(full))
            return TimelineFactory.ImageMedia(new MediaRef(uid, stored.Replace('\\', '/'), "hash-" + Path.GetFileName(full), 0, 640, 480, 0, true, false));
        var audio = MediaPool.KindOf(full, isDirectory: false) == MediaPoolKind.Audio;
        return new MediaRef(uid, stored.Replace('\\', '/'), "hash-" + Path.GetFileName(full), audio ? 30 : 10,
            audio ? 0 : 1920, audio ? 0 : 1080, audio ? 0 : 30, HasVideo: !audio, HasAudio: true);
    }

    protected void TickUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            _editor.Tick();
            Thread.Sleep(5);
        }

        Assert.True(condition(), $"timed out waiting for {what}");
    }

    protected string MediaFile(string name, string folder = "Assets/Video/Media")
    {
        var path = _project.Abs($"{folder}/{name}");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "media");
        return path;
    }

    protected MediaRef MediaOf(string path)
    {
        MediaRef? media = null;
        W.VideoView.WithMedia(path, m => media = m);
        TickUntil(() => media is not null, "the probe");
        return media!;
    }

    protected TimelineTab NewTimeline(string name = "Edit", string? media = null)
    {
        W.SetMode(EditorMode.Video);
        _editor.Tick(2);
        var pool = Assert.IsType<MediaPool>(W.VideoPanel.Pool);
        W.VideoView.CreateTimeline(pool, name, new TimelineSettings(1920, 1080, new FrameRate(30, 1)), media is null ? null : MediaOf(media));
        _editor.Tick(3);
        return Assert.IsType<TimelineTab>(W.Session.ActiveTab);
    }

    protected static string V1(TimelineTab tab) => tab.Timeline.TracksOf(TrackKind.Video)[0].Id;

    protected static string V2(TimelineTab tab) => tab.Timeline.TracksOf(TrackKind.Video)[1].Id;

    protected static string A1(TimelineTab tab) => tab.Timeline.TracksOf(TrackKind.Audio)[0].Id;

}

using MainframeEngine.Editor.Video;
using MainframeEngine.Tests;

namespace MainframeEngine.Editor.Tests.Video;

/// <summary>A timeline tab over the golden's timeline (fake decoders, the waveforms given) allocates nothing when steady.</summary>
[Collection(nameof(SerialEditor))]
public sealed class TimelineAllocationTests : IDisposable
{
    private readonly HeadlessEditor _editor = new();

    public void Dispose() => _editor.Dispose();

    [Fact]
    public async Task SteadyFramesWithATimelineTabAllocateNothing()
    {
        var w = _editor.Workspace;
        var root = VideoGolden.Project();
        w.Session.UseProjectOf(Path.Combine(root, ProjectSettings.FileName), allowSwitch: true);
        var cache = w.VideoView.CacheFor(null);
        cache?.SetWaveform(VideoGolden.Recording.Hash, VideoGolden.Waveform());
        cache?.SetWaveform(VideoGolden.Music.Hash, VideoGolden.Waveform());
        var decoders = new VideoGolden.Decoders();
        var document = new TimelineDocument(VideoGolden.Timeline(), Path.Combine(root, "Assets", "Video", "Timelines", "Trailer.mftimeline"), root);
        var tab = w.Session.OpenTimeline(document, decoders, ffmpeg: null, cache);
        await decoders.FirstFrameRead.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        w.SetMode(EditorMode.Video);
        tab.View.Selection.Add(tab.Timeline.ClipsOn(tab.Timeline.TracksOf(MainframeEngine.Video.Timelines.TrackKind.Video)[0].Id)[0].Id);
        tab.View.Fit(MainframeEngine.Video.Timelines.TimelineLayout.Duration(tab.Timeline));
        tab.Seek(VideoGolden.Playhead);
        tab.Notice = VideoGolden.Notice;
        _editor.Tick(60);

        var allocated = AllocationGate.SmallestWindow(() => _editor.Tick(100));
        Assert.True(allocated == 0, $"{allocated} B allocated over 100 steady frames with a timeline tab");
    }
}


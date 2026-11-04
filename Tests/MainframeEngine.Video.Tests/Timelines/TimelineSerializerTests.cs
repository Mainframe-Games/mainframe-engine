using MainframeEngine.Video.Timelines;
using static MainframeEngine.Video.Tests.Timelines.TimelineTestData;

namespace MainframeEngine.Video.Tests.Timelines;

public sealed class TimelineSerializerTests
{
    private static Timeline Busy()
    {
        var t = Append(Append(Vertical(), 0, 10), 20, 25);
        t = Cut(t, On(t, V1(t), 0), 2, 4);
        t = TimelineEdits.SetTransform(t, On(t, V1(t), 0).Id, new ClipTransform(FrameFit.Fit, 0.8, 10, -20, new CropRect(1, 2, 3, 4), 0.5));
        t = TimelineEdits.Append(t, Music(), 0, 30, null, A2(t));
        t = TimelineEdits.SetTrack(t, A2(t), x => x with { GainDb = -6, Solo = true });
        t = TimelineEdits.AddMarker(t, 3, "drop");
        return TimelineEdits.SetInOut(t, 1, 12);
    }

    [Fact]
    public void RoundTripsAndRefusesNewer()
    {
        var t = Busy();
        var json = TimelineSerializer.Serialize(t);
        var back = TimelineSerializer.Deserialize(json);
        Assert.Equal(json, TimelineSerializer.Serialize(back));
        Assert.Equal(t.Uid, back.Uid);
        Assert.Equal(t.Settings, back.Settings);
        Assert.Equal(t.Tracks, back.Tracks);
        Assert.StartsWith("{\n  \"version\": 1,\n  \"uid\": \"vtl_", json.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("\"kind\": \"video\"", json, StringComparison.Ordinal);
        Assert.Contains("\"fit\": \"fit\"", json, StringComparison.Ordinal);

        var newer = json.Replace("\"version\": 1", "\"version\": 2", StringComparison.Ordinal);
        var e = Assert.Throws<TimelineFormatException>(() => TimelineSerializer.Deserialize(newer));
        Assert.Contains("newer version", e.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{ \"uid\": \"vtl_0123456789ab\" }")]
    [InlineData("{ \"version\": 1 }")]
    public void BadFilesAreRefusedReadably(string json) =>
        Assert.Throws<TimelineFormatException>(() => TimelineSerializer.Deserialize(json));

    [Fact]
    public void InconsistentContentsAreRefused()
    {
        var json = TimelineSerializer.Serialize(Busy());
        var t = TimelineSerializer.Deserialize(json);
        var clip = On(t, V1(t), 0);
        Assert.Throws<TimelineFormatException>(() => TimelineSerializer.Validate(t with { Clips = t.Clips.Replace(clip, clip with { TrackId = "nope" }) }));
        Assert.Throws<TimelineFormatException>(() => TimelineSerializer.Validate(t with { Clips = t.Clips.Replace(clip, clip with { TrackId = A2(t), Media = clip.Media with { HasAudio = false } }) }));
        Assert.Throws<TimelineFormatException>(() => TimelineSerializer.Validate(t with { Clips = t.Clips.Replace(clip, clip with { Segments = clip.Segments.RemoveAt(0) }) }));
        Assert.Throws<TimelineFormatException>(() => TimelineSerializer.Validate(t with { Clips = t.Clips.Replace(clip, clip with { SourceOut = 99 }) }));
    }

    [Fact]
    public async Task SavesAtomicallyAndLoads()
    {
        var dir = Directory.CreateTempSubdirectory("mf-timeline-");
        try
        {
            var path = Path.Combine(dir.FullName, "Edit.mftimeline");
            var t = Busy();
            await TimelineSerializer.SaveAsync(t, path, TestContext.Current.CancellationToken);
            Assert.False(File.Exists(path + ".tmp"));
            Assert.True(TimelineSerializer.IsTimelinePath(path));
            var back = await TimelineSerializer.LoadAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(TimelineSerializer.Serialize(t), TimelineSerializer.Serialize(back));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}

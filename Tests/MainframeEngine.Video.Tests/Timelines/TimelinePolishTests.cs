using MainframeEngine.Video.Timelines;
using MainframeEngine.Video.Timelines.Export;
using static MainframeEngine.Video.Tests.Timelines.TimelineTestData;

namespace MainframeEngine.Video.Tests.Timelines;

/// <summary>
/// G20.2 on the timeline library: volume and fades, speed, stills (titles and images), transitions, in the model, the
/// file and the export graph.
/// </summary>
public sealed class TimelinePolishTests
{
    private static TimelineGraph Graph(Timeline t) => TimelineGraphBuilder.Build(t, TimelineExportPresets.Timeline, 0, TimelineLayout.Duration(t));

    // A at 0–4 s and B at 4–8 s of one 60 s recording (handles on both sides of the edit), linked V1/A1 pairs.
    private static Timeline TwoClips() => Append(Append(Vertical(), 0, 4), 10, 14);

    // ── File ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheNewFieldsRoundTripAndAVersion1FileOpens()
    {
        var t = TwoClips();
        var a = On(t, V1(t), 0);
        t = TimelineEdits.SetMix(t, a.Id, -6, 0.5, 1);
        t = TimelineEdits.AddTransition(t, a.Id, On(t, V1(t), 1).Id, TransitionKind.Dissolve, 1);
        t = TimelineEdits.PlaceStill(t, TimelineFactory.TitleMedia(TitleStyle.Default with { Text = "Hi" }, t.Settings), ClipKind.Title,
            TitleStyle.Default with { Text = "Hi" }, 1, 2, V2(t), insert: false);

        var back = TimelineSerializer.Deserialize(TimelineSerializer.Serialize(t));

        Assert.Equal(t.Transitions, back.Transitions);
        var title = Assert.Single(back.ClipsOn(V2(back)));
        Assert.Equal(ClipKind.Title, title.Kind);
        Assert.Equal("Hi", title.Title!.Text);
        var mixed = back.FindClip(a.Id)!;
        Assert.Equal((-6.0, 0.5, 1.0), (mixed.VolumeDb, mixed.FadeIn, mixed.FadeOut));

        // A version 1 file (no kinds, fades, titles or transitions) opens with the defaults.
        var v1 = TimelineSerializer.Serialize(TwoClips()).Replace("\"version\": 2", "\"version\": 1", StringComparison.Ordinal);
        var old = TimelineSerializer.Deserialize(v1);
        Assert.All(old.Clips, c => Assert.Equal((ClipKind.Media, 0.0, 0.0, 0.0), (c.Kind, c.VolumeDb, c.FadeIn, c.FadeOut)));
        Assert.Empty(old.Transitions);
    }

    // ── Edits ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void MixClampsAndSnapsToFrames()
    {
        var t = TwoClips();
        var a = On(t, V1(t), 0);
        t = TimelineEdits.SetMix(t, a.Id, -100, 0.51, 99);

        var mixed = t.FindClip(a.Id)!;
        Assert.Equal(-60, mixed.VolumeDb);
        Assert.Equal(0.5, mixed.FadeIn, 6); // 15.3 frames → 15
        Assert.Equal(3.5, mixed.FadeOut, 6); // what is left of the 4 s
        Assert.Same(t, TimelineEdits.SetMix(t, a.Id, -60, 0.5, 3.5)); // nothing changed
    }

    [Fact]
    public void SpeedRipplesAndStaysInRange()
    {
        var t = TwoClips();
        var a = On(t, V1(t), 0);
        t = TimelineEdits.SetSpeed(t, a.Id, 2);

        Assert.Equal(2, TimelineLayout.Length(t.FindClip(a.Id)!, t.Rate), 6); // halves
        Assert.Equal(2, On(t, V1(t), 1).Start, 6); // the next clip rippled
        Assert.Equal(2, On(t, A1(t), 0).Speed); // the linked sound too
        Assert.Equal(TimelineEdits.MaxSpeed, TimelineEdits.SetSpeed(t, a.Id, 99).FindClip(a.Id)!.Speed);
    }

    [Fact]
    public void StillsPlaceForTheirLength()
    {
        var t = Vertical();
        var image = TimelineFactory.ImageMedia(Recording("Assets/Video/Media/logo.png") with { Width = 512, Height = 512 });
        t = TimelineEdits.PlaceStill(t, image, ClipKind.Image, null, 2, TimelineEdits.DefaultStillSeconds, V1(t), insert: false);

        var clip = Assert.Single(t.Clips);
        Assert.Equal(ClipKind.Image, clip.Kind);
        Assert.Equal(5, TimelineLayout.Length(clip, t.Rate), 6);
        Assert.Equal(FrameFit.Fit, clip.Transform.Fit);
        Assert.False(clip.Media.HasAudio); // no sound: nothing on A1
        Assert.Contains("-loop", Graph(t).InputArguments);
    }

    [Fact]
    public void ATitlesMediaFollowsItsStyle()
    {
        var t = Vertical();
        var style = TitleStyle.Default with { Text = "Boss fight" };
        t = TimelineEdits.PlaceStill(t, TimelineFactory.TitleMedia(style, t.Settings), ClipKind.Title, style, 0, 3, V1(t), insert: false);
        var clip = Assert.Single(t.Clips);
        var before = clip.Media.Hash;

        t = TimelineEdits.SetTitle(t, clip.Id, style with { Text = "Victory" });

        Assert.NotEqual(before, t.Clips[0].Media.Hash); // a new raster
        Assert.Equal(TimelineFactory.TitleMedia(style with { Text = "Victory" }, t.Settings), t.Clips[0].Media);
        Assert.Same(t, TimelineEdits.SetTitle(t, clip.Id, style with { Text = "Victory" }));
    }

    [Fact]
    public void TransitionNeedsHandles()
    {
        // B starts at the very first frame of its recording: nothing before it to dissolve from.
        var t = Append(Vertical(), 0, 4);
        t = TimelineEdits.Append(t, Recording("Assets/Video/Media/b.mp4"), 0, 4, V1(t), null);
        var a = On(t, V1(t), 0);
        var b = On(t, V1(t), 1);

        Assert.Equal(TimelineEdits.NotEnoughFrames, TimelineEdits.TransitionProblem(t, a.Id, b.Id, TransitionKind.Dissolve, 1));
        Assert.Same(t, TimelineEdits.AddTransition(t, a.Id, b.Id, TransitionKind.Dissolve, 1));
        Assert.Null(TimelineEdits.TransitionProblem(t, a.Id, b.Id, TransitionKind.DipToBlack, 1)); // a dip needs none
        Assert.NotNull(TimelineEdits.TransitionProblem(t, b.Id, a.Id, TransitionKind.DipToBlack, 1)); // not an edit
    }

    [Fact]
    public void ADissolveOnALinkedPairCrossfadesItsSoundAndGoesWhenTheEditDoes()
    {
        var t = TwoClips();
        var a = On(t, V1(t), 0);
        t = TimelineEdits.AddTransition(t, a.Id, On(t, V1(t), 1).Id, TransitionKind.Dissolve, 1);

        Assert.Equal(2, t.Transitions.Length); // the picture's, and the sound's twin
        Assert.Contains(t.Transitions, x => x.TrackId == A1(t));
        var twin = t.Transitions[0].Id;
        Assert.Empty(TimelineEdits.RemoveTransition(t, twin).Transitions); // removing one removes both

        var moved = TimelineEdits.Move(t, On(t, V1(t), 1).Id, 6); // the edit opens into a gap
        Assert.DoesNotContain(moved.Transitions, x => x.TrackId == V1(t));
    }

    // ── Graph ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CrossDissolve()
    {
        // 1 s: half a second (15 frames at 30 fps) each side of the edit. A dissolve is centred on whole frames, so its
        // length is an even number of frames.
        var t = TwoClips();
        t = TimelineEdits.AddTransition(t, On(t, V1(t), 0).Id, On(t, V1(t), 1).Id, TransitionKind.Dissolve, 1);

        var graph = Graph(t);

        // A plays 0.5 s past its out and B starts 0.5 s before its in; the picture dissolves across the edit.
        Assert.Contains("xfade=transition=fade:duration=1:offset=3.5", graph.Graph, StringComparison.Ordinal);
        Assert.Contains("-ss 9.5 -t 4.5", string.Join(' ', graph.InputArguments), StringComparison.Ordinal);
        // The sounds overlap by 1 s, A fading out and B fading in.
        Assert.Contains("afade=t=out:st=3.5:d=1", graph.Graph, StringComparison.Ordinal);
        Assert.Contains("afade=t=in:st=0:d=1", graph.Graph, StringComparison.Ordinal);
        Assert.Contains("adelay=delays=3500", graph.Graph, StringComparison.Ordinal);
        Assert.Equal(8, graph.Duration, 6); // the timeline keeps its length
    }

    [Fact]
    public void DipToBlackFadesBothSides()
    {
        var t = TwoClips();
        t = TimelineEdits.AddTransition(t, On(t, V1(t), 0).Id, On(t, V1(t), 1).Id, TransitionKind.DipToBlack, 1);

        var graph = Graph(t).Graph;

        Assert.DoesNotContain("xfade", graph, StringComparison.Ordinal);
        Assert.Contains("fade=t=out:st=3.5:d=0.5:color=black", graph, StringComparison.Ordinal);
        Assert.Contains("fade=t=in:st=0:d=0.5:color=black", graph, StringComparison.Ordinal);
    }

    [Fact]
    public void SpeedChainsAtempo()
    {
        var t = Append(Vertical(), 0, 8);
        var clip = On(t, V1(t), 0);

        var twice = TimelineEdits.SetSpeed(t, clip.Id, 2);
        Assert.Equal(4, TimelineLayout.Duration(twice), 6);
        Assert.Contains("atempo=2,", Graph(twice).Graph, StringComparison.Ordinal);

        var four = TimelineEdits.SetSpeed(t, clip.Id, 4);
        Assert.Contains("atempo=2,atempo=2,", Graph(four).Graph, StringComparison.Ordinal);
        Assert.Contains("setpts=(PTS-STARTPTS)/4", Graph(four).Graph, StringComparison.Ordinal);
    }

    [Fact]
    public void VolumeAndFadesReachTheGraph()
    {
        var t = Append(Vertical(), 0, 8);
        t = TimelineEdits.SetMix(t, On(t, V1(t), 0).Id, 0, 0.5, 1);
        t = TimelineEdits.SetMix(t, On(t, A1(t), 0).Id, -6, 1, 0);

        var graph = Graph(t).Graph;

        Assert.Contains("fade=t=in:st=0:d=0.5:color=black", graph, StringComparison.Ordinal);
        Assert.Contains("fade=t=out:st=7:d=1:color=black", graph, StringComparison.Ordinal);
        Assert.Contains("volume=-6dB", graph, StringComparison.Ordinal);
        Assert.Contains("afade=t=in:st=0:d=1", graph, StringComparison.Ordinal);
    }
}

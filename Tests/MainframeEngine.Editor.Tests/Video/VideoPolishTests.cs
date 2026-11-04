using System.Numerics;
using MainframeEngine.Editor.Video;
using MainframeEngine.Video.Timelines;
using MainframeEngine.Video.Timelines.Export;
using Silk.NET.Input;
using Clip = MainframeEngine.Video.Timelines.Clip;

namespace MainframeEngine.Editor.Tests.Video;

/// <summary>
/// G20.2 in the editor: titles (added, styled in the Inspector, rasterised once for the viewer and the export alike),
/// transitions at the playhead's edit (Cmd+D, refused with the reason, edited and removed), speed, volume and fades from
/// the Inspector, fade handles, and images placed as 5 s stills.
/// </summary>
[Collection(nameof(SerialEditor))]
public sealed class VideoPolishTests : VideoEditorTestBase
{
    private static Clip ClipAt(TimelineTab tab, string track, int index) => tab.Timeline.ClipsOn(track)[index];

    private void Commit(string field, string value)
    {
        var input = W.VideoPanel.Document.QuerySelector($"input[data-field={field}]");
        Assert.False(input.IsNull, $"no {field} field");
        Assert.True(input.Focus());
        input.SetValue(value);
        input.Blur();
        _editor.Tick();
    }

    private void Select(TimelineTab tab, Clip clip)
    {
        tab.View.Selection.Clear();
        tab.View.SelectedTransition = null;
        tab.View.Selection.Add(clip.Id);
        _editor.Tick(2);
    }

    // The 10 s recording split at 5 s: A [0, 5) and B [5, 10), each with handles past the edit.
    private TimelineTab SplitTimeline()
    {
        var tab = NewTimeline(media: MediaFile("run.mp4"));
        tab.Seek(5);
        W.Commands.Execute("video.add_edit_all");
        return tab;
    }

    // ── Titles ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void PreviewAndExportShareRaster()
    {
        var tab = NewTimeline(media: MediaFile("run.mp4"));
        tab.Seek(1);
        _editor.Key(Key.T, Key.SuperLeft); // Add Title
        _editor.Tick(2);

        var title = Assert.Single(tab.Timeline.Clips, c => c.Kind == ClipKind.Title);
        Assert.Equal(V2(tab), title.TrackId); // above the picture, which it does not replace
        Assert.Single(tab.Timeline.ClipsOn(V1(tab)));
        Assert.Equal(1, title.Start, 6);
        Assert.Equal(3, TimelineLayout.Length(title, tab.Timeline.Rate), 6);
        Assert.Contains(title.Id, tab.View.Selection);

        Commit("tt_text", "Boss fight");
        title = Assert.Single(tab.Timeline.Clips, c => c.Kind == ClipKind.Title);
        Assert.Equal("Boss fight", title.Title!.Text);

        var raster = tab.Document.Resolve(title.Media);
        Assert.StartsWith(tab.Document.TitlesDirectory, raster, StringComparison.Ordinal);
        Assert.True(File.Exists(raster));
        // The viewer's decode and the export read the same PNG.
        Assert.Contains(raster, tab.Program.VideoArguments(0, 4));
        Assert.Contains(raster, TimelineGraphBuilder.Build(tab.Timeline, TimelineExportPresets.Vertical30, 0, 4, tab.Document.Resolve).InputArguments);

        Commit("tt_size", "120"); // a new style: a new raster
        var restyled = Assert.Single(tab.Timeline.Clips, c => c.Kind == ClipKind.Title);
        Assert.NotEqual(raster, tab.Document.Resolve(restyled.Media));
        Assert.Equal(2, tab.Document.History.Actions.Count(a => a.Name.StartsWith("Title", StringComparison.Ordinal)));
    }

    [Fact]
    public void ARasterHasItsBoxOutlineAndFill()
    {
        var style = TitleStyle.Default with { Text = "I", Size = 200, Outline = 8, OutlineColor = "#FF0000", Box = true, BoxColor = "#0000FF", BoxOpacity = 0.5 };
        var rgba = TitleRasterizer.Render(style, 400, 400);

        (byte R, byte G, byte B, byte A) Pixel(int x, int y)
        {
            var i = (y * 400 + x) * 4;
            return (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]);
        }

        Assert.Equal(0, Pixel(5, 5).A); // outside the box: clear
        var centre = Pixel(200, 200); // the "I"'s stem: white fill
        Assert.True(centre is { R: > 240, G: > 240, B: > 240, A: 255 }, $"centre {centre}");
        var red = Enumerable.Range(150, 100).Select(x => Pixel(x, 200)).Any(p => p is { R: > 200, G: < 60, B: < 60, A: > 200 });
        Assert.True(red, "no outline beside the stem");
        var box = Pixel(200, 200 - 115); // above the text, inside the padding: the half-opaque box
        Assert.True(box is { B: > 200, R: < 40 } && box.A is > 100 and < 160, $"box {box}");
        Assert.Equal(rgba, TitleRasterizer.Render(style, 400, 400)); // the same pixels every time
    }

    // ── Transitions ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CrossDissolveAtThePlayheadsEdit()
    {
        var tab = SplitTimeline();
        tab.Seek(5.2);
        _editor.Key(Key.D, Key.SuperLeft);
        _editor.Tick(2);

        Assert.Equal(2, tab.Timeline.Transitions.Length); // the picture's and its sound's
        var transition = tab.Timeline.FindTransition(tab.View.SelectedTransition!)!;
        Assert.Equal((TransitionKind.Dissolve, 1.0), (transition.Kind, transition.Duration));

        Commit("tr_duration", "2");
        Assert.All(tab.Timeline.Transitions, t => Assert.Equal(2, t.Duration, 6));
        Assert.NotNull(tab.View.SelectedTransition);

        Commit("tr_duration", "20"); // longer than the clips: refused, and said why
        Assert.All(tab.Timeline.Transitions, t => Assert.Equal(2, t.Duration, 6));

        _editor.Key(Key.Backspace); // Delete removes the selected transition, not a clip
        Assert.Empty(tab.Timeline.Transitions);
        Assert.Equal(2, tab.Timeline.ClipsOn(V1(tab)).Count);
    }

    [Fact]
    public void ADissolveWithoutHandlesIsRefusedWithTheReason()
    {
        var tab = NewTimeline(media: MediaFile("a.mp4"));
        var b = MediaOf(MediaFile("b.mp4"));
        tab.Document.Apply("Append", t => TimelineEdits.Append(t, b, 0, 10, V1(tab), null)); // B from its first frame
        tab.Seek(10);
        Select(tab, ClipAt(tab, V1(tab), 1));

        W.Commands.Execute("video.add_dissolve");

        Assert.Empty(tab.Timeline.Transitions);
        Assert.Equal(TimelineEdits.NotEnoughFrames, tab.Notice);
        W.Commands.Execute("video.add_dip"); // a dip needs no handles
        Assert.Single(tab.Timeline.Transitions);
    }

    [Fact]
    public void ClickingATransitionSelectsIt()
    {
        var tab = SplitTimeline();
        tab.Seek(5);
        W.Commands.Execute("video.add_dip");
        tab.View.SelectedTransition = null;
        var lane = tab.View.Lanes(tab.Timeline).First(l => l.TrackId == V1(tab));
        var p = new Vector2((float)tab.View.TimeToX(5), tab.View.LaneTop(lane) + lane.Height * 0.65f);

        tab.Controller.Press(p, EditorModifiers.None);
        tab.Controller.Release(p);

        Assert.Equal(tab.Timeline.Transitions.First(t => t.TrackId == V1(tab)).Id, tab.View.SelectedTransition);
        Assert.Empty(tab.View.Selection);
    }

    // ── Speed, volume, fades ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheInspectorSetsSpeedVolumeAndFades()
    {
        var tab = SplitTimeline();
        Select(tab, ClipAt(tab, V1(tab), 0));

        Commit("speed", "200");
        Assert.Equal(2.5, TimelineLayout.Length(ClipAt(tab, V1(tab), 0), tab.Timeline.Rate), 6);
        Assert.Equal(2.5, ClipAt(tab, V1(tab), 1).Start, 6); // B rippled

        Commit("fade_in", "0.5");
        Assert.Equal(0.5, ClipAt(tab, V1(tab), 0).FadeIn, 6);

        Select(tab, ClipAt(tab, A1(tab), 0));
        Commit("volume", "-6");
        Commit("fade_out", "1");
        var audio = ClipAt(tab, A1(tab), 0);
        Assert.Equal((-6.0, 1.0), (audio.VolumeDb, audio.FadeOut));
        Assert.Equal(0, ClipAt(tab, V1(tab), 0).FadeOut); // the picture keeps its own
    }

    [Fact]
    public void DraggingAFadeHandleSetsTheFadeAsOneStep()
    {
        var tab = NewTimeline(media: MediaFile("run.mp4"));
        tab.View.SetView(0, 100);
        _editor.Tick();
        var clip = ClipAt(tab, V1(tab), 0);
        var lane = tab.View.Lanes(tab.Timeline).First(l => l.TrackId == V1(tab));
        var top = tab.View.LaneTop(lane) + 4;
        var start = new Vector2((float)tab.View.TimeToX(0) + TimelineCanvas.FadeHandleSize / 2, top);
        var steps = tab.Document.History.Actions.Count;

        tab.Controller.Press(start, EditorModifiers.None);
        Assert.Equal(TimelineGesture.Fade, tab.Controller.Gesture);
        tab.Controller.Drag(start with { X = 50 }, EditorModifiers.None);
        tab.Controller.Drag(start with { X = 100 }, EditorModifiers.None);
        tab.Controller.Release(start with { X = 100 });

        Assert.Equal(1, tab.Timeline.FindClip(clip.Id)!.FadeIn, 6);
        Assert.Equal(steps + 1, tab.Document.History.Actions.Count);
    }

    // ── Images ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AnImageDroppedOnTheTimelineIsAFiveSecondStill()
    {
        var tab = NewTimeline();
        _editor.Tick(3);
        var png = Path.Combine(_outside, "logo.png");
        File.WriteAllText(png, "png");
        var rect = W.VideoPanel.TimelineRect;
        var lane = tab.View.Lanes(tab.Timeline).First(l => l.TrackId == V2(tab));
        var local = new Vector2((float)tab.View.TimeToX(1), tab.View.LaneTop(lane) + lane.Height / 2);

        W.DropOsFiles(new FilesDroppedEvent([png], new Vector2(rect.X, rect.Y) + local));
        TickUntil(() => tab.Timeline.ClipsOn(V2(tab)).Count == 1, "the image");

        var image = tab.Timeline.ClipsOn(V2(tab))[0];
        Assert.Equal(ClipKind.Image, image.Kind);
        Assert.Equal(5, TimelineLayout.Length(image, tab.Timeline.Rate), 6);
        Assert.Empty(tab.Timeline.ClipsOn(A1(tab))); // no sound
        Assert.Contains("-loop", tab.Program.VideoArguments(0, 6));

        Select(tab, image);
        W.Commands.Execute("video.detect_silence"); // the clip tools are for recordings
        Assert.Null(tab.SilenceJob);
        Assert.Contains("not on a title or an image", tab.Notice, StringComparison.Ordinal);
    }
}

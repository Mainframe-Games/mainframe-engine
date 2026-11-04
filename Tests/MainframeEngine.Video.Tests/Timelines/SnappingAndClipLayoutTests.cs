using MainframeEngine.Video.Timelines;
using static MainframeEngine.Video.Tests.Timelines.TimelineTestData;

namespace MainframeEngine.Video.Tests.Timelines;

public sealed class SnappingTests
{
    [Fact]
    public void EdgeSnapsToPlayhead()
    {
        var t = Append(Vertical(), 0, 4);
        var candidates = Snapping.Candidates(t, playhead: 6);
        Assert.Equal([0.0, 4.0, 6.0], candidates);
        Assert.Equal((6.0, true), Snapping.Snap(6.15, candidates, tolerance: 0.2));
        Assert.Equal((6.5, false), Snapping.Snap(6.5, candidates, tolerance: 0.2));
    }

    [Fact]
    public void DraggedClipsSnapByTheirNearerEdgeAndIgnoreThemselves()
    {
        var t = Append(Append(Vertical(), 0, 4), 10, 12);
        var dragged = On(t, V1(t), 1);
        var candidates = Snapping.Candidates(t, null, t.LinkedWith(dragged).Select(c => c.Id).ToHashSet()); // the clip and its sound
        Assert.DoesNotContain(6.0, candidates);
        Assert.Equal((4.0, true), Snapping.SnapSpan(4.1, 2, candidates, 0.2)); // head onto A's end
        Assert.Equal((2.0, true), Snapping.SnapSpan(1.9, 2, candidates, 0.2)); // tail onto 4
    }
}

public sealed class ClipLayoutTests
{
    [Fact]
    public void FillAndFitIntoVertical()
    {
        var fill = ClipLayout.Place(1920, 1080, ClipTransform.Default, 1080, 1920);
        Assert.Equal(1920, fill.Height, 6);
        Assert.Equal(1920 * 1920 / 1080.0, fill.Width, 6);
        Assert.Equal((1080 - fill.Width) / 2, fill.X, 6);
        var visible = ClipLayout.Visible(1920, 1080, ClipTransform.Default, 1080, 1920)!.Value;
        Assert.Equal(new VisiblePart(656, 0, 608, 1080, 0, 0, 1080, 1920), visible); // the centre 608 px, cropped

        var fit = ClipLayout.Place(1920, 1080, ClipTransform.Default with { Fit = FrameFit.Fit }, 1080, 1920);
        Assert.Equal(1080, fit.Width, 6);
        Assert.Equal(607.5, fit.Height, 6);
        Assert.Equal((1920 - 607.5) / 2, fit.Y, 6);
    }

    [Fact]
    public void StretchScalePositionAndCrop()
    {
        var stretch = ClipLayout.Place(1920, 1080, ClipTransform.Default with { Fit = FrameFit.Stretch }, 1080, 1920);
        Assert.Equal((0.0, 0.0, 1080.0, 1920.0), (stretch.X, stretch.Y, stretch.Width, stretch.Height));

        var pip = ClipTransform.Default with { Fit = FrameFit.Fit, Scale = 0.5, X = 200, Y = -300 };
        var p = ClipLayout.Place(1920, 1080, pip, 1080, 1920);
        Assert.Equal(540, p.Width, 6);
        Assert.Equal((1080 - 540) / 2.0 + 200, p.X, 6);

        var cropped = ClipLayout.Visible(1920, 1080, ClipTransform.Default with { Fit = FrameFit.Fit, Crop = new CropRect(480, 0, 480, 0) }, 1080, 1920)!.Value;
        Assert.Equal(480, cropped.SourceX);
        Assert.Equal(960, cropped.SourceWidth);
    }

    [Fact]
    public void AClipOutsideTheFrameIsNotVisible() =>
        Assert.Null(ClipLayout.Visible(1920, 1080, ClipTransform.Default with { X = 5000 }, 1080, 1920));
}

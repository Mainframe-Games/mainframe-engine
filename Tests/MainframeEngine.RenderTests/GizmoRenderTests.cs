using MainframeEngine.RenderTests.Host;
using MainframeEngine.RenderTests.Host.Scenes;

namespace MainframeEngine.RenderTests;

/// <summary>
/// The editor gizmo list (<see cref="GizmoDrawList"/>): lines keep their width in pixels with anti-aliased edges, solid
/// shapes draw, debug lines on a surface stay visible under TAA, and a frame that re-adds all of it allocates nothing.
/// </summary>
public class GizmoRenderTests
{
    private static string Output(string name) => Path.Combine(RenderTestEnvironment.ArtifactsDirectory, name);

    private static PngImage Capture(HostResult result, uint frame) => Png.ReadRgba8(result.Captures.Single(c => c.Frame == frame).Path);

    [Fact]
    public void LinesHaveTheirWidthInPixelsWithBlendedEdges()
    {
        var result = HostRunner.Run("gizmos", Output("gizmos"), "--capture", "10", "--hidden");
        Gates.AssertValidationClean(result);
        Gates.AssertMatchesGolden(result, 10);

        // A column through both lines (left of the cone): two runs of changed pixels, the red one above the blue one.
        var image = Capture(result, 10);
        var x = (int)(image.Width * 0.12f);
        var runs = Runs(image, x);
        TestContext.Current.SendDiagnosticMessage($"Column {x}: {string.Join(", ", runs.Select(r => $"y {r.Start}–{r.End} width {r.Width:0.00} edges {r.Partial}"))}");
        Assert.Equal(2, runs.Count);
        Assert.InRange(runs[0].Width, GizmoScene.ThinWidth - 1.2f, GizmoScene.ThinWidth + 1.2f);
        Assert.InRange(runs[1].Width, GizmoScene.ThickWidth - 1.2f, GizmoScene.ThickWidth + 1.2f);
        Assert.True(runs[0].Partial + runs[1].Partial >= 2, "the line edges are not blended (no partly covered pixels)");

        // The shaded cone and box: green and near-white pixels in the middle of the frame, in more than one shade each.
        var green = new HashSet<int>();
        var white = new HashSet<int>();
        for (var y = image.Height / 4; y < image.Height * 3 / 4; y++)
            for (var px = image.Width / 4; px < image.Width * 3 / 5; px++)
            {
                var (r, g, b) = Pixel(image, px, y);
                if (g > 120 && g > r + 40 && g > b + 60)
                    green.Add(g);
                else if (r > 110 && Math.Abs(r - g) < 12 && Math.Abs(r - b) < 12)
                    white.Add(r);
            }

        Assert.True(green.Count >= 3, $"the cone shows {green.Count} shades of green");
        Assert.True(white.Count >= 2, $"the box shows {white.Count} shades of grey");
    }

    [Fact]
    public void DebugLinesOnASurfaceStayVisibleUnderTaa()
    {
        // The yellow box outline lies exactly on the crate's faces; TAA jitters the scene depth every frame. Each frame shows
        // the visible edges, and consecutive frames show about as many of them (no z-fighting flicker).
        var result = HostRunner.Run("gizmos-taa", Output("gizmos-taa"), "--capture", "20,21,22", "--hidden");
        Gates.AssertValidationClean(result);
        uint[] frames = [20, 21, 22];
        var counts = frames.Select(frame => YellowPixels(Capture(result, frame))).ToArray();
        var image = Capture(result, 20);
        TestContext.Current.SendDiagnosticMessage($"Yellow pixels per frame: {string.Join(", ", counts)}");
        var expected = image.Height / 2; // nine edges about a sixth of the frame's height long
        Assert.All(counts, c => Assert.True(c >= expected, $"only {c} yellow pixels (expected at least {expected})"));
        Assert.True(counts.Max() - counts.Min() <= counts.Max() / 8, $"the outline flickers: {string.Join(", ", counts)}");
    }

    [Fact]
    public void ReAddingTheGizmosEveryFrameAllocatesNothing()
    {
        const int warmup = 60, measured = 200;
        var result = HostRunner.RunWithEnvironment(new Dictionary<string, string> { ["DOTNET_TieredCompilation"] = "0" },
            "gizmos", Output("gizmos-alloc"), "--alloc", $"{warmup}:{measured}", "--hidden");
        Assert.Equal(measured, result.MeasuredFrames);
        Assert.True(result.AllocatedBytes == 0, $"Steady-state frames allocated {result.AllocatedBytes} managed bytes over {measured} frames.");
        Gates.AssertValidationClean(result);
    }

    [Fact]
    public void TheAnalyticGridIsThinNearAndCalmTowardsTheHorizon()
    {
        var result = HostRunner.Run("grid-analytic", Output("grid-analytic"), "--capture", "5", "--hidden");
        Gates.AssertValidationClean(result);
        Gates.AssertMatchesGolden(result, 5);
        var image = Capture(result, 5);

        // Near the bottom the cells are wide: line pixels there belong to thin lines (the shorter run through a pixel across
        // the row or the column is a few pixels: 1–2.5 points plus the faded edge, √2 more on a diagonal; 90 % of them, as
        // where two lines cross both runs are long), and both axes show in the lower half.
        var background = Pixel(image, 1, 1);
        bool Line(int x, int y) => Difference(Pixel(image, x, y), background) >= 18;
        var (thickness, red, blue) = (new List<int>(), false, false);
        for (var y = image.Height * 3 / 4; y < image.Height - 1; y++)
            for (var x = 1; x < image.Width - 1; x++)
            {
                if (!Line(x, y))
                    continue;
                int across = 1, down = 1;
                for (var k = x - 1; k >= 0 && Line(k, y); k--) across++;
                for (var k = x + 1; k < image.Width && Line(k, y); k++) across++;
                for (var k = y - 1; k >= 0 && Line(x, k); k--) down++;
                for (var k = y + 1; k < image.Height && Line(x, k); k++) down++;
                thickness.Add(Math.Min(across, down));
            }

        for (var y = image.Height / 2; y < image.Height; y++)
            for (var x = 0; x < image.Width; x++)
            {
                var (r, g, b) = Pixel(image, x, y);
                red |= r > g + 60 && r > b + 60;
                blue |= b > r + 60 && b > g + 30;
            }

        thickness.Sort();
        var typical = thickness.Count > 0 ? thickness[thickness.Count * 9 / 10] : 0;
        TestContext.Current.SendDiagnosticMessage($"Bottom quarter: {thickness.Count} line pixels, 90 % at most {typical} px thick, axes {red}/{blue}");
        Assert.True(thickness.Count > image.Width / 2, $"only {thickness.Count} line pixels near the camera");
        Assert.True(typical <= 8 * image.Width / 320, $"lines {typical} px thick near the camera");
        Assert.True(red && blue, "an axis is missing");

        // Towards the horizon the cells shrink to a few pixels: the levels step up and the lines fade instead of turning
        // into moiré, so along a row just below the horizon neighbouring pixels barely differ.
        var top = image.Height * 2 / 5;
        var (change, count) = (0f, 0);
        for (var y = top; y < top + image.Height / 20; y++)
            for (var x = 0; x + 1 < image.Width; x++)
            {
                var (r0, g0, b0) = Pixel(image, x, y);
                var (r1, g1, b1) = Pixel(image, x + 1, y);
                change += MathF.Abs((r0 + g0 + b0 - r1 - g1 - b1) / 3f);
                count++;
            }

        change /= count;
        TestContext.Current.SendDiagnosticMessage($"Horizon band: {change:0.00} mean change between neighbouring pixels");
        Assert.True(change < 4f, $"neighbouring pixels below the horizon differ by {change:0.00} on average (moiré)");
    }

    [Fact]
    public void TheAnalyticGridAllocatesNothing()
    {
        const int warmup = 30, measured = 120;
        var result = HostRunner.RunWithEnvironment(new Dictionary<string, string> { ["DOTNET_TieredCompilation"] = "0" },
            "grid-analytic", Output("grid-analytic-alloc"), "--alloc", $"{warmup}:{measured}", "--hidden");
        Assert.Equal(measured, result.MeasuredFrames);
        Assert.True(result.AllocatedBytes == 0, $"Steady-state frames allocated {result.AllocatedBytes} managed bytes.");
        Gates.AssertValidationClean(result);
    }

    private static int YellowPixels(PngImage image)
    {
        var count = 0;
        for (var y = 0; y < image.Height; y++)
            for (var x = image.Width * 3 / 5; x < image.Width; x++)
            {
                var (r, g, b) = Pixel(image, x, y);
                if (r > 150 && g > 120 && b < r - 70)
                    count++;
            }

        return count;
    }

    // Runs of pixels down column x that differ from the background (the top pixel): the coverage-weighted width
    // (Σ difference / the run's peak) and the number of partly covered pixels.
    private static List<(int Start, int End, float Width, int Partial)> Runs(PngImage image, int x)
    {
        var background = Pixel(image, x, 1);
        var runs = new List<(int, int, float, int)>();
        var y = 0;
        while (y < image.Height)
        {
            if (Difference(Pixel(image, x, y), background) < 12)
            {
                y++;
                continue;
            }

            var start = y;
            var sum = 0f;
            var peak = 0f;
            while (y < image.Height && Difference(Pixel(image, x, y), background) >= 12)
            {
                var d = Difference(Pixel(image, x, y), background);
                sum += d;
                peak = MathF.Max(peak, d);
                y++;
            }

            var partial = 0;
            for (var k = start; k < y; k++)
                if (Difference(Pixel(image, x, k), background) < peak * 0.85f)
                    partial++;
            runs.Add((start, y - 1, sum / peak, partial));
        }

        return runs;
    }

    private static float Difference((int R, int G, int B) a, (int R, int G, int B) b) =>
        Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);

    private static (int R, int G, int B) Pixel(PngImage image, int x, int y)
    {
        var i = (y * image.Width + x) * 4;
        return (image.Pixels[i], image.Pixels[i + 1], image.Pixels[i + 2]);
    }
}

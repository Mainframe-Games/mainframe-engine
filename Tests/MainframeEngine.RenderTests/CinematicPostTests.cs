using System.Numerics;
using MainframeEngine.RenderTests.Host;
using MainframeEngine.RenderTests.Host.Scenes;

namespace MainframeEngine.RenderTests;

/// <summary>
/// ADR 0168, the cinematic post chain: the colour grade (adjustments, <c>.cube</c> LUTs), the Godot 4.4 tonemappers, the
/// high-quality glow, bokeh depth of field and the film effects. Every run is validation-clean; most checks are against
/// the frame's own math (the colour chart's patches are known values), a few frames also match goldens.
/// </summary>
public class CinematicPostTests
{
    private static string Output(string name) => Path.Combine(RenderTestEnvironment.ArtifactsDirectory, name);

    private static PngImage Capture(HostResult result, uint frame) => Png.ReadRgba8(result.Captures.Single(c => c.Frame == frame).Path);

    private static HostResult Grade(int count, uint frame = 3)
    {
        var result = HostRunner.Run("post-grade", Output($"post-grade-{count}"), "--capture", frame.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--count", count.ToString(System.Globalization.CultureInfo.InvariantCulture), "--hidden");
        Gates.AssertValidationClean(result);
        return result;
    }

    [Fact]
    public void AnIdentityLutLeavesTheImageUnchanged()
    {
        var plain = Capture(Grade(0), 3);
        var graded = Capture(Grade(1), 3);
        var worst = 0;
        for (var i = 0; i < plain.Pixels.Length; i += 4)
            for (var c = 0; c < 3; c++)
                worst = Math.Max(worst, Math.Abs(plain.Pixels[i + c] - graded.Pixels[i + c]));
        TestContext.Current.SendDiagnosticMessage($"Identity LUT: largest channel difference {worst}");
        Assert.True(worst <= 1, $"an identity LUT changed a channel by {worst}");

        // The baseline (linear tonemapper, exposure 1) shows every patch's albedo exactly.
        ForEachPatch(plain, (albedo, actual) => AssertNear(albedo, actual, 1, "baseline"));
    }

    [Fact]
    public void AKnownLutGivesTheExpectedColours()
    {
        var plain = Capture(Grade(0), 3);
        var rotated = Capture(Grade(2), 3);
        var half = Capture(Grade(4), 3);
        ForEachPatch(plain, rotated, (input, actual) => AssertNear(PostGradeScene.Rotate(input), actual, 2, "rotation LUT"));
        ForEachPatch(plain, half, (input, actual) => AssertNear(Vector3.Lerp(input, PostGradeScene.Rotate(input), 0.5f), actual, 2, "rotation LUT at strength 0.5"));
    }

    [Fact]
    public void AdjustmentsFollowGodotsFormula()
    {
        var plain = Capture(Grade(0), 3);
        var adjusted = Capture(Grade(3), 3);
        ForEachPatch(plain, adjusted, (input, actual) =>
        {
            // Godot 4's apply_bcs on the display value.
            var c = input / 255f * PostGradeScene.Brightness;
            c = Vector3.Lerp(new Vector3(0.5f), c, PostGradeScene.Contrast);
            c = Vector3.Lerp(new Vector3((c.X + c.Y + c.Z) * 0.33333f), c, PostGradeScene.Saturation);
            AssertNear(Vector3.Clamp(c, Vector3.Zero, Vector3.One) * 255f, actual, 2, "adjustments");
        });
    }

    [Theory]
    [InlineData(Tonemapper.Engine)]
    [InlineData(Tonemapper.GodotAces)]
    [InlineData(Tonemapper.Linear)]
    [InlineData(Tonemapper.Reinhard)]
    [InlineData(Tonemapper.Filmic)]
    [InlineData(Tonemapper.Agx)]
    public void TonemappersMatchTheirCpuCurves(Tonemapper tonemapper)
    {
        var count = 10 + (int)tonemapper;
        var frame = 4u;
        var result = Grade(count, frame);
        var settings = PostGradeScene.Settings(count);
        ForEachPatch(Capture(result, frame), (albedo, actual) =>
        {
            var hdr = ColorSpace.SrgbToLinear(albedo / 255f) * PostGradeScene.TonemapExposure;
            var display = Vector3.Clamp(TonemapCurves.Apply(settings, hdr), Vector3.Zero, Vector3.One);
            AssertNear(ColorSpace.LinearToSrgb(display) * 255f, actual, 2, tonemapper.ToString());
        });
        if (tonemapper == Tonemapper.Agx)
            Gates.AssertMatchesGolden(result, frame);
    }

    [Fact]
    public void FarDepthOfFieldBlursTheWallButNotTheSubject()
    {
        var on = HostRunner.Run("post-dof", Output("post-dof-far"), "--capture", "5", "--count", "0", "--hidden");
        var off = HostRunner.Run("post-dof", Output("post-dof-off"), "--capture", "5", "--count", "1", "--hidden");
        Gates.AssertValidationClean(on);
        Gates.AssertValidationClean(off);
        Gates.AssertMatchesGolden(on, 5);
        var blurred = Capture(on, 5);
        var sharp = Capture(off, 5);
        var (wall, wallOff) = (Contrast(blurred, WallRegion(blurred)), Contrast(sharp, WallRegion(sharp)));
        var subject = MeanDifference(blurred, sharp, SubjectRegion(sharp));
        TestContext.Current.SendDiagnosticMessage($"Far DoF: wall contrast {wall:F1} (sharp {wallOff:F1}); subject differs by {subject:F2}");
        Assert.True(wall < 0.6f * wallOff, $"the far wall is not blurred (contrast {wall} vs {wallOff})");
        Assert.True(subject <= 1f, $"the in-focus box changed (mean difference {subject})");
    }

    [Fact]
    public void NearDepthOfFieldBlursTheSubjectButNotTheWall()
    {
        var on = HostRunner.Run("post-dof", Output("post-dof-near"), "--capture", "6", "--count", "2", "--hidden");
        var off = HostRunner.Run("post-dof", Output("post-dof-near-off"), "--capture", "6", "--count", "1", "--hidden");
        Gates.AssertValidationClean(on);
        Gates.AssertValidationClean(off);
        Gates.AssertMatchesGolden(on, 6);
        var blurred = Capture(on, 6);
        var sharp = Capture(off, 6);
        var wall = MeanDifference(blurred, sharp, WallRegion(sharp));
        var edge = EdgeSharpness(blurred, sharp);
        TestContext.Current.SendDiagnosticMessage($"Near DoF: wall differs by {wall:F2}; the box's edge keeps {edge:P0} of its contrast");
        Assert.True(wall <= 1f, $"the in-focus wall changed (mean difference {wall})");
        Assert.True(edge < 0.7f, $"the near box's edge is not blurred ({edge:P0} of its contrast)");
    }

    [Fact]
    public void VignetteDarkensTowardsTheCorners()
    {
        var plain = HostRunner.Run("post-film", Output("post-film-plain"), "--capture", "5", "--count", "0", "--hidden");
        var vignette = HostRunner.Run("post-film", Output("post-film-vignette"), "--capture", "5", "--count", "1", "--hidden");
        Gates.AssertValidationClean(plain);
        Gates.AssertValidationClean(vignette);
        var a = Capture(plain, 5);
        var b = Capture(vignette, 5);
        int w = b.Width, h = b.Height;
        Assert.Equal(PostFilmScene.Grey, Red(a, w / 4, h / 2));

        // Along the diagonal to the top-left corner (away from the bars): what the shader's formula gives, and darker
        // outwards. A round vignette: the corner is r² = 1 in aspect-corrected units.
        var last = 256f;
        for (var step = 0; step <= 10; step++)
        {
            var x = (int)((w / 2 - 1) * (1f - step / 10f));
            var y = (int)((h / 2 - 1) * (1f - step / 10f));
            var uv = new Vector2((x + 0.5f) / w, (y + 0.5f) / h);
            var aspect = (float)w / h;
            var d = (uv - new Vector2(0.5f)) * new Vector2(aspect, 1f);
            var r2 = d.LengthSquared() / (0.25f * aspect * aspect + 0.25f);
            var factor = Math.Clamp(1f - PostFilmScene.Vignette * r2 * MathF.Sqrt(r2), 0f, 1f);
            var expected = ColorSpace.LinearToSrgb(ColorSpace.SrgbToLinear(PostFilmScene.Grey / 255f) * factor) * 255f;
            var actual = Red(b, x, y);
            Assert.InRange(actual, expected - 2f, expected + 2f);
            Assert.True(actual <= last, $"the vignette is not darker outwards at ({x}, {y})");
            last = actual;
        }
    }

    [Fact]
    public void FilmGrainIsSubtleTemporalAndDeterministic()
    {
        var first = HostRunner.Run("post-film", Output("post-film-grain"), "--capture", "5,6", "--count", "2", "--hidden");
        var again = HostRunner.Run("post-film", Output("post-film-grain-again"), "--capture", "5", "--count", "2", "--hidden");
        Gates.AssertValidationClean(first);
        var f5 = Capture(first, 5);
        var f6 = Capture(first, 6);
        var (mean, deviation) = Statistics(f5);
        var changed = 0;
        var total = 0;
        for (var y = 0; y < f5.Height; y++)
            for (var x = 0; x < f5.Width / 2; x++, total++)
                if (Red(f5, x, y) != Red(f6, x, y))
                    changed++;
        TestContext.Current.SendDiagnosticMessage($"Grain: mean {mean:F2}, deviation {deviation:F2}, {100f * changed / total:F0} % of pixels change between frames");
        Assert.InRange(mean, PostFilmScene.Grey - 1f, PostFilmScene.Grey + 1f);
        Assert.InRange(deviation, 1f, PostFilmScene.Grain * 255f);
        Assert.True(changed > total / 2, "the grain does not change from frame to frame");
        Assert.Equal(f5.Pixels, Capture(again, 5).Pixels); // the same frame is the same grain on every run
    }

    [Fact]
    public void ChromaticAberrationSplitsRedAndBlueTowardsTheEdges()
    {
        var plain = HostRunner.Run("post-film", Output("post-film-plain-ca"), "--capture", "8", "--count", "0", "--hidden");
        var split = HostRunner.Run("post-film", Output("post-film-aberration"), "--capture", "8", "--count", "3", "--hidden");
        Gates.AssertValidationClean(split);
        Gates.AssertMatchesGolden(split, 8);
        Assert.Equal(0, Fringed(Capture(plain, 8)));
        var image = Capture(split, 8);
        var fringed = Fringed(image);
        TestContext.Current.SendDiagnosticMessage($"Aberration: {fringed} fringed pixels");
        Assert.True(fringed > image.Height * 8, $"only {fringed} pixels show red/blue fringes");
        Assert.Equal(PostFilmScene.Grey, Red(image, image.Width / 4, image.Height / 2)); // flat grey stays grey

        static int Fringed(PngImage image)
        {
            var n = 0;
            for (var i = 0; i < image.Pixels.Length; i += 4)
                if (Math.Abs(image.Pixels[i] - image.Pixels[i + 2]) > 30)
                    n++;
            return n;
        }
    }

    [Fact]
    public void HighQualityGlowBleedsAroundBrightAreasOnlyAboveTheThreshold()
    {
        var on = HostRunner.Run("glow", Output("glow-high"), "--capture", "7", "--count", "3", "--hidden");
        var off = HostRunner.Run("glow", Output("glow-high-off"), "--capture", "7", "--count", "1", "--hidden");
        var dim = HostRunner.Run("glow", Output("glow-high-dim"), "--capture", "7", "--count", "4", "--hidden");
        Gates.AssertValidationClean(on);
        Gates.AssertValidationClean(dim);
        Gates.AssertMatchesGolden(on, 7);
        var glow = Capture(on, 7);
        var plain = Capture(off, 7);
        int w = glow.Width, h = glow.Height;
        var near = (w / 2 + h / 10, h / 2);
        var far = (w / 20, h / 20);
        var nearGain = Luminance(glow, near) - Luminance(plain, near);
        var farDiff = MathF.Abs(Luminance(glow, far) - Luminance(plain, far));
        TestContext.Current.SendDiagnosticMessage($"High glow gain next to the emitter {nearGain:F1}, far away {farDiff:F1}");
        Assert.True(nearGain > 8f, $"no glow next to the emitter (gain {nearGain})");
        Assert.True(farDiff <= 2f, $"glow reached the far corner ({farDiff})");
        Assert.True(MathF.Abs(Luminance(Capture(dim, 7), near) - Luminance(plain, near)) <= 2f, "glow below the threshold changed the image");
    }

    [Fact]
    public void TheCinematicChainAllocatesNothingPerFrame()
    {
        // Far and near depth of field, AgX, high-quality glow, adjustments, a LUT, vignette, grain, aberration and FXAA,
        // with the camera turning.
        const int warmup = 60, measured = 240;
        var result = HostRunner.Run("post-dof", Output("post-cinematic-alloc"), "--count", "3", "--alloc", $"{warmup}:{measured}", "--hidden");
        Assert.Equal(measured, result.MeasuredFrames);
        Assert.True(result.AllocatedBytes == 0, $"The cinematic post chain allocated {result.AllocatedBytes} managed bytes over {measured} frames.");
        Gates.AssertValidationClean(result);
    }

    [Fact]
    public void TheCinematicChainSurvivesAResize()
    {
        var result = HostRunner.Run("post-dof", Output("post-cinematic-resize"), "--count", "3", "--resize", "400x300@10", "--capture", "5,30", "--hidden");
        Gates.AssertValidationClean(result);
        var after = Capture(result, 30);
        Assert.Equal((int)(400 * result.ContentScale), after.Width);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static void ForEachPatch(PngImage image, Action<Vector3, Vector3> check)
    {
        for (var row = 0; row < ColorChart.Rows; row++)
            for (var column = 0; column < ColorChart.Columns; column++)
            {
                var a = ColorChart.Albedo[row * ColorChart.Columns + column];
                check(new Vector3(a[0], a[1], a[2]), PatchColor(image, column, row));
            }
    }

    private static void ForEachPatch(PngImage input, PngImage output, Action<Vector3, Vector3> check)
    {
        for (var row = 0; row < ColorChart.Rows; row++)
            for (var column = 0; column < ColorChart.Columns; column++)
                check(PatchColor(input, column, row), PatchColor(output, column, row));
    }

    private static Vector3 PatchColor(PngImage image, int column, int row)
    {
        var (x, y) = ColorChart.Center(column, row, image.Width, image.Height);
        var i = (y * image.Width + x) * 4;
        return new Vector3(image.Pixels[i], image.Pixels[i + 1], image.Pixels[i + 2]);
    }

    private static void AssertNear(Vector3 expected, Vector3 actual, float tolerance, string what)
    {
        var d = Vector3.Abs(expected - actual);
        Assert.True(d.X <= tolerance && d.Y <= tolerance && d.Z <= tolerance, $"{what}: expected {expected}, got {actual}");
    }

    private static byte Red(PngImage image, int x, int y) => image.Pixels[(y * image.Width + x) * 4];

    private static float Luminance(PngImage image, (int X, int Y) p)
    {
        var i = (p.Y * image.Width + p.X) * 4;
        return 0.2126f * image.Pixels[i] + 0.7152f * image.Pixels[i + 1] + 0.0722f * image.Pixels[i + 2];
    }

    // The wall away from the box (top-left), and the box's middle.
    private static (int X0, int Y0, int X1, int Y1) WallRegion(PngImage image) =>
        (image.Width / 32, image.Height / 24, image.Width / 4, image.Height / 4);

    private static (int X0, int Y0, int X1, int Y1) SubjectRegion(PngImage image) =>
        (image.Width / 2 - image.Width / 32, image.Height / 2 - image.Height / 24, image.Width / 2 + image.Width / 32, image.Height / 2 + image.Height / 24);

    // Mean absolute red difference between horizontal neighbours (the stripes are vertical).
    private static float Contrast(PngImage image, (int X0, int Y0, int X1, int Y1) r)
    {
        double sum = 0;
        var n = 0;
        for (var y = r.Y0; y < r.Y1; y++)
            for (var x = r.X0; x < r.X1; x++, n++)
                sum += Math.Abs(Red(image, x + 1, y) - Red(image, x, y));
        return (float)(sum / n);
    }

    private static float MeanDifference(PngImage a, PngImage b, (int X0, int Y0, int X1, int Y1) r)
    {
        double sum = 0;
        var n = 0;
        for (var y = r.Y0; y < r.Y1; y++)
            for (var x = r.X0; x < r.X1; x++, n++)
                for (var c = 0; c < 3; c++)
                    sum += Math.Abs(a.Pixels[(y * a.Width + x) * 4 + c] - b.Pixels[(y * b.Width + x) * 4 + c]) / 3.0;
        return (float)(sum / n);
    }

    // The box's silhouette contrast in the blurred image as a fraction of the sharp one's: along the middle row, the
    // largest step in green (the box is orange, the stripes grey) within a few pixels of each of its sharp edges.
    private static float EdgeSharpness(PngImage blurred, PngImage sharp)
    {
        var y = sharp.Height / 2;
        float sharpSum = 0, blurredSum = 0;
        for (var x = 1; x < sharp.Width - 1; x++)
        {
            var g = Math.Abs(sharp.Pixels[(y * sharp.Width + x + 1) * 4 + 2] - sharp.Pixels[(y * sharp.Width + x) * 4 + 2]);
            if (g < 60 || !IsOrange(sharp, x, y) && !IsOrange(sharp, x + 1, y))
                continue;
            sharpSum += g;
            blurredSum += Math.Abs(blurred.Pixels[(y * blurred.Width + x + 1) * 4 + 2] - blurred.Pixels[(y * blurred.Width + x) * 4 + 2]);
        }

        return sharpSum > 0 ? blurredSum / sharpSum : 1f;

        static bool IsOrange(PngImage image, int x, int y)
        {
            var i = (y * image.Width + x) * 4;
            return image.Pixels[i] > 150 && image.Pixels[i + 2] < 90;
        }
    }

    // Mean and standard deviation of red over the left half (the uniform grey, away from the bars).
    private static (float Mean, float Deviation) Statistics(PngImage image)
    {
        double sum = 0, squares = 0;
        var n = 0;
        for (var y = 0; y < image.Height; y++)
            for (var x = 0; x < image.Width / 2; x++, n++)
            {
                double v = Red(image, x, y);
                sum += v;
                squares += v * v;
            }

        var mean = sum / n;
        return ((float)mean, (float)Math.Sqrt(Math.Max(squares / n - mean * mean, 0)));
    }
}

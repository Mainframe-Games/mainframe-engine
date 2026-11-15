using MainframeEngine.Cli.ContactSheet;
using MainframeEngine.Tests.Scene;

namespace MainframeEngine.Tests.Animation.Authoring;

/// <summary>G1.17: contact sheet frames, geometry, CPU drawing and composition, without a GPU.</summary>
[Collection(nameof(SerialResources))]
public class ContactSheetLayoutTests
{
    private static readonly SheetView[] AllViews = [SheetView.Front, SheetView.Side, SheetView.ThreeQuarter];

    private static ClipDescription Keys(string json)
    {
        var (root, skeleton, rig) = PoseBakerTests.Character();
        var clip = PoseBaker.Bake(PoseScriptParser.Parse(json), rig, root.GetPathTo(skeleton)).Animation;
        return ClipDescriber.Describe(clip, rig, root.GetPathTo(skeleton));
    }

    [Fact]
    public void FramesDefaultToTheTimesEveryTrackIsKeyed()
    {
        var frames = ContactSheetLayout.ChooseFrames(Keys(PoseScriptParserTests.Jump), null, null);

        // The jump's refinement keys (on the legs only) are not frames.
        Assert.Equal([0f, 0.12f, 0.24f, 0.55f, 0.82f, 1.1f], frames.Select(f => f.Time));
        Assert.All(frames, f => Assert.True(f.IsKey));
    }

    [Fact]
    public void FramesCanBeSpacedOrSingle()
    {
        var keys = Keys(AnimationChecksTests.Squat);

        var spaced = ContactSheetLayout.ChooseFrames(keys, 5, null);
        Assert.Equal([0f, 0.4f, 0.8f, 1.2f, 1.6f], spaced.Select(f => f.Time));
        Assert.Equal([true, false, true, false, true], spaced.Select(f => f.IsKey));
        Assert.Equal("0.4 s ·", spaced[1].Label);

        var single = Assert.Single(ContactSheetLayout.ChooseFrames(keys, null, 0.8f));
        Assert.True(single.IsKey);
        Assert.Equal(1.6f, Assert.Single(ContactSheetLayout.ChooseFrames(keys, null, 9f)).Time);
    }

    [Fact]
    public void ManyKeysAreThinnedToTwelveKeepingTheEnds()
    {
        var keys = string.Join(", ", Enumerable.Range(1, 19).Select(i =>
            $$"""{ "t": {{(i * 0.1f).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}}, "joints": { "Spine": { "flex": {{i % 2 * 10}} } } }"""));
        var json = $$"""
            { "model": "m.glb", "library": "l.mfres", "name": "wiggle", "length": 2,
              "keys": [ { "t": 0, "pose": "stand" }, {{keys}}, { "t": 2, "pose": "stand" } ] }
            """;

        var frames = ContactSheetLayout.ChooseFrames(Keys(json), null, null);

        Assert.Equal(ContactSheetLayout.MaxFrames, frames.Count);
        Assert.Equal(0f, frames[0].Time);
        Assert.Equal(2f, frames[^1].Time);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(12)]
    public void CellsFitTheSheetWithoutOverlapping(int count)
    {
        var frames = Enumerable.Range(0, count).Select(i => new SheetFrame(i * 0.1f, true)).ToList();

        var layout = ContactSheetLayout.Create(AllViews, frames, 1600, reportLines: 5);

        var cells = layout.Cells().Select(c => layout.Cell(c.View, c.Frame)).ToList();
        Assert.Equal(3 * count, cells.Count);
        foreach (var cell in cells)
        {
            Assert.InRange(cell.X + cell.Width, 0, layout.Width);
            Assert.True(cell.Y + cell.Height < layout.Strip.Y);
            Assert.Equal(layout.CellHeight, cell.Height);
        }

        for (var i = 0; i < cells.Count; i++)
            for (var j = i + 1; j < cells.Count; j++)
                Assert.False(cells[i].X < cells[j].X + cells[j].Width && cells[j].X < cells[i].X + cells[i].Width &&
                             cells[i].Y < cells[j].Y + cells[j].Height && cells[j].Y < cells[i].Y + cells[i].Height);
        Assert.True(layout.Report.Y > layout.Strip.Y + layout.Strip.Height);
        Assert.Equal(layout.Report.Y + layout.Report.Height + ContactSheetLayout.Margin, layout.Height);
        Assert.InRange(layout.RenderSize.Height, 64, 1024);
        Assert.Equal(count == 1, layout.Single);
    }

    [Fact]
    public void OneFramePutsTheViewsSideBySide()
    {
        var layout = ContactSheetLayout.Create(AllViews, [new SheetFrame(0.5f, true)], 1600, 1);

        Assert.Equal(layout.Cell(0, 0).Y, layout.Cell(2, 0).Y);
        Assert.True(layout.Cell(1, 0).X > layout.Cell(0, 0).X);
    }

    [Fact]
    public void ViewsParseByName()
    {
        Assert.True(ContactSheetLayout.TryParse("threequarter", out var view));
        Assert.Equal(SheetView.ThreeQuarter, view);
        Assert.True(ContactSheetLayout.TryParse("three-quarter", out _));
        Assert.False(ContactSheetLayout.TryParse("top", out _));
        Assert.Equal("side", ContactSheetLayout.Name(SheetView.Side));
    }

    [Fact]
    public void TheCanvasFillsBlitsWithABoxFilterAndDrawsText()
    {
        var canvas = new SheetCanvas(40, 20, new Rgba(0, 0, 0));
        canvas.Fill(0, 0, 5, 5, new Rgba(255, 0, 0));
        Assert.Equal(new Rgba(255, 0, 0), canvas[2, 2]);
        Assert.Equal(new Rgba(0, 0, 0), canvas[6, 2]);

        // A 2×2 checker of black and white averages to grey at half size.
        byte[] checker = [0, 0, 0, 255, 255, 255, 255, 255, 255, 255, 255, 255, 0, 0, 0, 255];
        canvas.Blit(checker, 2, 2, 10, 10, 1, 1);
        Assert.Equal(new Rgba(127, 127, 127), canvas[10, 10]);

        var font = Font.FromData(ContentFiles.ReadAllBytes(ContentPaths.Resolve("Assets/UI/fonts/LatoLatin-Regular.ttf")));
        var width = canvas.Text(font, "Hi", 14, 20, 0, new Rgba(255, 255, 255));
        Assert.True(width > 0);
        var lit = 0;
        for (var y = 0; y < 20; y++)
            for (var x = 20; x < 40; x++)
                if (canvas[x, y].R > 128)
                    lit++;
        Assert.True(lit > 10, $"text drew {lit} pixels");
    }

    [Fact]
    public void ACompositionPlacesCellsHeaderStripAndReport()
    {
        var frames = new[] { new SheetFrame(0f, true), new SheetFrame(0.5f, false) };
        var layout = ContactSheetLayout.Create([SheetView.Front, SheetView.Side], frames, 800, 2);
        var red = new byte[8 * 8 * 4];
        for (var i = 0; i < red.Length; i += 4)
            (red[i], red[i + 3]) = (230, 255);
        var cells = new Dictionary<(int, int), FrameCapture> { [(0, 0)] = new FrameCapture(8, 8, red) };
        var content = new ContactSheetContent
        {
            Title = "squat",
            Subtitle = "1 s",
            Length = 1f,
            Strip = [new StripSample(0f, 0.9f, 0f, 0f, 1f, 1f, true, true), new StripSample(1f, 0.8f, 0.1f, 0.1f, 1.2f, 1.2f, false, false)],
            Report = ["Checks: 1 error(s), 0 warning(s)", "  error ground t = 0.5 s: LeftToes goes 2 cm below the ground."],
            Cells = cells,
        };
        var font = Font.FromData(ContentFiles.ReadAllBytes(ContentPaths.Resolve("Assets/UI/fonts/LatoLatin-Regular.ttf")));

        var canvas = ContactSheetComposer.Compose(layout, content, font, font);

        Assert.Equal((layout.Width, layout.Height), (canvas.Width, canvas.Height));
        var filled = layout.Cell(0, 0);
        Assert.Equal(new Rgba(230, 0, 0), canvas[filled.X + filled.Width / 2, filled.Y + filled.Height / 2]);
        var missing = layout.Cell(1, 1);
        Assert.Equal(ContactSheetComposer.Panel, canvas[missing.X + 5, missing.Y + missing.Height - 20]);
        // The left foot's contact band at the start of the strip, the report's error line in red.
        Assert.Contains(Enumerable.Range(layout.Strip.Y, layout.Strip.Height).Select(y => canvas[layout.Strip.X + 60, y]),
            c => c == ContactSheetComposer.LeftFootColour);
        Assert.Contains(Enumerable.Range(0, layout.Report.Width).SelectMany(x =>
                Enumerable.Range(layout.Report.Y + ContactSheetLayout.ReportLineHeight, ContactSheetLayout.ReportLineHeight).Select(y => canvas[layout.Report.X + x, y])),
            c => c.R > 200 && c.G < 140);
    }
}

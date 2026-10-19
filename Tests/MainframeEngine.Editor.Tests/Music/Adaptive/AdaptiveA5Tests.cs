using System.Numerics;
using MainframeEngine.Audio.Music;
using MainframeEngine.Editor.Music;
using static MainframeEngine.Editor.Tests.Music.MusicTestUtil;

namespace MainframeEngine.Editor.Tests.Music.Adaptive;

/// <summary>
/// The Curves pane (docs/design/future/adaptive-music.md, "Curve editor"): the mapping between the canvas and a curve, point
/// drags (Shift: one axis), double-click to add or remove, step curves over labels; each gesture one history entry.
/// </summary>
[Collection(nameof(SerialEditor))]
public sealed class CurvesPaneTests : IDisposable
{
    private readonly SessionHost _host = new();
    private readonly SongTab _tab;

    public CurvesPaneTests()
    {
        var path = SongTab.CreateFile(Path.Combine(_host.ProjectDirectory, "Assets", "Music", "Theme.msong"));
        _tab = _host.Session.OpenSong(path);
        SongAdaptiveDemo.Build(Doc);
        _tab.SelectCurve(Adaptive.Curves[1]); // Combat's volume over intensity: (0, −80) (0.5, −80) (0.8, 0) (1, 0)
        Curves.Size = new Vector2(852, 350); // a plot 784 × 300 dp
    }

    public void Dispose() => _host.Dispose();

    private SongDocument Doc => _tab.Document;
    private SongAdaptive Adaptive => Doc.Song.Adaptive!;
    private SongCurves Curves => _tab.Curves;
    private MusicCurveInfo Curve => Curves.Curve!;
    private MusicParameterInfo Intensity => Curves.Parameter!;
    private int Entries => Doc.History.Actions.Count;

    private Vector2 At(float x, float y) => new(Curves.X(Intensity, x), Curves.Y(MusicCurveTarget.Volume, y));

    [Fact]
    public void TheMappingRoundTrips()
    {
        Assert.Equal(SongCurves.Left, Curves.X(Intensity, 0));
        Assert.Equal(SongCurves.Left + Curves.PlotWidth, Curves.X(Intensity, 1), 3);
        Assert.Equal(0.25f, Curves.ValueAt(Intensity, Curves.X(Intensity, 0.25f)), 3);
        Assert.Equal(-24f, Curves.TargetAt(MusicCurveTarget.Volume, Curves.Y(MusicCurveTarget.Volume, -24)), 1);
        Assert.Equal(1000f, Curves.TargetAt(MusicCurveTarget.Cutoff, Curves.Y(MusicCurveTarget.Cutoff, 1000)), 0);
        Assert.Equal(2, Curves.PointAt(At(0.8f, 0)));
        Assert.Equal(-1, Curves.PointAt(At(0.3f, -40)));
    }

    [Fact]
    public void DraggingAPointIsOneEntryAndShiftKeepsAnAxis()
    {
        var entries = Entries;
        Curves.Press(At(0.8f, 0), EditorModifiers.None, doubleClick: false);
        Curves.Drag(At(0.7f, -6), EditorModifiers.None);
        Curves.Drag(At(0.65f, -12), EditorModifiers.None);
        Curves.Release();
        Assert.Equal(entries + 1, Entries);
        Assert.Contains(Curve.Points, p => MathF.Abs(p.X - 0.65f) < 0.01f && MathF.Abs(p.Y + 12) < 0.2f);

        // Shift: mostly sideways, so only x moves.
        var index = Curves.PointAt(At(0.65f, -12));
        Curves.Press(At(0.65f, -12), EditorModifiers.Shift, doubleClick: false);
        Curves.Drag(At(0.6f, -14), EditorModifiers.Shift);
        Curves.Release();
        Assert.True(index >= 0);
        Assert.Contains(Curve.Points, p => MathF.Abs(p.X - 0.6f) < 0.01f && MathF.Abs(p.Y + 12) < 0.2f);
        Doc.History.Undo();
        Doc.History.Undo();
        Assert.Contains(Curve.Points, p => p.X == 0.8f && p.Y == 0);
    }

    [Fact]
    public void ADoubleClickAddsAPointOrRemovesOne()
    {
        var count = Curve.Points.Count;
        Curves.Press(At(0.3f, -40), EditorModifiers.None, doubleClick: true);
        Assert.Equal(count + 1, Curve.Points.Count);
        Curves.Press(At(0.3f, -40), EditorModifiers.None, doubleClick: true);
        Assert.Equal(count, Curve.Points.Count);
    }

    [Fact]
    public void CurvesOverLabelsAreColumnsAndNewCurvesSpanTheRange()
    {
        var area = Adaptive.FindParameter("area")!;
        var curve = Doc.AddCurve(SongCurves.NewCurve("s0", MusicCurveTarget.Volume, area));
        Assert.Equal([new MusicCurvePoint(0, SongCurves.MinDb), new MusicCurvePoint(1, 0)], curve.Points);
        _tab.SelectCurve(curve);
        Assert.Equal(SongCurves.Left + Curves.PlotWidth / 4, Curves.X(area, 0), 3); // the first of two columns' middle
        Assert.Equal(1, Curves.ValueAt(area, SongCurves.Left + Curves.PlotWidth * 0.9f));
        var cutoff = SongCurves.NewCurve("s0", MusicCurveTarget.Cutoff, Intensity);
        Assert.Equal([new MusicCurvePoint(0, 800), new MusicCurvePoint(1, MusicCurve.MaxCutoffHz)], cutoff.Points);
    }

    [Fact]
    public void ParameterValuesComeFromAuditionsWhenNothingPlays()
    {
        Assert.Equal(0, _tab.ParameterValue("intensity"));
        _tab.SetAudition("intensity", 0.4f);
        Assert.Equal(0.4f, _tab.ParameterValue("intensity"));
        Assert.False(_tab.Baked.Available(Doc)); // no audio server in this host, no bake: Baked stays off
        _tab.SetAuditionBaked(true);
        Assert.False(_tab.AuditionBaked);
    }
}

/// <summary>The transition popover's edits (each chip one history entry) and the Curves pane in the headless editor.</summary>
[Collection(nameof(SerialEditor))]
public sealed class TransitionPopoverTests
{
    private static (HeadlessEditor Editor, SongTab Tab) Open()
    {
        var editor = new HeadlessEditor();
        var path = SongTab.CreateFile(Path.Combine(editor.Directory, "Project", "Assets", "Music", "Theme.msong"));
        editor.Workspace.Commands.OpenSong(path);
        editor.Tick(3);
        var tab = Assert.IsType<SongTab>(editor.Workspace.Session.ActiveTab);
        SongAdaptiveDemo.Build(tab.Document);
        return (editor, tab);
    }

    [Fact]
    public void ThePopoverEditsSourcesConditionsAndEntry()
    {
        var (editor, tab) = Open();
        using (editor)
        {
            var doc = tab.Document;
            var adaptive = doc.Song.Adaptive!;
            var rule = adaptive.Transitions[0]; // Explore → Combat when area = Combat
            var intro = adaptive.Sections.FindIndex(s => s.Name == "Intro");
            var entries = doc.History.Actions.Count;

            AdaptivePanel.EditRule(doc, adaptive, rule, "rule-from-toggle", intro);
            Assert.Equal(["s2", "s1"], rule.From);
            AdaptivePanel.EditRule(doc, adaptive, rule, "rule-from-toggle", -1);
            Assert.Equal([MusicPlan.AnySection], rule.From);
            AdaptivePanel.EditRule(doc, adaptive, rule, "cond-add", 0);
            Assert.Equal(2, rule.When!.All!.Count);
            AdaptivePanel.EditRule(doc, adaptive, rule, "rule-mode", 1);
            Assert.Equal(2, rule.When!.Any!.Count);
            Assert.Null(rule.When.All);
            AdaptivePanel.EditRule(doc, adaptive, rule, "cond-remove", 0);
            AdaptivePanel.EditRule(doc, adaptive, rule, "cond-remove", 0);
            Assert.Null(rule.When); // no condition left: it fires only from code
            AdaptivePanel.EditRule(doc, adaptive, rule, "rule-entry", 1);
            Assert.Equal(MusicEntry.Sync, rule.Entry);
            Assert.Equal(entries + 7, doc.History.Actions.Count);
            for (var i = 0; i < 7; i++)
                doc.History.Undo();
            Assert.Equal(["s2"], rule.From);
            Assert.Equal("Combat", rule.When!.All![0].Value.Label);
        }
    }

    [Fact]
    public void ThePopoverAndTheCurvesPaneShowAndAllocateNothingWhenSteady()
    {
        var (editor, tab) = Open();
        using (editor)
        {
            editor.Workspace.ShowMusicRightTab(adaptive: true);
            editor.Tick(2);
            editor.Workspace.SongPanel.Adaptive.EditTransition(0);
            tab.SelectCurve(tab.Document.Song.Adaptive!.Curves[1]);
            editor.Workspace.SetMusicPane(SongBottomPanel.Curves);
            editor.Tick(3);
            var panel = editor.Workspace.SongPanel.Document;
            var html = panel.GetElementById("song-adaptive").InnerRml;
            foreach (var expected in (string[])["+ Condition", "at its start", "in sync", "segment: none", "stinger: none", "+ Curve", "Baked"])
                Assert.True(html.Contains(expected, StringComparison.Ordinal), expected);
            Assert.Contains("Combat · volume over intensity", panel.AsElement().InnerRml, StringComparison.Ordinal);
            Assert.Empty(editor.RmlMessages);

            tab.Player.Engine.Process(new float[SongEngine.BlockFrames * 2]);
            editor.Tick(20);
            var canvas = tab.CurvesCanvas;
            canvas.ViewSize = new Vector2(900, 300);
            for (var i = 0; i < 3; i++)
                canvas.RunDraw();
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 20; i++)
                canvas.RunDraw();
            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
            editor.Tick(30);
            before = GC.GetAllocatedBytesForCurrentThread();
            editor.Tick(60);
            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        }
    }
}

/// <summary>
/// Baked audition (A5): the demo song baked (A3, WAV stems, no helper) and played back through the runtime's
/// <see cref="AdaptiveMusicInstance"/> on a manual null-device server, as the Adaptive panel's Baked mode does.
/// </summary>
[Collection(nameof(SerialEditor))]
public sealed class BakedAuditionTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mf-baked-").FullName;
    private readonly AssetDatabase _previous = AssetDatabase.Current;
    private readonly AssetDatabase _db;

    public BakedAuditionTests()
    {
        _db = new AssetDatabase(_root);
        AssetDatabase.Current = _db;
    }

    public void Dispose()
    {
        AssetDatabase.Current = _previous;
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void TheBakePlaysThroughTheRuntimeAndFollowsItsParameters()
    {
        var path = Path.Combine(_root, "Assets", "Music", "Theme.msong");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var document = new SongDocument(NewSong(), path);
        SongAdaptiveDemo.Build(document);
        SongBaker.Bake(document.Song, document.Name, _db, new SongBakeOptions { InstrumentFactory = static (_, _) => new ToneInstrument(), MaxParallelism = 2 },
            TestContext.Current.CancellationToken);

        using var server = AudioServer.Create(new AudioOptions { Device = AudioDeviceMode.NullManual, BusLayoutPath = null, SampleRate = Rate });
        using var baked = new BakedAudition(() => server);
        Assert.True(baked.Available(document));
        Assert.True(baked.Play(document, null, new Dictionary<string, float> { ["intensity"] = 1 }));
        var output = new float[800 * 2];
        var energy = 0f;
        var sections = new List<string>();
        for (var frame = 0; frame < 60 * 9; frame++) // 9 s: Intro (4 s), then Explore; Combat once area is Combat
        {
            if (frame == 60 * 5)
                baked.SetParameter("area", 1);
            server.Process(new GameTime { DeltaTime = 1f / 60f, FrameCount = (uint)frame });
            Assert.True(server.WaitForStreams(2848, TimeSpan.FromSeconds(10)));
            server.RenderNullDevice(800, output);
            energy += Energy(output);
            if (baked.Instance!.CurrentSection is { } section && (sections.Count == 0 || sections[^1] != section))
                sections.Add(section);
        }

        Assert.True(energy > 0);
        Assert.Equal(["s1", "s2", "s3"], sections); // Intro, Explore, Combat
        Assert.Equal(1, baked.Instance!.GetParameter("area"));
        baked.Stop();
        Assert.False(baked.Playing);
    }
}

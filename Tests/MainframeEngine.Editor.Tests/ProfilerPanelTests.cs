using MainframeEngine.EditorLink;

namespace MainframeEngine.Editor.Tests;

/// <summary>The bottom dock's Profiler tab (G10): its capture, the view over it, and the editor as a source.</summary>
[Collection(nameof(SerialEditor))]
public sealed class ProfilerPanelTests : IDisposable
{
    private readonly HeadlessEditor _editor = new();

    public ProfilerPanelTests() => Profiler.ResetForTests();

    public void Dispose()
    {
        _editor.Dispose();
        Profiler.ResetForTests();
    }

    private EditorWorkspace W => _editor.Workspace;

    private static EditorLinkMessage Markers() => new()
    {
        Type = EditorLinkMessageType.ProfileMarkers,
        ProfileMarkers =
        [
            new EditorLinkProfileMarker(1, ProfilerCategory.Engine, ProfilerMarkerKind.Cpu, "Frame", "", 0),
            new EditorLinkProfileMarker(2, ProfilerCategory.Script, ProfilerMarkerKind.Cpu, "Enemy.Think", "/src/Enemy.cs", 12),
            new EditorLinkProfileMarker(3, ProfilerCategory.Render, ProfilerMarkerKind.GpuInPass, "Tonemap", "", 0),
            new EditorLinkProfileMarker(0, ProfilerCategory.Engine, ProfilerMarkerKind.Counter, "Draw calls", "", (int)ProfilerCounterUnit.Count),
            new EditorLinkProfileMarker(1, ProfilerCategory.Engine, ProfilerMarkerKind.Counter, "Uploaded", "", (int)ProfilerCounterUnit.Bytes),
        ],
    };

    // A frame of `ms` CPU time starting at `start` µs: Frame > Enemy.Think (two calls), a GPU zone.
    private static EditorLinkMessage Frame(ulong number, long start, float ms, bool spike = false) => new()
    {
        Type = EditorLinkMessageType.ProfileFrame,
        ProfileFrame = new EditorLinkProfileFrame
        {
            Frame = number,
            StartMicroseconds = start,
            EndMicroseconds = start + (long)(ms * 1000),
            CpuMs = ms,
            WaitMs = 1f,
            GpuMs = 2f,
            IsSpike = spike,
            EncoderBoundaries = true,
            Counters = [(0, 40 + (long)number), (1, 3 * 1024 * 1024)],
            Threads = number == 1 ? [(0, "Main")] : [],
            CpuEvents =
            [
                new ProfilerEvent(start, start + (long)(ms * 1000), 1, 0, 0),
                new ProfilerEvent(start + 100, start + 600, 2, 1, 0, 2048),
                new ProfilerEvent(start + 700, start + 900, 2, 1, 0),
            ],
            GpuZones = [new ProfilerEvent(start + 2000, start + 4000, 3, 0, ProfilerEvent.GpuThread)],
        },
    };

    [Fact]
    public void TheBottomDockSwitchesBetweenOutputAndProfiler()
    {
        Assert.Equal(BottomDockTab.Output, W.BottomTab);
        Assert.True(W.OutputPanel.Visible);
        Assert.False(W.ProfilerPanel.Visible);
        W.ShowBottomTab(BottomDockTab.Profiler);
        _editor.Tick(2);
        Assert.True(W.ProfilerPanel.Visible);
        Assert.False(W.OutputPanel.Visible);
        Assert.Equal((int)BottomDockTab.Profiler, W.Layout.Settings.BottomTab); // kept with the layout
        W.ShowBottomTab(BottomDockTab.Output);
        Assert.True(W.OutputPanel.Visible);
        Assert.Empty(_editor.RmlMessages);
    }

    [Fact]
    public void FramesFillTheGraphTableAndCountersAndSelectionPicksAFrame()
    {
        W.ShowBottomTab(BottomDockTab.Profiler);
        var panel = W.ProfilerPanel;
        panel.Feed(Markers());
        panel.Feed(Frame(1, 1_000_000, 5f));
        panel.Feed(Frame(2, 1_016_000, 40f, spike: true));
        panel.Feed(Frame(3, 1_056_000, 8f));
        _editor.Tick(3);

        Assert.Equal(3, panel.Capture.Count);
        Assert.Equal(3, panel.Bars.Count);
        Assert.Contains("spike", panel.Bars[1].Css);
        Assert.Equal(2, panel.SelectedIndex); // following the newest
        var think = panel.Rows.Single(r => r.Name == "Enemy.Think");
        Assert.Equal(2, think.Calls);
        Assert.Equal(0.7, think.SelfMs, 6);
        Assert.Equal(2048, think.Alloc);
        Assert.True(think.HasSource);
        Assert.Contains(panel.Rows, r => r.Gpu && r.Name == "GPU · Tonemap");
        Assert.Equal(["Draw calls", "Uploaded"], panel.Counters.Select(c => c.Name));
        Assert.Equal(["43", "3.0 MiB"], panel.Counters.Select(c => c.Value));
        Assert.Contains(panel.TimelineBars, b => b.Label == "≈ Tonemap"); // in-pass zone on an encoder-boundary GPU

        panel.PickSlowest();
        Assert.Equal(1, panel.SelectedIndex);
        Assert.Contains("selected", panel.Bars[1].Css);
        Assert.Equal("42", panel.Counters[0].Value);

        panel.Pick(0);
        panel.Pick(2, extend: true); // a range: the table averages it
        Assert.Equal(2, panel.Rows.Single(r => r.Name == "Enemy.Think").Calls);
        panel.SetSort(ProfilerSort.Name);
        Assert.Equal(panel.Rows.Where(r => !r.Gpu).Select(r => r.Name).Order(StringComparer.OrdinalIgnoreCase), panel.Rows.Where(r => !r.Gpu).Select(r => r.Name));

        var opened = new List<(string, int)>();
        W.SourceOpener = (file, line) =>
        {
            opened.Add((file, line));
            return true;
        };
        Assert.True(panel.OpenSource(panel.Rows.ToList().FindIndex(r => r.Name == "Enemy.Think")));
        Assert.Equal([("/src/Enemy.cs", 12)], opened);
        Assert.Empty(_editor.RmlMessages);
    }

    [Fact]
    public void PauseFreezesTheViewAndClearEmptiesIt()
    {
        var panel = W.ProfilerPanel;
        panel.Feed(Markers());
        panel.Feed(Frame(1, 0, 5f));
        panel.SetPaused(true);
        panel.Feed(Frame(2, 16_000, 5f));
        Assert.Equal(1, panel.Capture.Count);
        panel.SetPaused(false);
        panel.Feed(Frame(3, 32_000, 5f));
        Assert.Equal(2, panel.Capture.Count);
        panel.Clear();
        Assert.Equal(0, panel.Capture.Count);
        Assert.Empty(panel.Rows);
    }

    [Fact]
    public void SavedTracesReopenWithTheirFrames()
    {
        var panel = W.ProfilerPanel;
        panel.Feed(Markers());
        for (ulong f = 1; f <= 4; f++)
            panel.Feed(Frame(f, (long)f * 16_000, 4f + f));
        var path = Path.Combine(_editor.Directory, "trace.json");
        panel.SaveTrace(path);

        panel.Clear();
        Assert.True(panel.OpenTrace(path));
        Assert.StartsWith("Trace: trace.json", panel.SourceLabel, StringComparison.Ordinal);
        Assert.Equal(4, panel.Capture.Count);
        Assert.Equal(8f, panel.Capture[3].CpuMs);
        Assert.Equal(3, panel.Capture[3].CpuEvents.Length);
        Assert.Contains(panel.Capture.Markers.Values, m => m.Name == "Enemy.Think");
        Assert.Contains(panel.Capture.Counters.Values, c => c.Name == "Draw calls");
        _editor.Tick(2);
        Assert.Equal(2, panel.Rows.Single(r => r.Name == "Enemy.Think").Calls);
        Assert.False(panel.OpenTrace(Path.Combine(_editor.Directory, "missing.json")));
    }

    [Fact]
    public void TheEditorProfilesItself()
    {
        var panel = W.ProfilerPanel;
        panel.SelectEditorSource();
        Assert.Equal("Editor", panel.SourceLabel);
        panel.ToggleRecord();
        Assert.True(panel.IsRecording);
        Assert.True(Profiler.IsRecording);
        for (var i = 0; i < 10; i++)
        {
            Profiler.BeginFrame();
            _editor.Tick();
            Profiler.EndFrame(1f, 0f);
        }

        _editor.Tick();
        Assert.True(panel.Capture.Count > 0);
        Assert.Contains(panel.Capture.Markers.Values, m => m.Name == "Tree.Tick");
        panel.ToggleRecord();
        Assert.False(panel.IsRecording);
        Assert.False(Profiler.IsRecording);
    }

    [Fact]
    public void WithoutAGameRecordDoesNothing()
    {
        var panel = W.ProfilerPanel;
        panel.SelectSource(null);
        panel.ToggleRecord();
        Assert.False(panel.IsRecording);
        Assert.Equal("No game running", panel.SourceLabel);
    }

    [Fact]
    public void CounterValuesAreFormattedByUnit()
    {
        Assert.Equal("1,234", ProfilerPanel.FormatCounter(1234, ProfilerCounterUnit.Count));
        Assert.Equal("1.5 KiB", ProfilerPanel.FormatCounter(1536, ProfilerCounterUnit.Bytes));
        Assert.Equal("0.250 ms", ProfilerPanel.FormatCounter(250, ProfilerCounterUnit.Microseconds));
    }
}

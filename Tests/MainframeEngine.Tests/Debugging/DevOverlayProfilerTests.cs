using MainframeEngine.Tests.UI;

namespace MainframeEngine.Tests.Debugging;

/// <summary>The overlay's Profiler section (G10). RmlUi and the profiler are process-wide: serial.</summary>
[Collection(nameof(SerialRmlUi))]
public sealed class DevOverlayProfilerTests : IDisposable
{
    private static readonly ProfilerMarker Work = new("OverlayTest.Work");

    public DevOverlayProfilerTests() => Profiler.ResetForTests();

    public void Dispose() => Profiler.ResetForTests();

    private static void Frames(UiTestTree ui, int count, float cpuMs)
    {
        for (var i = 0; i < count; i++)
        {
            Profiler.BeginFrame();
            using (Work.Auto())
            {
                var until = System.Diagnostics.Stopwatch.GetTimestamp() + System.Diagnostics.Stopwatch.Frequency / 500; // 2 ms: the top scope
                while (System.Diagnostics.Stopwatch.GetTimestamp() < until)
                    Thread.SpinWait(10);
            }

            ui.Tick();
            Profiler.EndFrame(cpuMs, 0.5f);
        }
    }

    [Fact]
    public void TheSectionHoldsTheProfilerOnlyWhileExpandedAndShown()
    {
        using var ui = new UiTestTree();
        var overlay = new DevOverlay(ui.Tree);
        ui.Servers.Register(overlay);
        DevOverlayPanels.AddBuiltIns(overlay, engine: null);
        var panel = overlay.Panels.Single(p => p.Id == "profiler");
        Assert.False(panel.Expanded); // collapsed by default
        overlay.Visible = true;
        Frames(ui, 3, 5f);
        Assert.False(Profiler.IsRecording);

        panel.Expanded = true;
        Assert.True(Profiler.IsRecording);
        Frames(ui, 40, 20f);
        var document = (UiDocument)ui.Tree.Root.GetNode<UiLayer>("DevOverlay").GetChild(0);
        var section = document.QuerySelector("[data-model=dev_profiler]")!;
        Assert.Contains("OverlayTest.Work", section.InnerRml); // a top CPU scope
        Assert.Contains("20.00 / 0.00 / 0.50", section.InnerRml); // CPU / GPU / wait of the selected frame
        Assert.NotNull(document.QuerySelector(".dev-bar.over")); // 20 ms is over the 16.7 ms budget

        overlay.Visible = false;
        Assert.False(Profiler.IsRecording);
        overlay.Visible = true;
        Assert.True(Profiler.IsRecording);
        panel.Expanded = false;
        Assert.False(Profiler.IsRecording);
        overlay.Dispose();
    }

    [Fact]
    public void RefreshingTheSectionsSnapshotAllocatesNothing()
    {
        var stats = new DevOverlayProfilerStats();
        var token = Profiler.Acquire();
        try
        {
            for (var f = 0; f < 120; f++)
            {
                Profiler.BeginFrame();
                using (Work.Auto())
                {
                }

                Profiler.EndFrame(f % 7 == 0 ? 40f : 4f, 0f);
                if (f % 15 == 0)
                    stats.Refresh();
            }

            var allocated = AllocationGate.SmallestWindow(() =>
            {
                for (var f = 0; f < 60; f++)
                {
                    Profiler.BeginFrame();
                    using (Work.Auto())
                    {
                    }

                    Profiler.EndFrame(4f, 0f);
                    if (f % 15 == 0)
                        stats.Refresh();
                }
            });
            Assert.Equal(0, allocated);
            Assert.True(stats.Has);
            Assert.Equal(DevOverlayProfilerStats.BarCount, stats.Bars.Count);
            Assert.Contains(stats.CpuTop, row => row.Name == "OverlayTest.Work");
        }
        finally
        {
            Profiler.Release(token);
        }
    }

    [Fact]
    public async Task SaveWritesATraceUnderTheGamesUserData()
    {
        var root = Directory.CreateTempSubdirectory("mf-profiles-");
        var previous = Environment.GetEnvironmentVariable(UserDataPaths.OverrideVariable);
        Environment.SetEnvironmentVariable(UserDataPaths.OverrideVariable, root.FullName);
        try
        {
            var token = Profiler.Acquire();
            Profiler.BeginFrame();
            Profiler.EndFrame(1f, 0f);
            Profiler.Release(token);
            var save = DevOverlayProfiler.SaveTrace("Profiler Test");
            Assert.NotNull(save);
            await save;
            var files = Directory.GetFiles(Path.Combine(root.FullName, UserDataPaths.SafeName("Profiler Test"), "profiles"), "trace-*.json");
            Assert.Single(files);
            Assert.Single(ChromeTraceReader.Load(files[0]).Frames);
        }
        finally
        {
            Environment.SetEnvironmentVariable(UserDataPaths.OverrideVariable, previous);
            root.Delete(recursive: true);
        }
    }
}

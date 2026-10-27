using MainframeEngine.UI.Rml;

namespace MainframeEngine;

/// <summary>A bar of the overlay's frame graph (data-bound: height in dp, over-budget and spike classes).</summary>
internal sealed class ProfilerBarRow
{
    public int Height;
    public bool Over;
    public bool Spike;
}

/// <summary>A row of the overlay's top-scope lists: the marker's cached name, ms per frame, bar width in dp.</summary>
internal sealed class ProfilerScopeRow
{
    public string Name = "";
    public float Ms;
    public int Width;
    public bool Approximate;
}

/// <summary>
/// What the overlay's Profiler section shows (G10), refreshed from <see cref="Profiler.Snapshot"/> at the overlay's rate:
/// the last <see cref="BarCount"/> frames as bars, the selected frame's CPU/GPU/wait ms, the top CPU scopes and GPU zones
/// by self time averaged over the refresh window, and a few counters. Allocation-free after construction.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static",
    Justification = "Bound by the overlay's data model through the snapshot instance.")]
internal sealed class DevOverlayProfilerStats
{
    public const int BarCount = 60;
    public const int TopCount = 5;

    /// <summary>Graph height (dp) of a 33.3 ms frame; taller frames are clamped.</summary>
    public const int GraphHeight = 56;

    private readonly ProfilerStatistics _cpu = new();
    private readonly ProfilerStatistics _gpu = new();
    private readonly ushort[] _top = new ushort[TopCount];
    private long _lastFrame = -1;

    public DevOverlayProfilerStats()
    {
        for (var i = 0; i < BarCount; i++)
            Bars.Add(new ProfilerBarRow());
    }

    public List<ProfilerBarRow> Bars { get; } = new(BarCount);

    public List<ProfilerScopeRow> CpuTop { get; } = new(TopCount);

    public List<ProfilerScopeRow> GpuTop { get; } = new(TopCount);

    private readonly ProfilerScopeRow[] _cpuRows = [.. Enumerable.Range(0, TopCount).Select(_ => new ProfilerScopeRow())];
    private readonly ProfilerScopeRow[] _gpuRows = [.. Enumerable.Range(0, TopCount).Select(_ => new ProfilerScopeRow())];

    public bool Has;
    public float CpuMs, GpuMs, WaitMs;
    public long Frame;
    public int DrawCalls, Gen0;
    public float TrianglesK, AllocatedKiB;
    public int Nodes;

    /// <summary>The spike threshold (ms, 0 = off) and auto-freeze, bound to the section's controls.</summary>
    public float SpikeMs
    {
        get => Profiler.SpikeThresholdMs;
        set => Profiler.SpikeThresholdMs = float.IsFinite(value) ? Math.Max(0f, value) : 0f;
    }

    public bool Freeze
    {
        get => Profiler.FreezeOnSpike;
        set => Profiler.FreezeOnSpike = value;
    }

    public bool Paused => Profiler.Paused;

    /// <summary>Copies the newest frames from the snapshot; false when nothing changed.</summary>
    public bool Refresh()
    {
        var snapshot = Profiler.Snapshot;
        var count = snapshot.FrameCount;
        Has = count > 0;
        if (!Has)
            return true;

        // Bars: the newest BarCount frames, oldest left.
        var first = Math.Max(0, count - BarCount);
        var empty = BarCount - (count - first);
        for (var i = 0; i < BarCount; i++)
        {
            var bar = Bars[i];
            if (i < empty)
            {
                (bar.Height, bar.Over, bar.Spike) = (0, false, false);
                continue;
            }

            var frame = snapshot.GetFrame(first + i - empty);
            var ms = Math.Max(frame.CpuMs, float.IsNaN(frame.GpuMs) ? 0f : frame.GpuMs);
            bar.Height = (int)Math.Clamp(MathF.Round(ms / 33.333f * GraphHeight), 1f, GraphHeight);
            bar.Over = ms > 16.667f;
            bar.Spike = frame.IsSpike || ms > 33.333f;
        }

        // The selected frame: the newest with its GPU data in (the newest is two frames from its read-back).
        var selected = snapshot.GetFrame(count - 1);
        for (var i = count - 1; i >= Math.Max(0, count - 4); i--)
        {
            var candidate = snapshot.GetFrame(i);
            if (!candidate.GpuPending)
            {
                selected = candidate;
                break;
            }
        }

        (Frame, CpuMs, WaitMs, GpuMs) = (selected.Number, selected.CpuMs, selected.WaitMs, float.IsNaN(selected.GpuMs) ? 0f : selected.GpuMs);
        DrawCalls = (int)snapshot.GetCounter(selected, EngineCounters.DrawCalls);
        TrianglesK = snapshot.GetCounter(selected, EngineCounters.Triangles) / 1000f;
        AllocatedKiB = snapshot.GetCounter(selected, EngineCounters.ManagedAllocated) / 1024f;
        Gen0 = (int)snapshot.GetCounter(selected, EngineCounters.Gen0);
        Nodes = (int)snapshot.GetCounter(selected, EngineCounters.Nodes);

        // Top scopes and zones over the frames since the last refresh (the newest few when paused).
        _cpu.Clear();
        _gpu.Clear();
        var from = count - 1;
        while (from > 0 && snapshot.GetFrame(from - 1).Number > _lastFrame && count - from < 30)
            from--;
        if (Profiler.Paused)
            from = Math.Max(0, count - 8);
        for (var i = from; i < count; i++)
        {
            var frame = snapshot.GetFrame(i);
            _cpu.Add(snapshot.GetEvents(frame));
            _cpu.Frames++;
            if (!frame.GpuPending)
            {
                _gpu.Add(snapshot.GetGpuZones(frame));
                _gpu.Frames++;
            }
        }

        _lastFrame = snapshot.GetFrame(count - 1).Number;
        FillTop(_cpu, _cpuRows, CpuTop, skipFrame: true);
        FillTop(_gpu, _gpuRows, GpuTop, skipFrame: false);
        return true;
    }

    private void FillTop(ProfilerStatistics statistics, ProfilerScopeRow[] rows, List<ProfilerScopeRow> into, bool skipFrame)
    {
        var found = statistics.Top(statistics.SelfTicks, _top);
        into.Clear();
        var frames = Math.Max(1, statistics.Frames);
        var max = 0f;
        for (var i = 0; i < found; i++)
        {
            var marker = _top[i];
            if (skipFrame && marker == EngineMarkers.Frame.Id)
                continue;
            var row = rows[into.Count];
            var info = Profiler.MarkerAt(marker);
            row.Name = info.Name;
            row.Approximate = info.Kind == ProfilerMarkerKind.GpuInPass;
            row.Ms = (float)(statistics.SelfTicks[marker] * 1000.0 / System.Diagnostics.Stopwatch.Frequency / frames);
            max = MathF.Max(max, row.Ms);
            into.Add(row);
        }

        foreach (var row in into)
            row.Width = max > 0f ? (int)MathF.Round(row.Ms / max * 120f) : 0;
    }
}

/// <summary>
/// The overlay's Profiler section (G10): collapsed by default; while it is expanded and the overlay visible it holds the
/// profiler (<see cref="Profiler.Acquire"/>), so a visible overlay costs nothing extra until someone looks at it.
/// </summary>
internal static class DevOverlayProfiler
{
    private static readonly RmlStructType<ProfilerBarRow> BarType = new RmlStructType<ProfilerBarRow>()
        .Member("h", static b => b.Height)
        .Member("over", static b => b.Over)
        .Member("spike", static b => b.Spike);

    private static readonly RmlStructType<ProfilerScopeRow> ScopeType = new RmlStructType<ProfilerScopeRow>()
        .Member("name", static s => s.Name)
        .Member("ms", static s => s.Ms)
        .Member("w", static s => s.Width)
        .Member("approx", static s => s.Approximate);

    public const string Rml = """
        <div class="dev-row">
          <button class="dev-button" data-event-click="pause()">{{paused ? 'Resume' : 'Pause'}}</button>
          <button class="dev-button" data-event-click="save()">Save trace</button>
        </div>
        <div class="dev-row"><span>Spike ms (0 = off)</span><span class="v">{{spike | format(1)}}</span></div>
        <div class="dev-row"><input class="dev-slider" type="range" min="0" max="100" step="0.5" data-value="spike"/></div>
        <div class="dev-row"><span>Freeze on spike</span><input type="checkbox" data-checked="freeze"/></div>
        <div class="dev-graph" data-if="has"><div class="dev-budget"></div><div data-for="b : bars" class="dev-bar" data-class-over="b.over" data-class-spike="b.spike" data-style-height="b.h + 'dp'"></div></div>
        <div class="dev-row" data-if="has"><span>Frame {{frame}}: CPU / GPU / wait ms</span><span class="v">{{cpu | format(2)}} / {{gpu | format(2)}} / {{wait | format(2)}}</span></div>
        <div class="dev-heading" data-if="has">CPU scopes (self ms / frame)</div>
        <div class="dev-scope" data-for="s : cpuTop"><span class="dev-scope-name">{{s.name}}</span><div class="dev-scope-bar" data-style-width="s.w + 'dp'"></div><span class="v">{{s.ms | format(2)}}</span></div>
        <div class="dev-heading" data-if="has">GPU zones (ms / frame)</div>
        <div class="dev-scope" data-for="s : gpuTop"><span class="dev-scope-name">{{s.approx ? '≈ ' : ''}}{{s.name}}</span><div class="dev-scope-bar gpu" data-style-width="s.w + 'dp'"></div><span class="v">{{s.ms | format(2)}}</span></div>
        <div class="dev-row" data-if="has"><span>Draws / ktris / alloc KiB / gen0 / nodes</span><span class="v">{{draws}} / {{tris | format(1)}} / {{alloc | format(1)}} / {{gen0}} / {{nodes}}</span></div>
        <div class="dev-row" data-if="!has"><span class="v">Recording…</span></div>
        """;

    public static void Add(DevOverlay overlay, string gameName)
    {
        var stats = new DevOverlayProfilerStats();
        var holder = new Holder(overlay);
        var panel = overlay.AddPanel("profiler", "Profiler", Rml, p => p.Model!
            .Bind("has", stats, static s => s.Has)
            .Bind("paused", stats, static s => s.Paused)
            .Bind("spike", stats, static s => s.SpikeMs, static (s, v) => s.SpikeMs = v)
            .Bind("freeze", stats, static s => s.Freeze, static (s, v) => s.Freeze = v)
            .Bind("frame", stats, static s => (int)s.Frame)
            .Bind("cpu", stats, static s => s.CpuMs)
            .Bind("gpu", stats, static s => s.GpuMs)
            .Bind("wait", stats, static s => s.WaitMs)
            .Bind("draws", stats, static s => s.DrawCalls)
            .Bind("tris", stats, static s => s.TrianglesK)
            .Bind("alloc", stats, static s => s.AllocatedKiB)
            .Bind("gen0", stats, static s => s.Gen0)
            .Bind("nodes", stats, static s => s.Nodes)
            .BindList("bars", stats.Bars, BarType)
            .BindList("cpuTop", stats.CpuTop, ScopeType)
            .BindList("gpuTop", stats.GpuTop, ScopeType)
            .Event("pause", _ =>
            {
                Profiler.Paused = !Profiler.Paused;
                p.Model?.Dirty("paused");
            })
            .Event("save", _ => SaveTrace(gameName)));
        panel.Expanded = false; // collapsed: the profiler records only while someone looks
        holder.Panel = panel;
        panel.ExpandedChanged += holder.Update;
        overlay.VisibleChanged += holder.Update;
        overlay.Refreshed += () =>
        {
            if (!holder.Holding)
                return;
            stats.Refresh();
            panel.Model?.DirtyAll();
        };
    }

    /// <summary>Writes the whole history to <c>{user data}/{game}/profiles/trace-{utc}.json</c> and logs the path.</summary>
    public static Task? SaveTrace(string gameName)
    {
        if (Profiler.Snapshot.FrameCount == 0)
        {
            Log.Warning("[Profiler] Nothing recorded yet: no trace saved.");
            return null;
        }

        var path = TracePath(gameName, DateTime.UtcNow);
        return Profiler.SaveTraceAsync(path, title: gameName).ContinueWith(t =>
        {
            if (t.Exception is { } e)
                Log.Error($"[Profiler] Could not save the trace '{path}': {e.InnerException?.Message}");
            else
                Log.Info($"[Profiler] Trace saved to {path} (open it in ui.perfetto.dev or chrome://tracing).");
        }, TaskScheduler.Default);
    }

    public static string TracePath(string gameName, DateTime utc) =>
        Path.Combine(UserDataPaths.GetDirectory(gameName), "profiles",
            "trace-" + utc.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + ".json");

    // Holds the profiler while the section is expanded and the overlay visible.
    private sealed class Holder(DevOverlay overlay)
    {
        private int _token;

        public DevOverlayPanel? Panel { get; set; }

        public bool Holding => _token != 0;

        public void Update()
        {
            var want = overlay.Visible && Panel is { Expanded: true };
            if (want && _token == 0)
                _token = Profiler.Acquire();
            else if (!want && _token != 0)
            {
                Profiler.Release(_token);
                _token = 0;
            }
        }
    }
}

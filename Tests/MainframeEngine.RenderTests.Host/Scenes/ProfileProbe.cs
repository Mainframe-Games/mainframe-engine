namespace MainframeEngine.RenderTests.Host.Scenes;

/// <summary>
/// G10: what a <c>--profile</c> run reports from the profiler's history: the GPU zones per frame (averaged), the top CPU
/// scopes, the read-back latency and the zone checks. Called from the update after the warm-up; allocation-free per frame
/// once its dictionaries hold every zone (the result is built at the end).
/// </summary>
internal sealed class ProfileProbe
{
    private readonly ProfilerStatistics _cpu = new();
    private readonly double[] _gpuMs = new double[Profiler.MaxMarkers];
    private readonly ushort[] _lastOrder = new ushort[256];
    private int _lastOrderCount;
    private long _lastGpuFrame = -1;
    private long _lastCpuFrame = -1;
    private int _latencyMin = int.MaxValue;
    private int _latencyMax;
    private int _errors;
    private int _gpuFrames;

    public void Sample()
    {
        var snapshot = Profiler.Snapshot;
        var count = snapshot.FrameCount;
        if (count == 0)
            return;
        var newest = snapshot.GetFrame(count - 1);
        for (var i = count - 1; i >= Math.Max(0, count - 8); i--)
        {
            var frame = snapshot.GetFrame(i);
            if (frame.GpuPending)
                continue;
            var latency = (int)(newest.Number - frame.Number);
            _latencyMin = Math.Min(_latencyMin, latency);
            _latencyMax = Math.Max(_latencyMax, latency);
            if (frame.Number > _lastGpuFrame && frame.GpuZoneCount > 0)
                AddGpu(snapshot, frame);
            break;
        }

        if (newest.Number > _lastCpuFrame)
        {
            _lastCpuFrame = newest.Number;
            _cpu.Add(snapshot.GetEvents(newest));
            _cpu.Frames++;
        }
    }

    private void AddGpu(ProfilerSnapshot snapshot, in ProfilerFrame frame)
    {
        _lastGpuFrame = frame.Number;
        _gpuFrames++;
        var zones = snapshot.GetGpuZones(frame);
        _lastOrderCount = 0;
        long first = long.MaxValue, last = long.MinValue;
        for (var i = 0; i < zones.Length; i++)
        {
            first = Math.Min(first, zones[i].Start);
            last = Math.Max(last, zones[i].End);
        }

        for (var i = 0; i < zones.Length; i++)
        {
            var zone = zones[i];
            if (zone.End < zone.Start)
                _errors++;
            _gpuMs[zone.Marker] += zone.DurationTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (zone.Depth == 0 && _lastOrderCount < _lastOrder.Length)
                _lastOrder[_lastOrderCount++] = zone.Marker;
        }

        // The frame's GPU time is its first-to-last span: no zone reaches outside it.
        var span = (last - first) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        if (zones.Length > 0 && Math.Abs(span - frame.GpuMs) > 0.05 + span * 0.01)
            _errors++;
    }

    public void Fill(Dictionary<string, double> gpuZones, Dictionary<string, double> topCpu, List<string> order, out int latencyMin,
        out int latencyMax, out int errors, out int frames)
    {
        for (var id = 0; id < Profiler.MarkerCount; id++)
        {
            if (_gpuMs[id] > 0 && _gpuFrames > 0)
            {
                var name = Profiler.MarkerAt((ushort)id).Name;
                gpuZones[name] = gpuZones.GetValueOrDefault(name) + _gpuMs[id] / _gpuFrames;
            }
        }

        Span<ushort> top = stackalloc ushort[10];
        var found = _cpu.Top(_cpu.SelfTicks, top);
        for (var i = 0; i < found; i++)
            topCpu[Profiler.MarkerAt(top[i]).Name] = _cpu.SelfTicks[top[i]] * 1000.0 / System.Diagnostics.Stopwatch.Frequency / Math.Max(1, _cpu.Frames);
        for (var i = 0; i < _lastOrderCount; i++)
            order.Add(Profiler.MarkerAt(_lastOrder[i]).Name);
        latencyMin = _latencyMin == int.MaxValue ? 0 : _latencyMin;
        latencyMax = _latencyMax;
        errors = _errors;
        frames = _gpuFrames;
    }
}

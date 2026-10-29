using BenchmarkDotNet.Attributes;
using Silk.NET.Vulkan;

namespace MainframeEngine.Benchmarks;

/// <summary>
/// The frame profiler's costs (G10), guarding "costs nothing when off": a scope with the profiler compiled out
/// (<see cref="Profiler.DisabledSwitch"/>, set before the profiler's first use in that benchmark's own process), a scope
/// while nobody records, a recorded scope (with and without allocation tracking; per scope, a frame end amortised over
/// 1000), a counter add, a frame end draining 500 events, and a GPU zone's CPU side outside a frame. All 0 B.
/// </summary>
[MemoryDiagnoser]
public class ProfilerBenchmarks : IDisposable
{
    private const int Scopes = 1000;

    // Created in the setups, after the switch: a static marker would initialise the profiler before ScopeOff's setup ran.
    private ProfilerMarker _marker;
    private ProfilerCounter _counter;
    private GpuProfiler _gpu = null!;
    private int _token;

    [GlobalSetup(Target = nameof(ScopeOff))]
    public void SetupOff()
    {
        AppContext.SetSwitch(Profiler.DisabledSwitch, true); // each benchmark runs in its own process: before first use there
        _marker = new ProfilerMarker("Bench.Scope");
        if (Profiler.IsAvailable)
            throw new InvalidOperationException("The profiler was initialised before the switch was set.");
    }

    [GlobalSetup(Targets = [nameof(ScopeIdle), nameof(CounterAdd), nameof(GpuZoneOffCpuSide)])]
    public void SetupIdle()
    {
        Profiler.ResetForTests();
        Create();
    }

    [GlobalSetup(Targets = [nameof(ScopeRecording), nameof(EndFrame500Events)])]
    public void SetupRecording()
    {
        Profiler.ResetForTests();
        Create();
        _token = Profiler.Acquire();
        Warm();
    }

    [GlobalSetup(Target = nameof(ScopeRecordingWithAllocations))]
    public void SetupRecordingWithAllocations()
    {
        Profiler.ResetForTests(new ProfilerOptions { TrackAllocations = true });
        Create();
        _token = Profiler.Acquire();
        Warm();
    }

    [GlobalCleanup]
    public void Cleanup() => Profiler.Release(_token);

    public void Dispose()
    {
        _gpu?.Dispose();
        GC.SuppressFinalize(this);
    }

    private void Create()
    {
        _marker = new ProfilerMarker("Bench.Scope");
        _counter = new ProfilerCounter("Bench.Counter");
        _gpu = new GpuProfiler();
    }

    private void Warm()
    {
        for (var f = 0; f < 4; f++)
        {
            Profiler.BeginFrame();
            for (var i = 0; i < Scopes; i++)
            {
                using var _ = _marker.Auto();
            }

            Profiler.EndFrame(1f, 0f);
        }
    }

    [Benchmark(OperationsPerInvoke = Scopes)]
    public void ScopeOff()
    {
        for (var i = 0; i < Scopes; i++)
        {
            using var _ = _marker.Auto();
        }
    }

    [Benchmark(OperationsPerInvoke = Scopes)]
    public void ScopeIdle()
    {
        for (var i = 0; i < Scopes; i++)
        {
            using var _ = _marker.Auto();
        }
    }

    [Benchmark(OperationsPerInvoke = Scopes)]
    public void ScopeRecording()
    {
        Profiler.BeginFrame();
        for (var i = 0; i < Scopes; i++)
        {
            using var _ = _marker.Auto();
        }

        Profiler.EndFrame(1f, 0f);
    }

    [Benchmark(OperationsPerInvoke = Scopes)]
    public void ScopeRecordingWithAllocations()
    {
        Profiler.BeginFrame();
        for (var i = 0; i < Scopes; i++)
        {
            using var _ = _marker.Auto();
        }

        Profiler.EndFrame(1f, 0f);
    }

    [Benchmark(OperationsPerInvoke = Scopes)]
    public void CounterAdd()
    {
        for (var i = 0; i < Scopes; i++)
            _counter.Add(1);
    }

    [Benchmark]
    public void EndFrame500Events()
    {
        Profiler.BeginFrame();
        for (var i = 0; i < 499; i++)
        {
            using var _ = _marker.Auto();
        }

        Profiler.EndFrame(1f, 0f);
    }

    [Benchmark(OperationsPerInvoke = Scopes)]
    public void GpuZoneOffCpuSide()
    {
        var cb = default(CommandBuffer);
        for (var i = 0; i < Scopes; i++)
        {
            using var _ = _gpu.Zone(cb, _marker);
        }
    }
}

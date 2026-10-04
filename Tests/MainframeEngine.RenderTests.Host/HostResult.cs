using System.Text.Json;

namespace MainframeEngine.RenderTests.Host;

/// <summary>What one host run reports back to the test (written as <c>result.json</c>).</summary>
public sealed record HostResult
{
    public const string FileName = "result.json";

    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public required string Scene { get; init; }
    public required string DeviceName { get; init; }
    public required string Driver { get; init; }

    /// <summary>Golden-image folder for this driver: <c>lavapipe</c>, <c>moltenvk</c>, ...</summary>
    public required string PlatformTag { get; init; }

    public required bool ValidationEnabled { get; init; }
    public required int ValidationWarnings { get; init; }
    public required int ValidationErrors { get; init; }
    public required IReadOnlyList<string> ValidationMessages { get; init; }
    public required int RenderedFrames { get; init; }

    /// <summary>What <c>Engine.Run()</c> returned (the host process exits with it).</summary>
    public int ExitCode { get; init; }

    /// <summary>Scene-specific self-checks that failed (empty when all passed).</summary>
    public IReadOnlyList<string> SceneCheckFailures { get; init; } = [];

    /// <summary>Managed bytes allocated on the main thread over <see cref="MeasuredFrames"/>; null when not measured.</summary>
    public long? AllocatedBytes { get; init; }
    public int MeasuredFrames { get; init; }

    public required IReadOnlyList<CaptureInfo> Captures { get; init; }

    /// <summary>Pipeline-cache bytes the renderer accepted from disk at startup (0 = cold cache).</summary>
    public int PipelineCacheLoadedBytes { get; init; }

    /// <summary>GPU allocator totals at the end of the run (before teardown).</summary>
    public int GpuDeviceMemoryCount { get; init; }
    public int GpuAllocationCount { get; init; }
    public long GpuReservedBytes { get; init; }
    public int ShaderModuleCount { get; init; }

    /// <summary>The device's <c>maxMemoryAllocationCount</c> limit.</summary>
    public long MaxMemoryAllocationCount { get; init; }

    /// <summary>Frame times over the <c>--perf</c> window (wall clock between frames, VSync off), in ms; 0 when not measured.</summary>
    public double AverageFrameMs { get; init; }
    public double P95FrameMs { get; init; }
    public int PerfMeasuredFrames { get; init; }

    /// <summary>The configuration the engine was built in (Debug/Release).</summary>
    public string Configuration { get; init; } = "";

    /// <summary>Mesh draw statistics of the last frame (instances, culled, draw calls).</summary>
    public int MeshInstances { get; init; }
    public int MeshDrawCalls { get; init; }
    public int MeshShadowDrawCalls { get; init; }

    /// <summary>Average CPU / GPU time of the shadow pass over the perf frames (ShadowSystem timings), in ms.</summary>
    public double ShadowCpuMs { get; init; }

    public double ShadowGpuMs { get; init; }
    public int MeshPipelines { get; init; }

    public sealed record CaptureInfo(uint Frame, string Path, int Width, int Height);
}

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

    /// <summary>Managed bytes allocated on the main thread over <see cref="MeasuredFrames"/>; null when not measured.</summary>
    public long? AllocatedBytes { get; init; }
    public int MeasuredFrames { get; init; }

    public required IReadOnlyList<CaptureInfo> Captures { get; init; }

    public sealed record CaptureInfo(uint Frame, string Path, int Width, int Height);
}

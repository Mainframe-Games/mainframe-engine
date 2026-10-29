using MainframeEngine.RenderTests.Host;

namespace MainframeEngine.RenderTests;

/// <summary>
/// G10: GPU zones on the showcase scene (both drivers). Timings are not deterministic, so these check structure: the
/// expected pass-level zones in frame order, non-negative durations inside the frame's GPU span, the read-back exactly
/// <see cref="IVulkanContext.MaxFramesInFlight"/> frames late, validation clean (query resets outside render passes, debug
/// labels). No goldens: the profiler draws nothing unless the overlay shows it.
/// </summary>
public sealed class ProfilerGpuZoneTests
{
    private static string Output(string scene) => Path.Combine(RenderTestEnvironment.ArtifactsDirectory, scene);

    [Fact]
    public void TheShowcaseRecordsEveryPassAsAZoneReadBackTwoFramesLate()
    {
        var result = HostRunner.Run("showcase", Output("showcase-gpu-zones"), "--frames", "150", "--hidden", "--profile");
        Gates.AssertValidationClean(result);
        if (!result.GpuTimestampsSupported)
            Assert.Skip($"{result.DeviceName} has no timestamps on its graphics queue: zones record debug labels only.");

        Assert.True(result.ProfiledFrames > 60, $"Only {result.ProfiledFrames} frames had GPU zones.");
        Assert.Equal(0, result.GpuZoneErrors);
        Assert.Equal(IVulkanContext.MaxFramesInFlight, result.GpuReadbackLatencyMin);
        Assert.Equal(IVulkanContext.MaxFramesInFlight, result.GpuReadbackLatencyMax);

        // Pass-level zones in recording order (the showcase has shadows, no prepass, the dev overlay and a HUD).
        string[] expected = ["Uploads", "Shadows", "Offscreen", "Main pass", "UI offscreen", "Tonemap", "Overlay"];
        var order = result.GpuZoneOrder;
        var positions = expected.Select(name => order.IndexOf(name)).ToArray();
        Assert.True(positions.All(p => p >= 0), $"Missing zones: {string.Join(", ", expected.Where(n => !order.Contains(n)))}; got {string.Join(", ", order)}.");
        Assert.True(positions.SequenceEqual(positions.Order()), $"Zones out of order: {string.Join(", ", order)}.");
        Assert.All(result.GpuZones, zone => Assert.True(zone.Value >= 0, $"{zone.Key}: {zone.Value} ms"));
        Assert.Contains(result.TopCpuScopes.Keys, name => name is "Render" or "Update" or "Main pass" or "Tree.Tick" or "Frame");
        Assert.Equal(0, result.ProfilerUnbalancedScopes);
    }
}

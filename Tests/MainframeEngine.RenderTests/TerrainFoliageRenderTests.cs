namespace MainframeEngine.RenderTests;

/// <summary>Terrain foliage (ADR 0157): grass, ferns and pebbles on a hill, swaying at a fixed wind time; 0 B frames.</summary>
public class TerrainFoliageRenderTests
{
    private static string Output(string scene) => Path.Combine(RenderTestEnvironment.ArtifactsDirectory, scene);

    [Fact]
    public void GrassOnAHillRendersCleanlyAndMatchesGolden()
    {
        var result = HostRunner.Run("terrain-foliage", Output("terrain-foliage"), "--capture", "30", "--hidden");

        Assert.True(result.SceneCheckFailures.Count == 0, string.Join("\n", result.SceneCheckFailures));
        Gates.AssertValidationClean(result);
        Gates.AssertMatchesGolden(result, 30);
    }

    [Fact]
    public void FoliageFramesAllocateNothingWhileTheCameraWalks()
    {
        // The walk thins, hides and shows tiles every frame; 0 B after the warm-up.
        const int warmup = 150, measured = 300;
        var result = HostRunner.RunWithEnvironment(new Dictionary<string, string> { ["DOTNET_TieredCompilation"] = "0" },
            "terrain-foliage", Output("terrain-foliage-alloc"), "--alloc", $"{warmup}:{measured}", "--hidden");

        Assert.Equal(measured, result.MeasuredFrames);
        Assert.True(result.AllocatedBytes == 0,
            $"Foliage frames allocated {result.AllocatedBytes} managed bytes over {measured} frames; per-frame code must not allocate.");
        Gates.AssertValidationClean(result);
    }

    [Fact]
    public void FoliageCostOnTheForestSizedTerrainIsReported()
    {
        // 256 m at 0.5 m with dense grass to 45 m around a walking camera, against the same terrain without foliage.
        // Report only: under display pacing (MoltenVK at 120 Hz) the CPU figure can include a wait, so the difference
        // is not a stable gate. The per-frame foliage work itself (distance thinning over every tile) is O(tiles), 0 B.
        var with = HostRunner.Run("terrain-foliage", Output("terrain-foliage-perf"), "--count", "1", "--perf", "120:300", "--no-validation", "--hidden");
        var without = HostRunner.Run("terrain-foliage", Output("terrain-foliage-perf-base"), "--count", "2", "--perf", "120:300", "--no-validation", "--hidden");

        Assert.True(with.SceneCheckFailures.Count == 0, string.Join("\n", with.SceneCheckFailures));
        TestContext.Current.SendDiagnosticMessage(
            $"terrain foliage ({with.Configuration}, {with.DeviceName}): CPU {with.AverageCpuFrameMs:0.00} ms per frame " +
            $"(p95 {with.P95CpuFrameMs:0.00}), {without.AverageCpuFrameMs:0.00} ms without foliage; wall clock " +
            $"{with.AverageFrameMs:0.00} / {without.AverageFrameMs:0.00} ms; {with.MeshDrawCalls} draws");
    }
}

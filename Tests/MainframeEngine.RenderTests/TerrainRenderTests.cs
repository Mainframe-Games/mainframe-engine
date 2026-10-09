namespace MainframeEngine.RenderTests;

/// <summary>
/// The Realistic terrain: chunk meshes with LOD levels and skirts, lit and shadowed by the sun; and its splat material
/// (ADR 0156: four generated layers, height blending, a triplanar cliff, near and far bands).
/// </summary>
public class TerrainRenderTests
{
    private static string Output(string scene) => Path.Combine(RenderTestEnvironment.ArtifactsDirectory, scene);

    [Fact]
    public void TerrainRendersCleanlyAndMatchesGolden()
    {
        var result = HostRunner.Run("terrain", Output("terrain"), "--capture", "10", "--hidden");

        Assert.True(result.SceneCheckFailures.Count == 0, string.Join("\n", result.SceneCheckFailures));
        Gates.AssertValidationClean(result);
        Gates.AssertMatchesGolden(result, 10);
    }

    [Fact]
    public void TerrainFramesAllocateNothingWhileTheCameraOrbits()
    {
        // The orbit loops every 120 frames: every chunk's LOD switches happen during the warm-up and again measured.
        const int warmup = 150, measured = 240;
        var result = HostRunner.RunWithEnvironment(new Dictionary<string, string> { ["DOTNET_TieredCompilation"] = "0" },
            "terrain", Output("terrain-alloc"), "--alloc", $"{warmup}:{measured}", "--hidden");

        Assert.Equal(measured, result.MeasuredFrames);
        Assert.True(result.AllocatedBytes == 0,
            $"Terrain frames allocated {result.AllocatedBytes} managed bytes over {measured} frames; per-frame code must not allocate.");
        Gates.AssertValidationClean(result);
    }

    [Fact]
    public void TerrainSplatMaterialRendersCleanlyAndMatchesGolden()
    {
        var result = HostRunner.Run("terrain-splat", Output("terrain-splat"), "--capture", "10", "--hidden");

        Assert.True(result.SceneCheckFailures.Count == 0, string.Join("\n", result.SceneCheckFailures));
        Gates.AssertValidationClean(result);
        Gates.AssertMatchesGolden(result, 10);
    }

    [Fact]
    public void TerrainSplatFramesAllocateNothingWhileTheCameraOrbits()
    {
        const int warmup = 150, measured = 240;
        var result = HostRunner.RunWithEnvironment(new Dictionary<string, string> { ["DOTNET_TieredCompilation"] = "0" },
            "terrain-splat", Output("terrain-splat-alloc"), "--alloc", $"{warmup}:{measured}", "--hidden");

        Assert.Equal(measured, result.MeasuredFrames);
        Assert.True(result.AllocatedBytes == 0,
            $"Terrain splat frames allocated {result.AllocatedBytes} managed bytes over {measured} frames; per-frame code must not allocate.");
        Gates.AssertValidationClean(result);
    }
}

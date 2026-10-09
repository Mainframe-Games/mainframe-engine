using MainframeEngine.RenderTests.Host;

namespace MainframeEngine.RenderTests;

/// <summary>
/// ADR 0165: screen-space ambient occlusion (GTAO). The <c>ssao</c> scene checks itself (creases darker than open floor and
/// wall with SSAO on, alike with it off, no halo beyond the radius); the SSAO frame matches its golden. Every run is
/// validation-clean.
/// </summary>
public class SsaoTests
{
    private static string Output(string name) => Path.Combine(RenderTestEnvironment.ArtifactsDirectory, name);

    private static void AssertSelfChecks(HostResult result) =>
        Assert.True(result.SceneCheckFailures.Count == 0, string.Join("\n", result.SceneCheckFailures));

    [Fact]
    public void CreasesAndContactsDarkenWithSsao()
    {
        var on = HostRunner.Run("ssao", Output("ssao"), "--capture", "10", "--hidden");
        Gates.AssertValidationClean(on);
        AssertSelfChecks(on);
        Gates.AssertMatchesGolden(on, 10);
    }

    [Fact]
    public void WithoutSsaoTheCreasesShadeLikeOpenSurfaces()
    {
        // Proves the scene's lighting is even: the darkening in the SSAO run comes from SSAO alone.
        var off = HostRunner.Run("ssao", Output("ssao-off"), "--capture", "10", "--count", "1", "--hidden");
        Gates.AssertValidationClean(off);
        AssertSelfChecks(off);
    }

    [Fact]
    public void SsaoWithAMovingCameraAndJitterAllocatesNothingPerFrame()
    {
        // The camera orbits (the passes run every frame) with the projection jitter on (the rotation changes per frame).
        const int warmup = 60, measured = 240;
        var result = HostRunner.Run("ssao", Output("ssao-alloc"), "--count", "2", "--jitter", "--alloc", $"{warmup}:{measured}", "--hidden");
        Assert.Equal(measured, result.MeasuredFrames);
        Assert.True(result.AllocatedBytes == 0,
            $"SSAO with a moving camera allocated {result.AllocatedBytes} managed bytes over {measured} frames.");
        Gates.AssertValidationClean(result);
    }
}

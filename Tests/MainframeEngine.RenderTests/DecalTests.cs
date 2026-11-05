using MainframeEngine.RenderTests.Host;

namespace MainframeEngine.RenderTests;

/// <summary>G6.5: forward decals. Validation-clean, self-checked, matching golden, allocation-free.</summary>
public class DecalTests
{
    private static string Output(string name) => Path.Combine(RenderTestEnvironment.ArtifactsDirectory, name);

    [Fact]
    public void DecalsOnPbrAndBlinnPhongSurfacesMatchGolden()
    {
        // A badge (albedo + normal) and a metal plate (ORM) on a PBR floor, stripes across a Blinn-Phong box's top (its
        // sides faded by angle), a glowing sign on a slab; one decal past its distance fade.
        var result = HostRunner.Run("decals", Output("decals"), "--capture", "6", "--hidden");

        Assert.True(result.SceneCheckFailures.Count == 0, string.Join("\n", result.SceneCheckFailures));
        Gates.AssertValidationClean(result);
        Gates.AssertMatchesGolden(result, 6);
    }

    [Fact]
    public void MovingDecalFramesAllocateNothing()
    {
        // The badge slides every frame: the masks change; the atlas never rebuilds.
        const int warmup = 60, measured = 240;
        var result = HostRunner.Run("decals", Output("decals-alloc"), "--count", "1", "--alloc", $"{warmup}:{measured}", "--hidden");

        Assert.True(result.SceneCheckFailures.Count == 0, string.Join("\n", result.SceneCheckFailures));
        Assert.Equal(measured, result.MeasuredFrames);
        Assert.True(result.AllocatedBytes == 0, $"Decal frames allocated {result.AllocatedBytes} managed bytes over {measured} frames.");
        Gates.AssertValidationClean(result);
    }
}

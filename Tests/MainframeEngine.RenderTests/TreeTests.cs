namespace MainframeEngine.RenderTests;

/// <summary>ADR 0158: <see cref="Tree3D"/> in both styles and a <see cref="TreeScatter"/> forest.</summary>
public class TreeTests
{
    private static string Output(string scene) => Path.Combine(RenderTestEnvironment.ArtifactsDirectory, scene);

    [Fact]
    public void RealisticTreesSwayInTheWindAndMatchGolden()
    {
        // An oak and a pine at level 0, t = 90 / 60 s under a fixed wind; the still run (--count 1) must differ: leaves,
        // branches and their shadows moved.
        var windy = HostRunner.Run("tree-realistic", Output("tree-realistic"), "--capture", "90", "--hidden");
        var still = HostRunner.Run("tree-realistic", Output("tree-realistic-still"), "--capture", "90", "--count", "1", "--hidden");

        Assert.True(windy.SceneCheckFailures.Count == 0, string.Join("\n", windy.SceneCheckFailures));
        Gates.AssertValidationClean(windy);
        Gates.AssertValidationClean(still);
        Gates.AssertMatchesGolden(windy, 90);
        var comparison = ImageComparison.Compare(Png.ReadRgba8(windy.Captures[0].Path), Png.ReadRgba8(still.Captures[0].Path), channelTolerance: 8);
        Assert.True(comparison.DifferingPixels > comparison.TotalPixels / 100,
            $"Only {comparison.DifferingPixels} of {comparison.TotalPixels} pixels moved with the wind on.");
    }

    [Fact]
    public void ClusterCardTreesMatchGolden()
    {
        // ADR 0172: the oak and pine of tree-realistic with leaf-cluster cards (baked twig atlases), in the same wind.
        var result = HostRunner.Run("tree-clusters", Output("tree-clusters"), "--capture", "90", "--hidden");

        Assert.True(result.SceneCheckFailures.Count == 0, string.Join("\n", result.SceneCheckFailures));
        Gates.AssertValidationClean(result);
        Gates.AssertMatchesGolden(result, 90);
    }

    [Fact]
    public void TreesBetweenTheirLastCastingLevelAndTheImpostorStillCastShadows()
    {
        // ADR 0179: a stand drawn at level 2, which does not cast; the impostor casts from 20 m (where level 1 stops), not
        // from 60 m (where it is drawn). The ground in front of the stand must be about as shadowed as with level 2's
        // meshes casting (--count 1), and clearly more than with no tree shadows (--count 2: what it was before).
        var handOff = HostRunner.Run("tree-shadow-handoff", Output("tree-shadow-handoff"), "--capture", "30", "--hidden");
        var meshes = HostRunner.Run("tree-shadow-handoff", Output("tree-shadow-handoff-meshes"), "--capture", "30", "--count", "1", "--hidden");
        var none = HostRunner.Run("tree-shadow-handoff", Output("tree-shadow-handoff-none"), "--capture", "30", "--count", "2", "--hidden");

        Assert.True(handOff.SceneCheckFailures.Count == 0, string.Join("\n", handOff.SceneCheckFailures));
        Gates.AssertValidationClean(handOff);
        Gates.AssertValidationClean(meshes);
        Gates.AssertValidationClean(none);
        Gates.AssertMatchesGolden(handOff, 30);

        var lit = GroundLuminance(none);
        var withHandOff = GroundLuminance(handOff);
        var withMeshes = GroundLuminance(meshes);
        TestContext.Current.SendDiagnosticMessage(
            $"tree-shadow-handoff: ground luminance {withHandOff:F1} (impostor casters), {withMeshes:F1} (level-2 meshes), {lit:F1} (no tree shadows)");
        Assert.True(lit - withMeshes > 8f, $"the reference's tree shadows darken the ground only by {lit - withMeshes:F1}");
        Assert.True(lit - withHandOff > 0.5f * (lit - withMeshes),
            $"the impostors cast {lit - withHandOff:F1} of darkening where the meshes cast {lit - withMeshes:F1}: the hand-off is missing");
    }

    // Mean luminance of the lower third of the frame: the ground between the camera and the stand, where its shadows fall.
    private static float GroundLuminance(Host.HostResult result)
    {
        var image = Png.ReadRgba8(result.Captures.Single().Path);
        double sum = 0;
        var count = 0;
        for (var y = image.Height * 2 / 3; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                var i = (y * image.Width + x) * 4;
                sum += 0.2126 * image.Pixels[i] + 0.7152 * image.Pixels[i + 1] + 0.0722 * image.Pixels[i + 2];
                count++;
            }
        }

        return (float)(sum / count);
    }

    [Fact]
    public void ImpostorsMatchTheMeshAtTheSwitchDistance()
    {
        // ADR 0172: three trees at 62 m as their finest mesh level, then as octahedral impostors (impostor shadow casters);
        // the impostor capture is a golden and must stay close to the mesh's.
        var mesh = HostRunner.Run("tree-impostor", Output("tree-impostor-mesh"), "--capture", "30", "--hidden");
        var impostor = HostRunner.Run("tree-impostor", Output("tree-impostor"), "--capture", "30", "--count", "1", "--hidden");

        Assert.True(impostor.SceneCheckFailures.Count == 0, string.Join("\n", impostor.SceneCheckFailures));
        Gates.AssertValidationClean(mesh);
        Gates.AssertValidationClean(impostor);
        Gates.AssertMatchesGolden(impostor, 30);
        var comparison = ImageComparison.Compare(Png.ReadRgba8(mesh.Captures[0].Path), Png.ReadRgba8(impostor.Captures[0].Path), channelTolerance: 24);
        TestContext.Current.SendDiagnosticMessage(
            $"tree-impostor: {comparison.DifferingPixels} of {comparison.TotalPixels} pixels differ from the mesh by more than 24 levels");
        Assert.True(comparison.DifferingPixels < comparison.TotalPixels * 8 / 100,
            $"The impostors differ from the mesh in {comparison.DifferingPixels} of {comparison.TotalPixels} pixels.");
    }

    [Fact]
    public void HierarchicalWindSwaysTreesAndMatchesGolden()
    {
        // ADR 0172: pivot-stream wind (trunk sway, branch and twig bends, leaf flutter) in a strong wind at t = 1.5 s; the
        // still run and the same wind 1.25 s later must both differ (it moves, and keeps moving), with clean validation.
        var windy = HostRunner.Run("tree-wind", Output("tree-wind"), "--capture", "90", "--hidden");
        var later = HostRunner.Run("tree-wind", Output("tree-wind-later"), "--capture", "165", "--hidden");
        var still = HostRunner.Run("tree-wind", Output("tree-wind-still"), "--capture", "90", "--count", "1", "--hidden");

        Assert.True(windy.SceneCheckFailures.Count == 0, string.Join("\n", windy.SceneCheckFailures));
        Gates.AssertValidationClean(windy);
        Gates.AssertValidationClean(later);
        Gates.AssertValidationClean(still);
        Gates.AssertMatchesGolden(windy, 90);
        var image = Png.ReadRgba8(windy.Captures[0].Path);
        foreach (var other in (Host.HostResult[])[still, later])
        {
            var comparison = ImageComparison.Compare(image, Png.ReadRgba8(other.Captures[0].Path), channelTolerance: 8);
            Assert.True(comparison.DifferingPixels > comparison.TotalPixels / 50,
                $"Only {comparison.DifferingPixels} of {comparison.TotalPixels} pixels differ from {Path.GetFileName(other.Captures[0].Path)}.");
        }
    }

    [Fact]
    public void BarkDetailMatchesGolden()
    {
        // ADR 0172: a root flare, branch collars, moss climbing from the roots and detail normals, close up.
        var result = HostRunner.Run("tree-bark", Output("tree-bark"), "--capture", "30", "--hidden");

        Assert.True(result.SceneCheckFailures.Count == 0, string.Join("\n", result.SceneCheckFailures));
        Gates.AssertValidationClean(result);
        Gates.AssertMatchesGolden(result, 30);
    }

    [Fact]
    public void NewSpeciesMatchGolden()
    {
        // ADR 0172: Birch, Beech, Spruce and Fir Medium with their painted leaves and bark and LengthProfile crowns.
        var result = HostRunner.Run("tree-species", Output("tree-species"), "--capture", "90", "--hidden");

        Assert.True(result.SceneCheckFailures.Count == 0, string.Join("\n", result.SceneCheckFailures));
        Gates.AssertValidationClean(result);
        Gates.AssertMatchesGolden(result, 90);
    }

    [Fact]
    public void LowPolyTreesMatchGolden()
    {
        var result = HostRunner.Run("tree-lowpoly", Output("tree-lowpoly"), "--capture", "90", "--hidden");

        Assert.True(result.SceneCheckFailures.Count == 0, string.Join("\n", result.SceneCheckFailures));
        Gates.AssertValidationClean(result);
        Gates.AssertMatchesGolden(result, 90);
    }

    [Fact]
    public void SmallForestDrawsOneLevelPerChunkAndMatchesGolden()
    {
        var result = HostRunner.Run("tree-forest", Output("tree-forest"), "--capture", "30", "--hidden");

        Assert.True(result.SceneCheckFailures.Count == 0, string.Join("\n", result.SceneCheckFailures));
        Gates.AssertValidationClean(result);
        Gates.AssertMatchesGolden(result, 30);
    }

    [Fact]
    public void FlyingThroughTwoThousandTreesAllocatesNothingPerFrame()
    {
        const int warmup = 60, measured = 120;
        var result = HostRunner.Run("tree-forest", Output("tree-forest-alloc"), "--count", "2000", "--alloc", $"{warmup}:{measured}", "--hidden");

        Assert.True(result.SceneCheckFailures.Count == 0, string.Join("\n", result.SceneCheckFailures));
        Assert.Equal(measured, result.MeasuredFrames);
        Assert.True(result.AllocatedBytes == 0, $"Forest frames allocated {result.AllocatedBytes} managed bytes over {measured} frames.");
        Gates.AssertValidationClean(result);
    }

    [Fact]
    public void FoliageQualityForestMatchesGolden()
    {
        // ADR 0172: tree-forest with clusters, hierarchical wind, bark detail, impostors (and their casters) and per-instance
        // cross-fades, under the prepass and TAA (the Forest's path: biased prepassed foliage, dithered bands).
        var result = HostRunner.Run("tree-forest-g8e", Output("tree-forest-g8e"), "--capture", "30", "--prepass", "--aa", "taa", "--hidden");

        Assert.True(result.SceneCheckFailures.Count == 0, string.Join("\n", result.SceneCheckFailures));
        Gates.AssertValidationClean(result);
        Gates.AssertMatchesGolden(result, 30);
    }

    [Fact]
    public void FlyingThroughAFoliageQualityForestAllocatesNothingPerFrame()
    {
        // ADR 0172: every level switching per instance on the fly-through, impostors in the coarse cascades, prepass and TAA.
        // Per instance, each level draws with its own material copy, prepared on its first draw like any material: the scene
        // shows every batch for its first frames, as the Forest's prewarm does.
        const int warmup = 60, measured = 120;
        var result = HostRunner.Run("tree-forest-g8e", Output("tree-forest-g8e-alloc"), "--count", "2000", "--alloc", $"{warmup}:{measured}",
            "--prepass", "--aa", "taa", "--hidden");

        Assert.True(result.SceneCheckFailures.Count == 0, string.Join("\n", result.SceneCheckFailures));
        Assert.Equal(measured, result.MeasuredFrames);
        Assert.True(result.AllocatedBytes == 0, $"Forest frames allocated {result.AllocatedBytes} managed bytes over {measured} frames.");
        Gates.AssertValidationClean(result);
    }

    [Fact]
    public void FlyingThroughTwoThousandTreesCostsLittleCpu()
    {
        // 2,000 trees over 256 m (three species, two seeds each, 32 m chunks; 120 without sun shadows on a CPU device such as lavapipe): the
        // CPU cost of a frame is the batches' culling and draws, a few ms at most (Release on a GPU device). Reports the
        // GPU-bound frame time too.
        var result = HostRunner.Run("tree-forest", Output("tree-forest-perf"), "--count", "2000", "--perf", "60:240", "--no-validation", "--hidden");

        Assert.True(result.SceneCheckFailures.Count == 0, string.Join("\n", result.SceneCheckFailures));
        TestContext.Current.SendDiagnosticMessage(
            $"tree-forest 2000 ({result.Configuration}, {result.DeviceName}): frame {result.AverageFrameMs:0.00} ms " +
            $"(p95 {result.P95FrameMs:0.00}), CPU {result.AverageCpuFrameMs:0.00} ms (p95 {result.P95CpuFrameMs:0.00}), shadow GPU " +
            $"{result.ShadowGpuMs:0.00} ms; {result.MeshDrawCalls} draws, {result.MeshShadowDrawCalls} shadow draws");
        if (result.Configuration == "Release" && !result.IsCpuDevice)
            Assert.True(result.AverageCpuFrameMs < 4.0, $"The forest costs {result.AverageCpuFrameMs:0.00} ms of CPU per frame.");
    }
}

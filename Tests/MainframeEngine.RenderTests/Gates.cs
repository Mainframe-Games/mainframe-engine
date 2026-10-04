using MainframeEngine.RenderTests.Host;

namespace MainframeEngine.RenderTests;

/// <summary>Assertions shared by every scene: the validation gate and golden-image matching.</summary>
public static class Gates
{
    /// <summary>Zero validation warnings and errors, including those reported at shutdown.</summary>
    public static void AssertValidationClean(HostResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.ValidationEnabled)
        {
            const string reason = "Vulkan validation layers (VK_LAYER_KHRONOS_validation) are not installed.";
            if (RenderTestEnvironment.IsCi)
                Assert.Fail(reason + " CI must install them (vulkan-validationlayers).");
            Assert.Skip(reason + " Install the Vulkan SDK to run the validation gate locally.");
        }

        Assert.True(result.ValidationErrors == 0 && result.ValidationWarnings == 0,
            $"Validation reported {result.ValidationErrors} error(s) and {result.ValidationWarnings} warning(s) " +
            $"on {result.DeviceName}:\n{string.Join("\n", result.ValidationMessages)}");
    }

    /// <summary>
    /// Compares a captured frame with <c>Goldens/&lt;platform-tag&gt;/&lt;file&gt;</c>. Missing goldens skip
    /// (the actual frame is left in the artifacts folder); <c>UPDATE_GOLDENS=1</c> records them.
    /// On mismatch, writes <c>.expected.png</c> and <c>.diff.png</c> next to the actual frame.
    /// </summary>
    public static void AssertMatchesGolden(HostResult result, uint frame,
        int channelTolerance = ImageComparison.DefaultChannelTolerance,
        double maxDifferingPercent = ImageComparison.DefaultMaxDifferingPercent)
    {
        ArgumentNullException.ThrowIfNull(result);
        var capture = result.Captures.SingleOrDefault(c => c.Frame == frame)
                      ?? throw new InvalidOperationException($"Frame {frame} was not captured.");

        var fileName = Path.GetFileName(capture.Path);
        var goldenPath = Path.Combine(RenderTestEnvironment.GoldensDirectory, result.PlatformTag, fileName);

        if (RenderTestEnvironment.UpdateGoldens)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(goldenPath)!);
            File.Copy(capture.Path, goldenPath, overwrite: true);
            return;
        }

        if (!File.Exists(goldenPath))
            Assert.Skip($"No '{result.PlatformTag}' golden for {fileName} ({result.DeviceName}); the frame is at " +
                        $"{capture.Path}. Record it with UPDATE_GOLDENS=1 (just golden-update).");

        var expected = Png.ReadRgba8(goldenPath);
        var actual = Png.ReadRgba8(capture.Path);
        var comparison = ImageComparison.Compare(expected, actual, channelTolerance);
        if (ImageComparison.IsMatch(comparison, maxDifferingPercent))
            return;

        var stem = Path.Combine(Path.GetDirectoryName(capture.Path)!, Path.GetFileNameWithoutExtension(capture.Path));
        File.Copy(goldenPath, stem + ".expected.png", overwrite: true);
        if (comparison.DiffImage is not null)
            Png.WriteRgba8(stem + ".diff.png", actual.Width, actual.Height, comparison.DiffImage);

        Assert.Fail(comparison.SizeMatches
            ? $"{fileName} differs from the {result.PlatformTag} golden: {comparison.DifferingPixels} pixels " +
              $"({comparison.DifferingPercent:0.###}% > {maxDifferingPercent}%) exceed ±{channelTolerance}, " +
              $"max channel delta {comparison.MaxChannelDelta}. See {stem}.diff.png."
            : $"{fileName} is {actual.Width}x{actual.Height} but the {result.PlatformTag} golden is " +
              $"{expected.Width}x{expected.Height}.");
    }
}

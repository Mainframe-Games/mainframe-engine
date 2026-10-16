using System.Security.Cryptography;
using MainframeEngine.Tests.TestAssets;

namespace MainframeEngine.Tests.Project;

/// <summary>
/// BRUTAL licence 2.2 (ADR 0192): the logo is shown unaltered except for scaling, so the engine's copies are BRUTAL's
/// docs/branding files byte for byte. build/brand/brutal-logos.sha256 (written when they were fetched) guards them against
/// any later edit, re-export or image optimiser.
/// </summary>
public sealed class BrutalLogoTests
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    [Fact]
    public void TheBrutalLogosAreBrutalsFilesByteForByte()
    {
        var root = TestPaths.RepositoryRoot();
        var lines = File.ReadAllLines(Path.Combine(root, "build", "brand", "brutal-logos.sha256")).Where(l => l.Length > 0).ToArray();
        Assert.Equal(4, lines.Length); // white and black, in the engine content and in docs/images
        foreach (var line in lines)
        {
            var hash = line[..64];
            var path = line[66..];
            var bytes = File.ReadAllBytes(Path.Combine(root, path));
            Assert.True(bytes.AsSpan().StartsWith(PngSignature), $"{path} is not a PNG.");
            Assert.Equal(hash, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
    }

    [Fact]
    public void TheEngineContentCarriesBothVariants()
    {
        Assert.True(File.Exists(ContentPaths.Resolve("Content/Brand/brutal/LOGO_BRUTAL_WHT.png")));
        Assert.True(File.Exists(ContentPaths.Resolve("Content/Brand/brutal/LOGO_BRUTAL_BLK.png")));
    }
}

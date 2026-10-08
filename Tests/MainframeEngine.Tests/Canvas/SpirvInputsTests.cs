namespace MainframeEngine.Tests.Canvas;

/// <summary>
/// <see cref="SpirvInputs"/> reads the stage-input locations of compiled SPIR-V: the canvas shader build uses it to make the
/// vertex stage write only what the fragment stage reads (Slang drops unread fragment inputs; ADR 0144).
/// </summary>
public sealed class SpirvInputsTests
{
    private static readonly string ShaderDir = Path.Combine(FindRepoRoot(), "MainframeEngine", "Content", "Shaders");

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MainframeEngine.slnx")))
                return dir.FullName;
        }

        throw new InvalidOperationException("Repository root (MainframeEngine.slnx) not found above the test output.");
    }

    [Theory]
    [InlineData("Mesh/Mesh.vk.frag.spv", new[] { 0, 1, 2 })]
    [InlineData("Mesh/MeshId.vk.frag.spv", new[] { 2, 3 })]
    [InlineData("Canvas/Canvas.vk.frag.spv", new[] { 0, 1 })]
    [InlineData("Mesh/Mesh.vk.vert.spv", new[] { 0, 1, 2, 3, 4, 5, 6 })] // vertex attributes; built-ins have no location
    public void InputLocationsOfCompiledShaders(string spv, int[] expected)
    {
        var locations = SpirvInputs.InputLocations(File.ReadAllBytes(Path.Combine(ShaderDir, spv)));
        Assert.Equal(expected, locations.Order());
    }

    [Theory]
    [InlineData("2026.1-52-gc8ddf20bb", true)]  // the Vulkan SDK's build
    [InlineData("v2026.19", true)]
    [InlineData("2027.0", true)]
    [InlineData("2025.24.3", false)]            // older than the minimum: the build keeps the committed SPIR-V
    [InlineData("slangc: unknown option", false)]
    [InlineData("", false)]
    public void SlangcVersionsBelowTheMinimumAreNotUsed(string versionOutput, bool supported) =>
        Assert.Equal(supported, CanvasShaderBuild.IsSupportedSlangcVersion(versionOutput));

    [Fact]
    public void NotSpirvIsRejected() =>
        Assert.Throws<FormatException>(() => SpirvInputs.InputLocations(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
}

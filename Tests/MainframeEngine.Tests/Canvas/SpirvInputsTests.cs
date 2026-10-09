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
    [InlineData("Mesh/Mesh.vk.frag.spv", new[] { 0, 1, 2, 3 })] // 3: the vertex colour (ADR 0151)
    [InlineData("Foliage/Foliage.vk.frag.spv", new[] { 0, 1, 2, 3, 4, 5 })] // 5: instance distance and height (ADR 0172)
    [InlineData("Mesh/MeshExt.vk.vert.spv", new[] { 0, 1, 2, 3, 4, 5, 6, 8 })]
    [InlineData("Foliage/Foliage.vk.vert.spv", new[] { 0, 1, 2, 3, 4, 5, 6, 8, 9, 14, 15 })] // 14–15: the wind pivots (ADR 0172)
    [InlineData("Shadows/Shadow2DFoliageInstanced.vk.vert.spv", new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8 })]
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

    // A minimal module: header, then OpDecorate/OpVariable instructions (ids only; types are not needed by the reader).
    private static byte[] Module(params uint[][] instructions)
    {
        var words = new List<uint> { 0x07230203, 0x00010500, 0, 100, 0 };
        foreach (var operands in instructions)
            words.AddRange(operands);
        return words.SelectMany(BitConverter.GetBytes).ToArray();
    }

    private static uint[] Decorate(uint id, uint decoration, uint value) => [(4u << 16) | 71, id, decoration, value];
    private static uint[] InputVariable(uint id) => [(4u << 16) | 59, 1, id, 1];

    [Fact]
    public void BuiltInInputsHaveNoLocation()
    {
        var spirv = Module(Decorate(10, 30, 3), InputVariable(10), Decorate(11, 11, 15), InputVariable(11)); // 11: BuiltIn FragCoord
        Assert.Equal([3], SpirvInputs.InputLocations(spirv));
    }

    [Fact]
    public void AnInputWithNeitherLocationNorBuiltInIsRejected()
    {
        // Block-typed stage input (member Locations): the reader cannot tell which locations it uses, so it must not
        // silently report none (the vertex stage would then write nothing).
        var spirv = Module(Decorate(10, 30, 0), InputVariable(10), InputVariable(12));
        Assert.Throws<FormatException>(() => SpirvInputs.InputLocations(spirv));
    }

    [Fact]
    public void CanvasShadersCompileWithTheEngineFlagsGlslSyntaxAndNoNames()
    {
        // -obfuscate drops every OpName: MoltenVK's SPIRV-Cross would otherwise carry a user's local named like a Metal
        // keyword (vertex, device, …) into the Metal source, which then fails to compile.
        Assert.Equal(
            ["in.slang", "-allow-glsl", "-obfuscate", "-target", "spirv", "-capability", "spirv_1_5", "-matrix-layout-row-major",
             "-entry", "main", "-stage", "fragment", "-I", "include", "-o", "out.spv"],
            CanvasShaderBuild.SlangcArguments("in.slang", "fragment", "include", "out.spv"));
    }

    [Fact]
    public void NotSpirvIsRejected() =>
        Assert.Throws<FormatException>(() => SpirvInputs.InputLocations(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
}

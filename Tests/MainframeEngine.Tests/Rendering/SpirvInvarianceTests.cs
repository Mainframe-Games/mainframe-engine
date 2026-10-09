using System.Buffers.Binary;

namespace MainframeEngine.Tests.Rendering;

/// <summary>ADR 0172: <see cref="SpirvInvariance"/> marks the foliage and impostor vertex positions invariant.</summary>
public sealed class SpirvInvarianceTests
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

    // (target, decoration) of every OpDecorate, and (struct, member, decoration) of every OpMemberDecorate.
    private static List<(uint Target, uint Member, uint Decoration, uint Value)> Decorations(byte[] code)
    {
        var list = new List<(uint, uint, uint, uint)>();
        for (var i = 20; i < code.Length;)
        {
            var word = BinaryPrimitives.ReadUInt32LittleEndian(code.AsSpan(i));
            int count = (int)(word >> 16), op = (int)(word & 0xFFFF);
            uint W(int k) => BinaryPrimitives.ReadUInt32LittleEndian(code.AsSpan(i + 4 * k));
            if (op == 71)
                list.Add((W(1), uint.MaxValue, W(2), count > 3 ? W(3) : 0));
            else if (op == 72)
                list.Add((W(1), W(2), W(3), count > 4 ? W(4) : 0));
            i += count * 4;
        }

        return list;
    }

    [Theory]
    [MemberData(nameof(InvariantShaders))]
    public void ThePositionOfEachListedShaderBecomesInvariant(string shader)
    {
        var code = File.ReadAllBytes(Path.Combine(ShaderDir, shader));
        Assert.True(SpirvInvariance.Applies("Content/Shaders/" + shader));
        Assert.DoesNotContain(Decorations(code), d => d.Decoration == 18);

        var patched = SpirvInvariance.MakePositionInvariant(code);
        var decorations = Decorations(patched);
        var position = Assert.Single(decorations, d => d.Decoration == 11 && d.Value == 0); // BuiltIn Position
        Assert.Contains(decorations, d => d.Target == position.Target && d.Member == position.Member && d.Decoration == 18);
        Assert.Equal(code.Length + (position.Member == uint.MaxValue ? 12 : 16), patched.Length);
        Assert.Same(patched, SpirvInvariance.MakePositionInvariant(patched)); // already invariant: unchanged
    }

    public static TheoryData<string> InvariantShaders() => [.. SpirvInvariance.Shaders];

    [Fact]
    public void OtherShadersAndModulesWithoutAPositionStayAsTheyAre()
    {
        Assert.False(SpirvInvariance.Applies("Content/Shaders/Mesh/Mesh.vk.vert.spv"));
        Assert.False(SpirvInvariance.Applies("Content/Shaders/Foliage/Foliage.vk.frag.spv"));
        var fragment = File.ReadAllBytes(Path.Combine(ShaderDir, "Foliage/Foliage.vk.frag.spv"));
        Assert.Same(fragment, SpirvInvariance.MakePositionInvariant(fragment));
        Assert.Throws<InvalidDataException>(() => SpirvInvariance.MakePositionInvariant(new byte[24]));
    }
}

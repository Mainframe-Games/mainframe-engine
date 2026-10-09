using System.Numerics;
using System.Text;
using System.Text.Json;
using MainframeEngine.Trees;

namespace MainframeEngine.Tests.Trees;

/// <summary>
/// The C# port against Ez Tree itself: for every preset and level of detail, the same vertex and index counts, the same
/// indices, positions and normals within 1e-4 (and, on the platforms checked so far, the same float32 values), and the
/// same skeleton. The fixture comes from <c>build/ez-tree-reference.mjs</c> (Ez Tree at the pinned commit).
/// </summary>
[Collection(nameof(Scene.SerialResources))]
public sealed class TreeParityTests
{
    private const double Tolerance = 1e-4;

    public static TheoryData<string> Presets() => [.. TreePresets.Names.Take(TreePresets.EzTreeCount)]; // Ez Tree's own (the fixture)

    [Fact]
    public void FixtureCoversEveryPresetAtThePin()
    {
        Assert.Equal("dcf309bd86bd521083d9c70f01f2de45fdc7c457", TreeFixture.Root.GetProperty("ezTree").GetString());
        Assert.Equal("0.167.1", TreeFixture.Root.GetProperty("three").GetString());
        var names = TreeFixture.Root.GetProperty("presets").EnumerateArray().Select(p => p.GetProperty("name").GetString()!).ToArray();
        Assert.Equal(TreePresets.Names.Take(TreePresets.EzTreeCount), names);
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void SkeletonMatchesEzTree(string name)
    {
        var fixture = TreeFixture.Preset(name);
        var parameters = TreeFixture.ParityParams(name);
        Assert.Equal(fixture.GetProperty("seed").GetInt32(), parameters.Seed);

        var skeleton = new TreeGenerator().GrowSkeleton(parameters, parameters.Seed);

        // Name the first diverging branch: everything after it differs too.
        var expected = fixture.GetProperty("skeleton").EnumerateArray().ToArray();
        var count = Math.Min(expected.Length, skeleton.BranchCount);
        for (var b = 0; b < count; b++)
        {
            var e = expected[b].EnumerateArray().Select(x => x.GetInt32()).ToArray();
            var branch = skeleton.Branches[b];
            var tip = skeleton.SectionsOf(branch)[^1].Origin;
            var got = new[] { branch.SectionCount + 1, (int)TreeFixture.Quantize(tip.X), (int)TreeFixture.Quantize(tip.Y), (int)TreeFixture.Quantize(tip.Z) };
            var same = got[0] == e[0] && Math.Abs(got[1] - e[1]) <= 1 && Math.Abs(got[2] - e[2]) <= 1 && Math.Abs(got[3] - e[3]) <= 1;
            Assert.True(same, $"{name}: branch {b} (level {branch.Level}) diverges: sections/tip {string.Join(", ", got)}, Ez Tree {string.Join(", ", e)}");
        }

        Assert.Equal(fixture.GetProperty("branches").GetInt32(), skeleton.BranchCount);
        Assert.Equal(fixture.GetProperty("leaves").GetInt32(), skeleton.LeafCount);
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void MeshesMatchEzTreeAtEveryLevelOfDetail(string name)
    {
        var fixture = TreeFixture.Preset(name);
        var stride = TreeFixture.Root.GetProperty("stride").GetInt32();
        var parameters = TreeFixture.ParityParams(name);
        var lods = new TreeGenerator().Generate(parameters);
        var expected = fixture.GetProperty("lods").EnumerateArray().ToArray();
        Assert.Equal(expected.Length, lods.Length);

        var hashMismatches = new StringBuilder();
        for (var lod = 0; lod < lods.Length; lod++)
        {
            CompareSurface($"{name} LOD{lod} bark", lods[lod].Bark, expected[lod].GetProperty("bark"), stride, hashMismatches);
            CompareSurface($"{name} LOD{lod} leaves", lods[lod].Leaves, expected[lod].GetProperty("leaves"), stride, hashMismatches);
        }

        // The rounded hashes compare every vertex, not just the samples. A value lying exactly on a rounding boundary
        // with a one-ulp transcendental difference could flip one; that has not happened on any platform so far.
        Assert.True(hashMismatches.Length == 0, hashMismatches.ToString());
    }

    private static void CompareSurface(string what, TreeSurfaceData surface, JsonElement expected, int stride, StringBuilder hashMismatches)
    {
        Assert.True(expected.GetProperty("vertices").GetInt32() == surface.VertexCount, $"{what}: {surface.VertexCount} vertices, Ez Tree {expected.GetProperty("vertices").GetInt32()}");
        Assert.True(expected.GetProperty("indices").GetInt32() == surface.Indices.Length, $"{what}: {surface.Indices.Length} indices, Ez Tree {expected.GetProperty("indices").GetInt32()}");
        Assert.True(expected.GetProperty("indexHash").GetUInt32() == TreeFixture.HashIndices(surface.Indices), $"{what}: the indices differ");

        var i = 0;
        foreach (var sample in expected.GetProperty("samples").EnumerateArray())
        {
            var v = i * stride;
            var e = sample.EnumerateArray().Select(x => x.GetDouble()).ToArray();
            var p = surface.Positions[v];
            var n = surface.Normals[v];
            var error = new[] { p.X - e[0], p.Y - e[1], p.Z - e[2], n.X - e[3], n.Y - e[4], n.Z - e[5] }.Max(Math.Abs);
            Assert.True(error <= Tolerance, $"{what}: vertex {v} is {p} / {n}, Ez Tree ({e[0]}, {e[1]}, {e[2]}) / ({e[3]}, {e[4]}, {e[5]})");
            i++;
        }

        Check("positions", TreeFixture.Hash(surface.Positions), expected.GetProperty("positionHash").GetUInt32());
        Check("normals", TreeFixture.Hash(surface.Normals), expected.GetProperty("normalHash").GetUInt32());
        Check("UVs", TreeFixture.Hash(surface.UVs), expected.GetProperty("uvHash").GetUInt32());

        void Check(string stream, uint got, uint want)
        {
            if (got != want)
                hashMismatches.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"{what}: the rounded {stream} hash is {got:x8}, Ez Tree {want:x8}");
        }
    }

    [Fact]
    public void TriangleCountsMatchTheProposalTable()
    {
        // docs/design/future/procedural-trees.md: Oak Medium has 13 806, 6 694 and 3 782 triangles at LOD0–2.
        var lods = new TreeGenerator().Generate(TreeFixture.ParityParams("Oak Medium"));
        Assert.Equal([13806, 6694, 3782], lods.Select(l => l.TriangleCount));
    }
}

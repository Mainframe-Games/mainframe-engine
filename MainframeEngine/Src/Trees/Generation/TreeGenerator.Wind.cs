using System.Numerics;

namespace MainframeEngine.Trees;

/// <summary>Pivot streams of the hierarchical wind (ADR 0172): engine extensions of the port, RNG-free.</summary>
public sealed partial class TreeGenerator
{
    /// <summary>Stiffness written for pivots that do not bend (the trunk's vertices): at or above the shader's kRigid.</summary>
    public const float RigidStiffness = 4096f;

    /// <summary>
    /// The pivot streams of every branch (two entries per branch: <c>Custom1</c>, <c>Custom2</c>): the object-space base of
    /// the branch's level-1 ancestor and its stiffness, then its level-2 ancestor's. Levels count branches that leave their
    /// parent: a deciduous branch's terminal continuation belongs to its parent's level (the trunk's top sways with the
    /// trunk, a branch's tip with the branch). Trunk vertices get rigid pivots (only the trunk sway moves them); level-1
    /// vertices a rigid second pivot at their first. Stiffness grows with a branch's base radius over its length.
    /// </summary>
    internal static Vector4[] BranchPivots(TreeSkeleton skeleton, TreeParams parameters)
    {
        var branches = skeleton.Branches;
        var effective = new int[branches.Count];
        var first = new int[branches.Count];
        var second = new int[branches.Count];
        var pivots = new Vector4[branches.Count * 2];
        var scale = parameters.Scale;
        for (var b = 0; b < branches.Count; b++)
        {
            var branch = branches[b];
            var parent = branch.Parent;
            if (parent < 0)
            {
                (effective[b], first[b], second[b]) = (0, -1, -1);
            }
            else if (branch.Terminal)
            {
                (effective[b], first[b], second[b]) = (effective[parent], first[parent], second[parent]);
            }
            else
            {
                effective[b] = effective[parent] + 1;
                first[b] = effective[b] == 1 ? b : first[parent];
                second[b] = effective[b] == 2 ? b : effective[b] > 2 ? second[parent] : -1;
            }

            var c1 = first[b] < 0 ? new Vector4(0f, 0f, 0f, RigidStiffness) : Pivot(skeleton, first[b], scale);
            var c2 = second[b] < 0 ? c1 with { W = RigidStiffness } : Pivot(skeleton, second[b], scale);
            pivots[2 * b] = c1;
            pivots[2 * b + 1] = c2;
        }

        return pivots;
    }

    private static Vector4 Pivot(TreeSkeleton skeleton, int branch, double scale)
    {
        var b = skeleton.Branches[branch];
        var origin = Scaled(skeleton.Sections[b.FirstSection].Origin, scale);
        var stiffness = (float)Math.Clamp(120 * b.BaseRadius / Math.Max(b.Length, 1e-6), 0.5, 40);
        return new Vector4(origin, stiffness);
    }

    // The bark's pivots: its vertices run branch by branch, rings × (sides + 1) each (MeshBark's order).
    private static TreeSurfaceData WithBarkPivots(TreeSurfaceData bark, TreeSkeleton skeleton, Vector4[] pivots, int sectionStride, double segmentFactor)
    {
        if (bark.VertexCount == 0)
            return bark;
        var custom1 = new Vector4[bark.VertexCount];
        var custom2 = new Vector4[bark.VertexCount];
        var vertex = 0;
        for (var b = 0; b < skeleton.Branches.Count; b++)
        {
            var branch = skeleton.Branches[b];
            var count = SampledRingCount(branch.SectionCount + 1, sectionStride) * (SegmentsFor(branch.SegmentCount, segmentFactor) + 1);
            custom1.AsSpan(vertex, count).Fill(pivots[2 * b]);
            custom2.AsSpan(vertex, count).Fill(pivots[2 * b + 1]);
            vertex += count;
        }

        return bark.WithWind(custom1, custom2);
    }

    // The leaves' (or cluster cards') pivots: each card's vertices take its branch's.
    private static TreeSurfaceData WithCardPivots(TreeSurfaceData leaves, List<int> cardBranches, int verticesPerCard, Vector4[] pivots)
    {
        if (leaves.VertexCount == 0)
            return leaves;
        var custom1 = new Vector4[leaves.VertexCount];
        var custom2 = new Vector4[leaves.VertexCount];
        for (var c = 0; c < cardBranches.Count; c++)
        {
            var b = Math.Max(cardBranches[c], 0);
            custom1.AsSpan(c * verticesPerCard, verticesPerCard).Fill(pivots[2 * b]);
            custom2.AsSpan(c * verticesPerCard, verticesPerCard).Fill(pivots[2 * b + 1]);
        }

        return leaves.WithWind(custom1, custom2);
    }
}

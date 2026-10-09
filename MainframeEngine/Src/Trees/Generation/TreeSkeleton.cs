namespace MainframeEngine.Trees;

/// <summary>
/// The grown shape of a tree (Ez Tree's <c>skeleton</c>): the section frames of every branch and the placement of every
/// leaf. <see cref="TreeGenerator.GrowSkeleton"/> fills it (all RNG use happens there); <see cref="TreeGenerator.Mesh"/>
/// meshes it any number of times. Reuse one between calls to avoid reallocating its lists.
/// </summary>
public sealed class TreeSkeleton
{
    internal readonly List<SkeletonSection> Sections = [];
    internal readonly List<SkeletonBranch> Branches = [];
    internal readonly List<SkeletonLeaf> Leaves = [];

    /// <summary>The seed it was grown from.</summary>
    public int Seed { get; internal set; }

    /// <summary>Branch levels of the parameters it was grown with.</summary>
    public int Levels { get; internal set; }

    public int BranchCount => Branches.Count;

    public int LeafCount => Leaves.Count;

    /// <summary>Highest section or leaf origin (Ez Tree units); at least a small positive value.</summary>
    public double Height { get; internal set; }

    /// <summary>Where the trunk's first side branch starts (0–1 along the trunk); 1 when it has none.</summary>
    internal double TrunkFirstChildStart { get; set; }

    /// <summary>Canopy ellipsoid around the leaf origins (centre and per-axis half extents, Ez Tree units).</summary>
    internal Vec3d CanopyCenter { get; set; }

    internal Vec3d CanopyExtent { get; set; }

    internal void Clear()
    {
        Sections.Clear();
        Branches.Clear();
        Leaves.Clear();
        Height = 0;
        TrunkFirstChildStart = 1;
        CanopyCenter = default;
        CanopyExtent = default;
    }

    /// <summary>The sections of <paramref name="branch"/> (sectionCount + 1 frames, base to tip).</summary>
    internal ReadOnlySpan<SkeletonSection> SectionsOf(in SkeletonBranch branch) =>
        System.Runtime.InteropServices.CollectionsMarshal.AsSpan(Sections).Slice(branch.FirstSection, branch.SectionCount + 1);
}

/// <summary>One section frame: Ez Tree's <c>{ origin, orientation, radius }</c>.</summary>
internal readonly record struct SkeletonSection(Vec3d Origin, EulerXyz Orientation, double Radius);

/// <summary>
/// One branch of the skeleton (Ez Tree's <c>{ sections, segmentCount, baseRadius }</c>) plus RNG-free engine data: the
/// level, the wind coordinate range (<see cref="WeightBase"/> at the base, <see cref="WeightTip"/> at the tip, in
/// "levels"), the phase, the parent and where it attaches.
/// </summary>
internal readonly record struct SkeletonBranch(
    int FirstSection,
    int SectionCount,
    int SegmentCount,
    double BaseRadius,
    int Level,
    double Length,
    double WeightBase,
    double WeightTip,
    float Phase,
    int Parent,
    double ParentStart,
    bool Terminal);

/// <summary>One leaf: Ez Tree's <c>{ origin, orientation, size }</c> plus its wind weight, phase and parent direction.</summary>
internal readonly record struct SkeletonLeaf(Vec3d Origin, EulerXyz Orientation, double Size, double Weight, float Phase, Vec3d BranchDirection);

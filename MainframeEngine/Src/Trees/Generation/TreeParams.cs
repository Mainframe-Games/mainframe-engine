namespace MainframeEngine.Trees;

/// <summary>Ez Tree's <c>TreeType</c>: deciduous trees continue every branch with a terminal branch; evergreens taper to a point.</summary>
public enum TreeType : byte
{
    Deciduous,
    Evergreen,
}

/// <summary>Ez Tree's <c>Billboard</c>: one leaf quad, or two crossed at 90°.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "Ez Tree's Billboard names ('single', 'double').")]
public enum TreeBillboard : byte
{
    Single,
    Double,
}

/// <summary>How a tree is meshed from its skeleton.</summary>
public enum TreeStyle : byte
{
    /// <summary>Ez Tree's meshing: smooth bark rings and alpha-cut leaf cards.</summary>
    Realistic,

    /// <summary>Faceted bark (split normals, fewer rings and sides) and low-poly leaf blobs, for untextured palette colours.</summary>
    LowPoly,
}

/// <summary>How the bark's V coordinate runs along a branch.</summary>
public enum BarkUvMode : byte
{
    /// <summary>V by arc length (square texels at the branch base), continuous from the parent branch.</summary>
    Continuous,

    /// <summary>Ez Tree's 0/1 ping-pong per ring (the texture mirrors at every ring): parity tests and imported looks.</summary>
    EzTree,
}

/// <summary>The shape of a LowPoly leaf blob.</summary>
public enum BlobShape : byte
{
    /// <summary>An icosphere (<see cref="TreeType.Evergreen"/> trees get <see cref="Cone"/> instead).</summary>
    Icosphere,

    /// <summary>An icosphere with a flat underside.</summary>
    Hemisphere,

    /// <summary>A cone leaning along its parent branches (evergreens).</summary>
    Cone,
}

/// <summary>
/// An extra growth force (the slot Ez Tree uses for its trellis, which is not ported). Called once per section after
/// the growth force, with the section's next origin and radius (Ez Units). Return false for no force; otherwise the
/// section turns towards <paramref name="direction"/> by up to <paramref name="strength"/> radians
/// (<c>setFromUnitVectors</c> + <c>rotateTowards</c>, as Ez Tree's trellis does).
/// </summary>
public interface ITreeGrowthForce
{
    bool TryGetForce(System.Numerics.Vector3 position, double radius, out System.Numerics.Vector3 direction, out double strength);
}

/// <summary>
/// The plain inputs of <see cref="TreeGenerator"/>: Ez Tree's options plus the engine's style settings. Every
/// generator input is a double, so values like <c>69.60000000000001</c> reach the port unchanged. Per-level values are
/// indexed by branch level 0–3 (Ez Tree's <c>branch.&lt;x&gt;[level]</c>; <c>Angle</c> and <c>Start</c> are unused at
/// level 0, <c>Children</c> at level 3). Build one with <c>TreeOptions.ToParams()</c> or <see cref="EzTreeDefaults"/>.
/// </summary>
public sealed class TreeParams
{
    /// <summary>Branch levels are 0 (trunk) to 3.</summary>
    public const int LevelCount = 4;

    public int Seed { get; set; }

    public TreeType Type { get; set; }

    /// <summary>Branch recursion levels: 0 = trunk only, at most 3.</summary>
    public int Levels { get; set; } = 3;

    public double[] Angle { get; set; } = [0, 70, 60, 60];

    public int[] Children { get; set; } = [7, 7, 5, 0];

    public double[] Gnarliness { get; set; } = [0.15, 0.2, 0.3, 0.02];

    public double[] Length { get; set; } = [20, 20, 10, 1];

    public double[] Radius { get; set; } = [1.5, 0.7, 0.7, 0.7];

    public int[] Sections { get; set; } = [12, 10, 8, 6];

    public int[] Segments { get; set; } = [8, 6, 4, 3];

    public double[] Start { get; set; } = [0, 0.4, 0.3, 0.3];

    public double[] Taper { get; set; } = [0.7, 0.7, 0.7, 0.7];

    public double[] Twist { get; set; } = [0, 0, 0, 0];

    /// <summary><c>branch.force.direction</c> (normalized on use).</summary>
    public System.Numerics.Vector3 GrowthDirection { get; set; } = System.Numerics.Vector3.UnitY;

    /// <summary><c>branch.force.strength</c>.</summary>
    public double GrowthForce { get; set; } = 0.01;

    /// <summary>The trellis slot (not ported); null in every preset.</summary>
    public ITreeGrowthForce? ExtraForce { get; set; }

    /// <summary><c>bark.textureScale.x</c>: texture wraps around a branch per unit of base radius.</summary>
    public double BarkTextureScaleX { get; set; } = 1;

    public BarkUvMode BarkUv { get; set; }

    public TreeBillboard LeafBillboard { get; set; } = TreeBillboard.Double;

    /// <summary>Leaf angle to the parent branch, degrees.</summary>
    public double LeafAngle { get; set; } = 10;

    /// <summary>Leaves per last-level branch.</summary>
    public int LeafCount { get; set; } = 1;

    /// <summary>Where leaves start along a branch (0–1).</summary>
    public double LeafStart { get; set; }

    public double LeafSize { get; set; } = 2.5;

    public double LeafSizeVariance { get; set; } = 0.7;

    /// <summary>Leaf normals bend outward from the leaf origin (a rounded canopy).</summary>
    public bool LeafRoundedNormals { get; set; } = true;

    /// <summary>Ez Tree units to engine units; multiplies positions after meshing (never the skeleton).</summary>
    public double Scale { get; set; } = 0.3;

    public int LowPolySectionStride { get; set; } = 2;

    public double LowPolySegmentFactor { get; set; } = 0.6;

    /// <summary>LowPoly drops branches thinner than this (Ez Tree units): they would hide inside the blobs.</summary>
    public double LowPolyMinBranchRadius { get; set; } = 0.15;

    public BlobShape BlobShape { get; set; }

    /// <summary>Icosphere subdivisions: 0 = 20 triangles, 1 = 80.</summary>
    public int BlobDetail { get; set; } = 1;

    /// <summary>Grid cell size (Ez Tree units) that seeds the blob clusters.</summary>
    public double BlobSize { get; set; } = 6;

    public int MaxBlobs { get; set; } = 24;

    /// <summary>Vertex jitter along the normal, as a fraction of the blob radius.</summary>
    public double BlobJitter { get; set; } = 0.15;

    /// <summary>Ez Tree's <c>new TreeOptions()</c> defaults, with the engine's defaults for the rest.</summary>
    public static TreeParams EzTreeDefaults() => new();

    /// <summary>Checks array lengths and ranges; throws <see cref="ArgumentException"/> describing the first problem.</summary>
    public void Validate()
    {
        if (Levels is < 0 or > LevelCount - 1)
            throw new ArgumentException($"Levels is {Levels}; it must be 0–{LevelCount - 1}.");
        Check(Angle, nameof(Angle));
        Check(Children, nameof(Children));
        Check(Gnarliness, nameof(Gnarliness));
        Check(Length, nameof(Length));
        Check(Radius, nameof(Radius));
        Check(Sections, nameof(Sections));
        Check(Segments, nameof(Segments));
        Check(Start, nameof(Start));
        Check(Taper, nameof(Taper));
        Check(Twist, nameof(Twist));
        for (var level = 0; level <= Levels; level++)
        {
            if (Sections[level] < 1)
                throw new ArgumentException($"Sections[{level}] is {Sections[level]}; a branch needs at least one section.");
            if (Segments[level] < 1)
                throw new ArgumentException($"Segments[{level}] is {Segments[level]}; a branch needs at least one segment.");
            if (level < Levels && Children[level] < 0)
                throw new ArgumentException($"Children[{level}] is negative.");
        }

        if (LeafCount < 0)
            throw new ArgumentException("LeafCount is negative.");
    }

    private static void Check<T>(T[]? values, string name)
    {
        if (values is null || values.Length != LevelCount)
            throw new ArgumentException($"{name} needs {LevelCount} values (levels 0–3), got {values?.Length ?? 0}.");
    }
}

/// <summary>
/// Ez Tree's <c>LODDetail</c> plus the style: how coarsely <see cref="TreeGenerator.Mesh"/> meshes a skeleton. Create
/// it with <c>new TreeMeshDetail { ... }</c> (the parameterless constructor sets full detail; <c>default</c> does not).
/// </summary>
public readonly record struct TreeMeshDetail
{
    public TreeMeshDetail()
    {
    }

    /// <summary>Keep every Nth section ring (and always the last).</summary>
    public int SectionStride { get; init; } = 1;

    /// <summary>Radial segments are <c>max(3, round(segments × factor))</c>.</summary>
    public double SegmentFactor { get; init; } = 1;

    /// <summary>Keep every Nth leaf.</summary>
    public int LeafStride { get; init; } = 1;

    /// <summary>Size multiplier for the kept leaves.</summary>
    public double LeafScale { get; init; } = 1;

    /// <summary>Overrides <see cref="TreeParams.LeafBillboard"/> for this level.</summary>
    public TreeBillboard? Billboard { get; init; }

    public TreeStyle Style { get; init; }

    /// <summary>LowPoly: icosphere subdivisions of the blobs.</summary>
    public int BlobDetail { get; init; } = 1;

    /// <summary>LowPoly: most blobs (0 = <see cref="TreeParams.MaxBlobs"/>).</summary>
    public int MaxBlobs { get; init; }
}

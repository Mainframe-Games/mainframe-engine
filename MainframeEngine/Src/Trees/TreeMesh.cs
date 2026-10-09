using MainframeEngine.Trees;

namespace MainframeEngine;

/// <summary>
/// A generated tree ready to draw: one <see cref="ArrayMesh"/> per level of detail (surface 0 bark, surface 1 leaves,
/// with the <c>Custom0</c> wind stream and, for LowPoly blobs, <c>Colors</c>), the trunk capsule, and what it was made
/// from (<see cref="Options"/>, <see cref="Seed"/>, <see cref="Style"/>), which picks its materials
/// (<see cref="TreeMaterials"/>). <see cref="Tree3D.Bake"/> saves one as <c>.mres</c>, so a shipped game loads the
/// meshes instead of running the generator (ADR 0158). The surfaces carry no materials: the tree applies them.
/// </summary>
[EditorIcon("package")]
public sealed class TreeMesh : Resource
{
    /// <summary>The levels of detail, finest first (three for the default details).</summary>
    [Export]
    public ArrayMesh[] Lods { get; set; } = [];

    /// <summary>The options the meshes were generated from (inline; their bark and leaf names pick the materials).</summary>
    [Export]
    public TreeOptions? Options { get; set; }

    [Export]
    public int Seed { get; set; }

    [Export]
    public TreeStyle Style { get; set; }

    /// <summary>Trunk capsule height (caps included, metres) from the ground to the first side branch.</summary>
    [Export]
    public float TrunkHeight { get; set; }

    /// <summary>Trunk radius 1 m above the ground (metres).</summary>
    [Export]
    public float TrunkRadius { get; set; }

    /// <summary><see cref="TreeGenerator.GeneratorVersion"/> at generation (a different current version marks a bake stale).</summary>
    [Export]
    public int GeneratorVersion { get; set; }

    public TreeTrunkCapsule Trunk => new(TrunkHeight, TrunkRadius);

    /// <summary>
    /// Generates every default level of detail of <paramref name="options"/> for <paramref name="seed"/> in
    /// <paramref name="style"/> (<see cref="TreeGenerator.Generate"/>; Oak Medium Realistic takes about 3–5 ms).
    /// Reuse <paramref name="generator"/> across calls on one thread.
    /// </summary>
    public static TreeMesh Generate(TreeOptions options, int seed, TreeStyle style, TreeGenerator? generator = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var parameters = options.ToParams();
        parameters.Seed = seed;
        var lods = (generator ?? new TreeGenerator()).Generate(parameters, style);
        var meshes = new ArrayMesh[lods.Length];
        for (var i = 0; i < lods.Length; i++)
            meshes[i] = lods[i].ToArrayMesh();
        var trunk = lods.Length > 0 ? lods[0].Trunk : default;
        return new TreeMesh
        {
            Lods = meshes,
            Options = options,
            Seed = seed,
            Style = style,
            TrunkHeight = trunk.Height,
            TrunkRadius = trunk.Radius,
            GeneratorVersion = TreeGenerator.GeneratorVersion,
        };
    }

    /// <summary>
    /// The visibility range of level <paramref name="lod"/> of <paramref name="count"/>: [0, <paramref name="lod1"/>),
    /// [<paramref name="lod1"/>, <paramref name="lod2"/>), [<paramref name="lod2"/>, <paramref name="max"/>) (0 = no
    /// upper bound), so exactly one level draws at any distance. Begin/End as
    /// <see cref="GeometryInstance3D.VisibilityRangeBegin"/>/<see cref="GeometryInstance3D.VisibilityRangeEnd"/>. The last
    /// level runs to <paramref name="max"/>; a level whose range is empty (a distance of 0, or levels past the third)
    /// gets (<see cref="float.MaxValue"/>, <see cref="float.MaxValue"/>): never drawn.
    /// </summary>
    public static (float Begin, float End) LodRange(int lod, int count, float lod1, float lod2, float max)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(lod);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(lod, count);
        var begin = lod == 0 ? 0f : lod == 1 ? lod1 : lod2;
        if (lod == count - 1)
            return (begin, max);
        var end = lod == 0 ? lod1 : lod2;
        return end > begin ? (begin, end) : (float.MaxValue, float.MaxValue);
    }
}

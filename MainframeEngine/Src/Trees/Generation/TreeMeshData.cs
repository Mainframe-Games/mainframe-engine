using System.Numerics;

namespace MainframeEngine.Trees;

/// <summary>
/// One surface of a generated tree as plain arrays (one entry per vertex, except <see cref="Indices"/>). Positions are in
/// engine units (Ez Tree units × <see cref="TreeParams.Scale"/>); UVs have their origin top-left.
/// </summary>
/// <remarks>
/// <see cref="Custom0"/> is the foliage vertex stream (<c>MeshSurface.Custom0</c>): x = wind weight (0 at the trunk base,
/// 1 at the twig tips), y = branch level / 4 (1 on leaves: flutter on), z = phase (0–1), w = ambient occlusion (0.4–1).
/// </remarks>
public sealed class TreeSurfaceData
{
    public static TreeSurfaceData Empty { get; } = new([], [], [], [], [], []);

    public TreeSurfaceData(Vector3[] positions, Vector3[] normals, Vector2[] uvs, Vector4[] custom0, Vector4[] colors, int[] indices)
    {
        Positions = positions;
        Normals = normals;
        UVs = uvs;
        Custom0 = custom0;
        Colors = colors;
        Indices = indices;
    }

    public Vector3[] Positions { get; }

    public Vector3[] Normals { get; }

    public Vector2[] UVs { get; }

    /// <summary>Wind weight, branch level, phase and AO; see the type remarks.</summary>
    public Vector4[] Custom0 { get; }

    /// <summary>
    /// Per-vertex colour multipliers (RGBA 0–1) or empty. LowPoly blobs vary their palette colour by ±8 % lightness per
    /// blob through it; Realistic surfaces leave it empty.
    /// </summary>
    public Vector4[] Colors { get; }

    /// <summary>Triangle list, counter-clockwise front faces.</summary>
    public int[] Indices { get; }

    public int VertexCount => Positions.Length;

    public int TriangleCount => Indices.Length / 3;
}

/// <summary>The bark and leaf surfaces of one level of detail.</summary>
public sealed class TreeMeshData
{
    public TreeMeshData(TreeSurfaceData bark, TreeSurfaceData leaves, double geometricError, TreeTrunkCapsule trunk)
    {
        Bark = bark;
        Leaves = leaves;
        GeometricError = geometricError;
        Trunk = trunk;
    }

    public TreeSurfaceData Bark { get; }

    public TreeSurfaceData Leaves { get; }

    /// <summary>
    /// How far (engine units) this level departs from full detail: for the bark, the largest distance between a dropped
    /// ring centre and its interpolation plus the radius lost to fewer sides; for leaves, the mean spacing of the dropped
    /// leaves (cards) or the blob coarsening (LowPoly). 0 at full detail. For screen-space LOD selection.
    /// </summary>
    public double GeometricError { get; }

    /// <summary>The trunk's collision capsule (the same for every level).</summary>
    public TreeTrunkCapsule Trunk { get; }

    public int VertexCount => Bark.VertexCount + Leaves.VertexCount;

    public int TriangleCount => Bark.TriangleCount + Leaves.TriangleCount;
}

/// <summary>
/// The trunk as a vertical capsule standing on the tree's origin (engine units): <see cref="Height"/> includes the caps
/// (Godot's <c>CapsuleShape3D.Height</c>), so its centre is at <c>Height / 2</c>.
/// </summary>
/// <param name="Height">From the ground to the first side branch (at least <c>2 × Radius</c>).</param>
/// <param name="Radius">The trunk radius 1 m above the ground.</param>
public readonly record struct TreeTrunkCapsule(float Height, float Radius);

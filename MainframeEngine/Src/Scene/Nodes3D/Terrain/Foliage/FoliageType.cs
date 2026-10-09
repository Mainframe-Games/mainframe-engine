namespace MainframeEngine;

/// <summary>
/// One kind of terrain foliage (G8a scatter, ADR 0157): grass, ferns, small plants or pebbles, drawn as one
/// <see cref="MultiMesh"/> per terrain chunk (or per tile, see <see cref="Subdivisions"/>) by the terrain's
/// <see cref="TerrainFoliage3D"/>. List it in <see cref="TerrainData.FoliageTypes"/>.
/// </summary>
/// <remarks>
/// <para>Placement is deterministic: a jittered grid of <see cref="Density"/> points per square metre over the whole map,
/// each point's random values hashed from (<see cref="Seed"/>, type index, grid point), so the same data always gives
/// the same instances and a chunk rebuild never moves its neighbours'. A point grows with probability = the summed
/// weight of the <see cref="LayerMask"/> splat layers (Realistic; Faceted: whether its surface id is in the mask), and
/// never in water, above <see cref="SlopeMaxDegrees"/> or outside the height limits.</para>
/// <para>Distance: full density up to <see cref="CullDistance"/> − <see cref="ThinBand"/>, then instances drop out one
/// by one (a per-instance hash) until none are left at <see cref="CullDistance"/>.</para>
/// </remarks>
[EditorIcon("feather")]
public sealed class FoliageType : Resource
{
    private int _version;

    /// <summary>Changes whenever a property changes (the terrain rebuilds its foliage).</summary>
    public int Version => _version;

    /// <summary>What every instance draws (for example <see cref="GrassMesh.Clump"/>).</summary>
    [Export]
    public Mesh? Mesh { get; set => Set(ref field, value); }

    /// <summary>Overrides the mesh's surface materials (usually a <see cref="FoliageMaterial3D"/>); null: the mesh's.</summary>
    [Export]
    public Material? Material { get; set => Set(ref field, value); }

    /// <summary>Instances per square metre at full weight (the grid spacing is 1 / √density).</summary>
    [ExportGroup("Placement")]
    [Export(Range = "0,64,0.01")]
    public float Density { get; set => Set(ref field, value); } = 1f;

    /// <summary>How far each point moves inside its grid cell (0: a regular grid, 1: anywhere in the cell).</summary>
    [Export(Range = "0,1,0.01")]
    public float Jitter { get; set => Set(ref field, value); } = 1f;

    /// <summary>Uniform scale range.</summary>
    [Export(Range = "0.01,10,0.01")]
    public float ScaleMin { get; set => Set(ref field, value); } = 0.8f;

    [Export(Range = "0.01,10,0.01")]
    public float ScaleMax { get; set => Set(ref field, value); } = 1.2f;

    /// <summary>A random rotation about the up axis.</summary>
    [Export]
    public bool RandomYaw { get; set => Set(ref field, value); } = true;

    /// <summary>How much instances tilt from up towards the ground's smooth normal (0 upright, 1 fully aligned).</summary>
    [Export(Range = "0,1,0.01")]
    public float AlignToNormal { get; set => Set(ref field, value); } = 0.3f;

    /// <summary>Metres the base is pushed into the ground (times the instance scale), so it never floats on slopes.</summary>
    [Export(Range = "0,2,0.01")]
    public float SinkMeters { get; set => Set(ref field, value); } = 0.03f;

    /// <summary>Seeds the hashes; change it to get a different arrangement.</summary>
    [Export]
    public int Seed { get; set => Set(ref field, value); }

    /// <summary>
    /// Splat layers (Realistic, bits 0–7) whose summed weight is the growth probability, or surface ids (Faceted) where it
    /// grows; 0 grows everywhere.
    /// </summary>
    [ExportGroup("Limits")]
    [Export]
    public uint LayerMask { get; set => Set(ref field, value); }

    /// <summary>Steepest ground it grows on, in degrees (it thins out over the last 5°).</summary>
    [Export(Range = "0,90,0.5")]
    public float SlopeMaxDegrees { get; set => Set(ref field, value); } = 40f;

    /// <summary>Lowest ground height (terrain-local, metres) it grows at.</summary>
    [Export]
    public float HeightMin { get; set => Set(ref field, value); } = -100000f;

    /// <summary>Highest ground height (terrain-local, metres) it grows at.</summary>
    [Export]
    public float HeightMax { get; set => Set(ref field, value); } = 100000f;

    /// <summary>Distance from the camera (to a chunk's centre) beyond which nothing is drawn.</summary>
    [ExportGroup("Distance")]
    [Export(Range = "1,2000,1")]
    public float CullDistance { get; set => Set(ref field, value); } = 60f;

    /// <summary>The band before <see cref="CullDistance"/> over which instances drop out one by one.</summary>
    [Export(Range = "0,2000,1")]
    public float ThinBand { get; set => Set(ref field, value); } = 25f;

    /// <summary>
    /// Tiles per terrain chunk side (1, 2, 4 …): one <see cref="MultiMeshInstance3D"/> per tile. Smaller tiles follow the
    /// thinning more closely at the cost of more draws.
    /// </summary>
    [Export(Range = "1,8,1")]
    public int Subdivisions { get; set => Set(ref field, value); } = 1;

    /// <summary>Whether instances cast shadows (off for grass).</summary>
    [ExportGroup("Rendering")]
    [Export]
    public bool CastShadows { get; set => Set(ref field, value); }

    private void Set<T>(ref T storage, T value)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value))
            return;
        storage = value;
        _version++;
        EmitChanged();
    }
}

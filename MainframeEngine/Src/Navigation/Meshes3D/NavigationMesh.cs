using System.Numerics;

namespace MainframeEngine;

/// <summary>How Recast splits the walkable area into regions before polygons (Godot's <c>NavigationMesh.SamplePartitionType</c>).</summary>
public enum NavigationMeshPartition : byte
{
    /// <summary>The best tessellation; the slowest (Godot's default).</summary>
    Watershed,

    /// <summary>The fastest; long thin polygons.</summary>
    Monotone,

    /// <summary>Between the two; good for tiles with many small obstacles.</summary>
    Layers,
}

/// <summary>
/// G3.12: a 3D navigation mesh (Godot's <c>NavigationMesh</c>, inline or a <c>.mfres</c>) baked by
/// <see cref="NavigationRegion3D.Bake"/> with Recast (DotRecast, ADR 0264): <see cref="Vertices"/> and the convex
/// <see cref="Polygons"/> over them (region-local metres; for the debug draw and tools) and <see cref="DetourData"/>, the
/// Detour tile queries run on. The bake settings are Godot's names and defaults (metres and degrees). Changing a setting
/// does not re-bake: bake again.
/// </summary>
[EditorIcon("vector-triangle", Family = EditorIconFamily.Resource)]
public sealed class NavigationMesh : Resource
{
    private Vector3[] _vertices = [];
    private int[][] _polygons = [];
    private string _detourData = "";

    /// <summary>The baked polygons' vertices (region-local metres).</summary>
    [Export]
    public Vector3[] Vertices
    {
        get => _vertices;
        set
        {
            _vertices = value ?? [];
            Touch();
        }
    }

    /// <summary>The baked convex polygons, as indices into <see cref="Vertices"/>.</summary>
    [Export]
    public int[][] Polygons
    {
        get => _polygons;
        set
        {
            _polygons = value ?? [];
            Touch();
        }
    }

    /// <summary>The Detour tile (<c>dtmd1:</c> + base64 of DotRecast's mesh data); empty: not baked.</summary>
    [Export]
    public string DetourData
    {
        get => _detourData;
        set
        {
            _detourData = value ?? "";
            Touch();
        }
    }

    /// <summary>The voxel size across (metres; Godot's 0.25). Smaller is more precise and slower to bake.</summary>
    [ExportGroup("Cells")]
    [Export(Range = "0.01,10,0.01")]
    public float CellSize { get; set; } = 0.25f;

    /// <summary>The voxel height (metres; Godot's 0.25).</summary>
    [Export(Range = "0.01,10,0.01")]
    public float CellHeight { get; set; } = 0.25f;

    /// <summary>Agents need this much headroom (metres; Godot's 1.5).</summary>
    [ExportGroup("Agents")]
    [Export(Range = "0.01,50,0.01")]
    public float AgentHeight { get; set; } = 1.5f;

    /// <summary>The mesh keeps this far from walls and edges (metres; Godot's 0.5).</summary>
    [Export(Range = "0,50,0.01")]
    public float AgentRadius { get; set; } = 0.5f;

    /// <summary>Steps up to this height are walkable (metres; Godot's 0.25).</summary>
    [Export(Range = "0,50,0.01")]
    public float AgentMaxClimb { get; set; } = 0.25f;

    /// <summary>Slopes up to this angle are walkable (degrees; Godot's 45).</summary>
    [Export(Range = "0,90,0.1")]
    public float AgentMaxSlope { get; set; } = 45f;

    /// <summary>How the walkable area is split into regions.</summary>
    [ExportGroup("Regions")]
    [Export]
    public NavigationMeshPartition SamplePartitionType { get; set; }

    /// <summary>Regions smaller than this many cells² are dropped (Godot's 2).</summary>
    [Export(Range = "0,150,1")]
    public float RegionMinSize { get; set; } = 2f;

    /// <summary>Regions smaller than this many cells² merge into neighbours (Godot's 20).</summary>
    [Export(Range = "0,150,1")]
    public float RegionMergeSize { get; set; } = 20f;

    /// <summary>The longest polygon edge along the border (metres; Godot's 0: unlimited).</summary>
    [ExportGroup("Edges")]
    [Export(Range = "0,50,0.01")]
    public float EdgeMaxLength { get; set; }

    /// <summary>How far the simplified border may stray from the voxel outline (cells; Godot's 1.3).</summary>
    [Export(Range = "0.1,3,0.01")]
    public float EdgeMaxError { get; set; } = 1.3f;

    /// <summary>The most vertices a polygon may have (3–6; Godot's 6).</summary>
    [ExportGroup("Polygons")]
    [Export(Range = "3,6,1")]
    public int VerticesPerPolygon { get; set; } = 6;

    /// <summary>Detail mesh sampling distance (cells; Godot's 6).</summary>
    [ExportGroup("Details")]
    [Export(Range = "0,16,0.01")]
    public float DetailSampleDistance { get; set; } = 6f;

    /// <summary>Detail mesh maximum height error (cell heights; Godot's 1).</summary>
    [Export(Range = "0,16,0.01")]
    public float DetailSampleMaxError { get; set; } = 1f;

    /// <summary>What the bake parses: visible meshes, static collision or both (Godot's default: both).</summary>
    [ExportGroup("Geometry")]
    [Export]
    public NavigationParsedGeometryType ParsedGeometryType { get; set; } = NavigationParsedGeometryType.Both;

    /// <summary>The physics layers whose static bodies are parsed.</summary>
    [Export(Flags = true)]
    public uint CollisionMask { get; set; } = 0xFFFF_FFFF;

    /// <summary>Which nodes are parsed.</summary>
    [Export]
    public NavigationSourceGeometryMode SourceGeometryMode { get; set; }

    /// <summary>The group the group modes parse.</summary>
    [Export]
    public string SourceGroupName { get; set; } = "navigation_mesh_source_group";

    /// <summary>Spans that are walkable only because an agent can climb onto them stay walkable (Godot's off).</summary>
    [ExportGroup("Filters")]
    [Export]
    public bool FilterLowHangingObstacles { get; set; }

    /// <summary>Spans at ledges are not walkable (Godot's off).</summary>
    [Export]
    public bool FilterLedgeSpans { get; set; }

    /// <summary>Spans with less headroom than the agent height are not walkable (Godot's off).</summary>
    [Export]
    public bool FilterWalkableLowHeightSpans { get; set; }

    /// <summary>When it has a volume, only the part of the geometry inside this box (region-local) is baked.</summary>
    [Export]
    public Aabb FilterBakingAabb { get; set; }

    /// <summary>Bumped by every change of the baked data (regions re-read it at the next map sync).</summary>
    public int Version { get; private set; }

    public int PolygonCount => _polygons.Length;

    /// <summary>Whether there is a Detour tile to query.</summary>
    public bool IsBaked => _detourData.Length > 0;

    /// <summary>Clears the baked data (the settings stay).</summary>
    public void Clear() => SetBaked([], [], "");

    /// <summary>Replaces the baked data at once (one change for the regions).</summary>
    public void SetBaked(Vector3[] vertices, int[][] polygons, string detourData)
    {
        _vertices = vertices ?? [];
        _polygons = polygons ?? [];
        _detourData = detourData ?? "";
        Touch();
    }

    private void Touch()
    {
        Version++;
        EmitChanged();
    }
}

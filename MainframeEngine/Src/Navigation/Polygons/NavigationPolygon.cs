using System.Numerics;

namespace MainframeEngine;

/// <summary>What a navigation polygon bake reads as obstructions (Godot's <c>NavigationPolygon.ParsedGeometryType</c>).</summary>
public enum NavigationParsedGeometryType : byte
{
    /// <summary>Visible geometry: <see cref="Polygon2D"/> nodes.</summary>
    MeshInstances,

    /// <summary>Collision: <see cref="StaticBody2D"/> shapes and tile map layers' collision polygons on <see cref="NavigationPolygon.ParsedCollisionMask"/>.</summary>
    StaticColliders,

    /// <summary>Both (Godot's default).</summary>
    Both,
}

/// <summary>Which nodes a navigation polygon bake parses (Godot's <c>NavigationPolygon.SourceGeometryMode</c>).</summary>
public enum NavigationSourceGeometryMode : byte
{
    /// <summary>The region node's descendants.</summary>
    RootNodeChildren,

    /// <summary>The nodes in <see cref="NavigationPolygon.SourceGeometryGroupName"/> and their descendants.</summary>
    GroupsWithChildren,

    /// <summary>Only the nodes in <see cref="NavigationPolygon.SourceGeometryGroupName"/>.</summary>
    GroupsExplicit,
}

/// <summary>
/// G3.9: a 2D navigation mesh (Godot's <c>NavigationPolygon</c>, inline or a <c>.mfres</c>): <see cref="Vertices"/> and the
/// convex <see cref="Polygons"/> over them that a <see cref="NavigationRegion2D"/> puts on its map, plus the bake's input
/// — the traversable <see cref="Outlines"/> (or <see cref="BakingRect"/>), the agent radius and what to parse as
/// obstructions. <see cref="NavigationRegion2D.Bake"/> fills the polygons from the scene; <see cref="MakePolygonsFromOutlines"/>
/// from the outlines alone. Positions are in the region's local pixels. Polygons may be wound either way, but must be
/// convex; two polygons that share an edge (both of its vertices) are connected.
/// </summary>
[EditorIcon("polygon", Family = EditorIconFamily.Resource)]
public sealed class NavigationPolygon : Resource
{
    private Vector2[] _vertices = [];
    private int[][] _polygons = [];
    private Vector2[][] _outlines = [];

    /// <summary>The mesh's vertices (local pixels).</summary>
    [Export]
    public Vector2[] Vertices
    {
        get => _vertices;
        set
        {
            _vertices = value ?? [];
            Touch();
        }
    }

    /// <summary>The convex polygons, as indices into <see cref="Vertices"/>.</summary>
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

    /// <summary>The traversable outlines the bake starts from (local pixels; an outline inside another is a hole).</summary>
    [Export]
    public Vector2[][] Outlines
    {
        get => _outlines;
        set
        {
            _outlines = value ?? [];
            Touch();
        }
    }

    /// <summary>The bake keeps polygons this far from obstructions and from the outlines' edges (Godot's 10 px).</summary>
    [ExportGroup("Agents")]
    [Export(Range = "0,500,0.5")]
    public float AgentRadius { get; set; } = 10f;

    /// <summary>The bake's vertex grid (pixels; Godot's 1). Maps should use the same <see cref="NavigationMap2D.CellSize"/>.</summary>
    [ExportGroup("Cells")]
    [Export(Range = "0.01,64,0.01")]
    public float CellSize
    {
        get;
        set
        {
            if (!(value > 0f) || !float.IsFinite(value))
                throw new ArgumentOutOfRangeException(nameof(value), "The cell size must be positive.");
            field = value;
        }
    } = 1f;

    /// <summary>What the bake parses as obstructions.</summary>
    [ExportGroup("Geometry")]
    [Export]
    public NavigationParsedGeometryType ParsedGeometryType { get; set; } = NavigationParsedGeometryType.Both;

    /// <summary>The physics layers whose static bodies and tile collision are obstructions.</summary>
    [Export(Flags = true)]
    public uint ParsedCollisionMask { get; set; } = 0xFFFF_FFFF;

    /// <summary>Which nodes are parsed.</summary>
    [Export]
    public NavigationSourceGeometryMode SourceGeometryMode { get; set; }

    /// <summary>The group the group modes parse.</summary>
    [Export]
    public string SourceGeometryGroupName { get; set; } = "navigation_polygon_source_geometry_group";

    /// <summary>
    /// When it has an area: the bake clips the outlines to this rectangle (local pixels), and with no outlines it is the
    /// traversable area itself — the quickest way to bake a level: a rectangle round it, the walls carved out.
    /// </summary>
    [ExportGroup("Filters")]
    [Export]
    public Rect2 BakingRect { get; set; }

    /// <summary>Bumped by every change of the vertices, polygons or outlines (regions re-read the mesh at the next map sync).</summary>
    public int Version { get; private set; }

    public int PolygonCount => _polygons.Length;

    public int OutlineCount => _outlines.Length;

    /// <summary>The polygon's vertex indices.</summary>
    public int[] GetPolygon(int index) => _polygons[index];

    /// <summary>Adds a convex polygon (indices into <see cref="Vertices"/>).</summary>
    public void AddPolygon(int[] polygon)
    {
        ArgumentNullException.ThrowIfNull(polygon);
        _polygons = [.. _polygons, polygon];
        Touch();
    }

    public void ClearPolygons()
    {
        _polygons = [];
        Touch();
    }

    public Vector2[] GetOutline(int index) => _outlines[index];

    public void AddOutline(Vector2[] outline)
    {
        ArgumentNullException.ThrowIfNull(outline);
        _outlines = [.. _outlines, outline];
        Touch();
    }

    public void SetOutline(int index, Vector2[] outline)
    {
        ArgumentNullException.ThrowIfNull(outline);
        var copy = (Vector2[][])_outlines.Clone();
        copy[index] = outline;
        _outlines = copy;
        Touch();
    }

    public void RemoveOutline(int index)
    {
        var list = _outlines.ToList();
        list.RemoveAt(index);
        _outlines = [.. list];
        Touch();
    }

    public void ClearOutlines()
    {
        _outlines = [];
        Touch();
    }

    /// <summary>Clears the vertices and polygons (the outlines and settings stay).</summary>
    public void Clear()
    {
        _vertices = [];
        _polygons = [];
        Touch();
    }

    /// <summary>
    /// Fills <see cref="Vertices"/> and <see cref="Polygons"/> from <see cref="Outlines"/> (and <see cref="BakingRect"/>)
    /// alone, without the agent radius or any parsed geometry (Godot's <c>make_polygons_from_outlines</c>).
    /// </summary>
    public void MakePolygonsFromOutlines()
    {
        var (vertices, polygons) = NavigationPolygonBuilder.Build(_outlines, [], 0f, CellSize, BakingRect);
        SetMesh(vertices, polygons);
    }

    /// <summary>Replaces the mesh at once (one change for the regions).</summary>
    public void SetMesh(Vector2[] vertices, int[][] polygons)
    {
        _vertices = vertices ?? [];
        _polygons = polygons ?? [];
        Touch();
    }

    private void Touch()
    {
        Version++;
        EmitChanged();
    }
}

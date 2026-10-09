using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// Geometry made of one or more <see cref="MeshSurface"/>s (submeshes), each with its own default material
/// (Godot's <c>Mesh</c>). Drawn by <see cref="MeshInstance3D"/>; the render server uploads it once (device-local
/// vertex + index buffers) and shares it between every instance.
/// </summary>
/// <remarks>
/// <see cref="Version"/> changes whenever the geometry or a surface material changes; the renderer re-uploads
/// on the next frame. Subclasses: <see cref="ArrayMesh"/> (explicit surfaces, imported models) and the
/// <see cref="PrimitiveMesh"/> generators (<see cref="BoxMesh"/>, <see cref="SphereMesh"/>, ...).
/// </remarks>
[EditorIcon("vector-triangle")]
public abstract class Mesh : Resource
{
    private int _version = 1;
    private int _boundsVersion;
    private Aabb _bounds = Aabb.Empty;

    /// <summary>Number of surfaces (submeshes).</summary>
    public abstract int SurfaceCount { get; }

    /// <summary>Surface <paramref name="index"/> (0 .. <see cref="SurfaceCount"/> - 1).</summary>
    public abstract MeshSurface GetSurface(int index);

    /// <summary>The default material of surface <paramref name="index"/> (null: the engine default).</summary>
    public virtual Material? GetSurfaceMaterial(int index) => GetSurface(index).Material;

    /// <summary>Changes whenever the mesh content changes (geometry, surface list, surface materials).</summary>
    public virtual int Version => _version;

    /// <summary>Bounds of every surface in mesh space.</summary>
    public Aabb Bounds
    {
        get
        {
            var version = Version;
            if (_boundsVersion != version)
            {
                var bounds = Aabb.Empty;
                for (var i = 0; i < SurfaceCount; i++)
                    bounds = bounds.Merge(GetSurface(i).Bounds);
                _bounds = bounds;
                _boundsVersion = version;
            }

            return _bounds;
        }
    }

    /// <summary>Total vertex count of every surface.</summary>
    public int VertexCount
    {
        get
        {
            var count = 0;
            for (var i = 0; i < SurfaceCount; i++)
                count += GetSurface(i).VertexCount;
            return count;
        }
    }

    /// <summary>Marks the content changed (bumps <see cref="Version"/> and raises <see cref="Resource.Changed"/>).</summary>
    protected void Invalidate()
    {
        _version++;
        EmitChanged();
    }
}

/// <summary>
/// One submesh: indexed triangles with per-vertex position, normal and UV, and the material it is drawn with
/// unless an instance overrides it. Stored as plain arrays so meshes serialize as inline resources.
/// </summary>
/// <remarks>
/// Assign whole arrays (the setters bump <see cref="Version"/>); after editing an array in place call
/// <see cref="NotifyChanged"/>. Missing normals are generated (smooth, area-weighted) and missing UVs are zero
/// when the surface is uploaded.
/// </remarks>
[EditorIcon("polygon")]
public sealed class MeshSurface : Resource
{
    private Vector3[] _positions = [];
    private Vector3[] _normals = [];
    private Vector2[] _uvs = [];
    private int[] _indices = [];
    private Vector4[] _colors = [];
    private Vector4[] _custom0 = [];
    private Material? _material;
    private int _version = 1;
    private int _boundsVersion;
    private Aabb _bounds = Aabb.Empty;

    public MeshSurface()
    {
    }

    public MeshSurface(Vector3[] positions, Vector3[] normals, Vector2[] uvs, int[] indices, Material? material = null)
    {
        _positions = positions ?? throw new ArgumentNullException(nameof(positions));
        _normals = normals ?? throw new ArgumentNullException(nameof(normals));
        _uvs = uvs ?? throw new ArgumentNullException(nameof(uvs));
        _indices = indices ?? throw new ArgumentNullException(nameof(indices));
        _material = material;
    }

    [Export]
    public Vector3[] Positions { get => _positions; set { _positions = value ?? []; Touch(); } }

    /// <summary>Unit normals, one per position (empty: generated on upload).</summary>
    [Export]
    public Vector3[] Normals { get => _normals; set { _normals = value ?? []; Touch(); } }

    /// <summary>Texture coordinates, one per position, origin top-left (empty: all zero).</summary>
    [Export]
    public Vector2[] UVs { get => _uvs; set { _uvs = value ?? []; Touch(); } }

    /// <summary>Triangle list, counter-clockwise front faces (three indices per triangle).</summary>
    [Export]
    public int[] Indices { get => _indices; set { _indices = value ?? []; Touch(); } }

    /// <summary>
    /// Optional per-vertex colours (Godot's <c>ARRAY_COLOR</c>): RGBA 0..1, sRGB-authored, one per position (empty:
    /// none). Stored as RGBA8 in the second vertex stream; lit materials multiply their albedo by it.
    /// </summary>
    [Export]
    public Vector4[] Colors { get => _colors; set { _colors = value ?? []; Touch(); } }

    /// <summary>
    /// Optional per-vertex custom data (Godot's <c>ARRAY_CUSTOM0</c>), one per position (empty: none). Foliage reads
    /// x = wind weight, y = branch level / 4 (1 = leaf), z = wind phase 0..1, w = ambient occlusion.
    /// </summary>
    [Export]
    public Vector4[] Custom0 { get => _custom0; set { _custom0 = value ?? []; Touch(); } }

    /// <summary>True when the surface has <see cref="Colors"/> or <see cref="Custom0"/> (it uploads the second vertex stream).</summary>
    public bool HasVertexStreams => _colors.Length != 0 || _custom0.Length != 0;

    /// <summary>Material used when the instance has no override (null: the engine default).</summary>
    [Export]
    public Material? Material { get => _material; set { _material = value; Touch(); } }

    public int VertexCount => _positions.Length;
    public int IndexCount => _indices.Length;

    /// <summary>Changes whenever the surface changes.</summary>
    public int Version => _version;

    /// <summary>Bounds of <see cref="Positions"/>.</summary>
    public Aabb Bounds
    {
        get
        {
            if (_boundsVersion != _version)
            {
                _bounds = Aabb.FromPoints(_positions);
                _boundsVersion = _version;
            }

            return _bounds;
        }
    }

    /// <summary>Call after modifying the arrays in place.</summary>
    public void NotifyChanged() => Touch();

    /// <summary>
    /// Checks the arrays are consistent: indices in range and a multiple of three, normals/UVs empty or one per
    /// position. Throws <see cref="InvalidDataException"/> describing the first problem.
    /// </summary>
    public void Validate()
    {
        if (_indices.Length % 3 != 0)
            throw new InvalidDataException($"Surface has {_indices.Length} indices; triangles need a multiple of 3.");
        if (_normals.Length != 0 && _normals.Length != _positions.Length)
            throw new InvalidDataException($"Surface has {_normals.Length} normals for {_positions.Length} positions.");
        if (_uvs.Length != 0 && _uvs.Length != _positions.Length)
            throw new InvalidDataException($"Surface has {_uvs.Length} UVs for {_positions.Length} positions.");
        if (_colors.Length != 0 && _colors.Length != _positions.Length)
            throw new InvalidDataException($"Surface has {_colors.Length} colours for {_positions.Length} positions.");
        if (_custom0.Length != 0 && _custom0.Length != _positions.Length)
            throw new InvalidDataException($"Surface has {_custom0.Length} custom0 values for {_positions.Length} positions.");
        foreach (var index in _indices)
            if ((uint)index >= (uint)_positions.Length)
                throw new InvalidDataException($"Surface index {index} is out of range (vertex count {_positions.Length}).");
    }

    /// <summary>Writes interleaved <see cref="MeshVertex"/> data (generating missing normals).</summary>
    public void WriteVertices(Span<MeshVertex> destination)
    {
        if (destination.Length < _positions.Length)
            throw new ArgumentException("Destination is smaller than the vertex count.", nameof(destination));

        var normals = _normals;
        if (normals.Length != _positions.Length)
            normals = MeshGeometry.ComputeSmoothNormals(_positions, _indices);
        for (var i = 0; i < _positions.Length; i++)
            destination[i] = new MeshVertex(_positions[i], normals[i], _uvs.Length == _positions.Length ? _uvs[i] : default);
    }

    /// <summary>
    /// Writes the second vertex stream (<see cref="MeshVertexExt"/>): colours as RGBA8 (missing: opaque white) and
    /// custom0 (missing: zero).
    /// </summary>
    public void WriteVertexStreams(Span<MeshVertexExt> destination)
    {
        if (destination.Length < _positions.Length)
            throw new ArgumentException("Destination is smaller than the vertex count.", nameof(destination));
        var colors = _colors.Length == _positions.Length ? _colors : null;
        var custom = _custom0.Length == _positions.Length ? _custom0 : null;
        for (var i = 0; i < _positions.Length; i++)
            destination[i] = new MeshVertexExt(colors is null ? MeshVertexExt.White : MeshVertexExt.PackColor(colors[i]),
                custom is null ? default : custom[i]);
    }

    private void Touch()
    {
        _version++;
        EmitChanged();
    }
}

/// <summary>
/// A mesh built from explicit <see cref="MeshSurface"/>s (Godot's <c>ArrayMesh</c>): imported models, procedural
/// geometry. Serializes its surfaces inline.
/// </summary>
[EditorIcon("vector-triangle")]
public sealed class ArrayMesh : Mesh
{
    private List<MeshSurface> _surfaces = [];
    private int[] _seenSurfaceVersions = [];

    /// <summary>The surfaces. Use <see cref="AddSurface"/>/<see cref="ClearSurfaces"/>, or assign a new list.</summary>
    [Export]
    public List<MeshSurface> Surfaces
    {
        get => _surfaces;
        set
        {
            _surfaces = value ?? [];
            Invalidate();
        }
    }

    public override int SurfaceCount => _surfaces.Count;

    public override MeshSurface GetSurface(int index) => _surfaces[index];

    /// <summary>Content version: bumped by the mesh's own changes and whenever a surface's version moves.</summary>
    public override int Version
    {
        get
        {
            var changed = false;
            if (_seenSurfaceVersions.Length != _surfaces.Count)
            {
                _seenSurfaceVersions = new int[_surfaces.Count];
                changed = true;
            }

            for (var i = 0; i < _surfaces.Count; i++)
            {
                var v = _surfaces[i].Version;
                if (_seenSurfaceVersions[i] != v)
                {
                    _seenSurfaceVersions[i] = v;
                    changed = true;
                }
            }

            if (changed)
                Invalidate();
            return base.Version;
        }
    }

    /// <summary>Appends a surface and returns its index.</summary>
    public int AddSurface(MeshSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        _surfaces.Add(surface);
        Invalidate();
        return _surfaces.Count - 1;
    }

    /// <summary>Appends a surface built from arrays and returns its index.</summary>
    public int AddSurface(Vector3[] positions, Vector3[] normals, Vector2[] uvs, int[] indices, Material? material = null) =>
        AddSurface(new MeshSurface(positions, normals, uvs, indices, material));

    public void ClearSurfaces()
    {
        _surfaces.Clear();
        Invalidate();
    }
}

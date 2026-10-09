using System.Numerics;

namespace MainframeEngine;

/// <summary>The look a <see cref="TerrainData"/> is created for (fixed once created).</summary>
public enum TerrainProfile : byte
{
    /// <summary>Flat palette colours on 2 m facets, cells half a quad wide (low-poly games). No chunk LOD.</summary>
    Faceted,

    /// <summary>Smooth normals at 0.5–1 m spacing, geomipmapped chunk LOD with skirts, eight splat-weighted layers.</summary>
    Realistic,
}

/// <summary>Which diagonal splits each quad of the height grid.</summary>
public enum TerrainDiagonal : byte
{
    /// <summary>Diagonal A where <c>i + j</c> is even, B where it is odd: diamond facets, symmetric ramps.</summary>
    Checkerboard,

    /// <summary>Diagonal A (<c>(i+1, j)</c>–<c>(i, j+1)</c>) everywhere, as <see cref="HeightMapShape3D"/>.</summary>
    Uniform,
}

/// <summary>Terrain layers, as flags: what an edit touched or records.</summary>
[Flags]
public enum TerrainLayers : ushort
{
    None = 0,

    /// <summary>The height grid (<c>heightmap.png</c>).</summary>
    Height = 1,

    /// <summary>Faceted surface ids (<c>surface.png</c>) or Realistic splat weights (<c>splat-0.png</c>, <c>splat-1.png</c>).</summary>
    Surface = 2,

    /// <summary>Four game-defined channels per cell (<c>user.png</c>).</summary>
    User = 4,

    /// <summary>Foliage density (reserved: scatter is not built yet).</summary>
    Foliage = 8,

    /// <summary>Water depth per vertex (<c>water.png</c>).</summary>
    Water = 16,

    /// <summary>Scattered objects (reserved: scatter is not built yet).</summary>
    Objects = 32,
}

/// <summary>Which chunks of a <see cref="Terrain3D"/> get collision shapes.</summary>
public enum TerrainCollisionMode : byte
{
    /// <summary>Every chunk: one trimesh per chunk, built from its full-resolution triangles.</summary>
    All,

    /// <summary>
    /// Only chunks near moving bodies (large Realistic maps). Not built yet: behaves as <see cref="All"/> for now.
    /// </summary>
    NearBodies,

    /// <summary>No collision.</summary>
    None,
}

/// <summary>Where a <see cref="Terrain3D.Raycast"/> hit the ground (world space).</summary>
public readonly record struct TerrainHit(Vector3 Position, Vector3 Normal, float Distance);

/// <summary>
/// What a terrain edit changed: <paramref name="Layers"/>, the touched <paramref name="Cells"/> (cell coordinates; for
/// height and water edits the cells around the touched vertices) and <paramref name="Chunks"/> (chunk coordinates whose
/// meshes were rebuilt). <paramref name="FromUndo"/> is set when an undo or redo wrote the layers back.
/// </summary>
public readonly record struct TerrainChange(TerrainLayers Layers, Rect2I Cells, Rect2I Chunks, bool FromUndo);

/// <summary>Fills the eight splat weights of one cell centre (terrain-local metres) for <see cref="TerrainData.SetWeightsFrom"/>.</summary>
public delegate void TerrainWeightGenerator(float x, float z, Span<float> weights);

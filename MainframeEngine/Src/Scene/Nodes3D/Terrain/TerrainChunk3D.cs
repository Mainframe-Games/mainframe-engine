namespace MainframeEngine;

/// <summary>
/// One LOD level of one terrain chunk: an internal, unsaved child of a <see cref="Terrain3D"/> (no
/// <see cref="Node.Owner"/>, so the scene writer skips it and a viewport click selects the terrain). It draws its
/// <see cref="ArrayMesh"/> (chunk-local positions, smooth normals, UV = terrain-local XZ ÷ size) through the normal
/// mesh renderer with <see cref="Terrain3D.Material"/>; the terrain shows one level per chunk and hides the others.
/// </summary>
[EditorIcon("mountain")]
public sealed class TerrainChunk3D : GeometryInstance3D
{
    /// <summary>The terrain that owns the chunk (null for a detached chunk).</summary>
    public Terrain3D? Terrain { get; internal set; }

    /// <summary>Chunk coordinates in the terrain's chunk grid.</summary>
    public int ChunkX { get; internal set; }

    public int ChunkZ { get; internal set; }

    /// <summary>LOD level: vertices every <c>2^Lod</c> quads.</summary>
    public int Lod { get; internal set; }

    /// <summary>The level's mesh: grid triangles first, then the skirt.</summary>
    public ArrayMesh? Mesh { get; internal set; }

    internal override Mesh? GetRenderMesh() => Mesh;

    internal override Material GetRenderMaterial(Mesh mesh, int surface) =>
        MaterialOverride ?? Terrain?.Material ?? Terrain3D.DefaultMaterial;
}

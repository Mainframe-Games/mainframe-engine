using MainframeEngine.Trees;

namespace MainframeEngine;

/// <summary>Turns generated tree geometry into engine meshes.</summary>
public static class TreeMeshDataExtensions
{
    /// <summary>
    /// A <see cref="MeshSurface"/> from the surface's positions, normals, UVs and indices (the arrays are shared, not
    /// copied). <see cref="TreeSurfaceData.Custom0"/> and <see cref="TreeSurfaceData.Colors"/> are not carried over:
    /// they belong in the surface's optional vertex streams.
    /// </summary>
    public static MeshSurface ToSurface(this TreeSurfaceData data, Material? material = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        return new MeshSurface(data.Positions, data.Normals, data.UVs, data.Indices, material);
    }

    /// <summary>
    /// An <see cref="ArrayMesh"/> with the bark surface, then the leaf surface (see <see cref="ToSurface"/>); an empty
    /// surface (a tree without leaves) is left out.
    /// </summary>
    public static ArrayMesh ToArrayMesh(this TreeMeshData data, Material? barkMaterial = null, Material? leafMaterial = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        var mesh = new ArrayMesh();
        if (data.Bark.VertexCount > 0)
            mesh.AddSurface(data.Bark.ToSurface(barkMaterial));
        if (data.Leaves.VertexCount > 0)
            mesh.AddSurface(data.Leaves.ToSurface(leafMaterial));
        return mesh;
    }
}

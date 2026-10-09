namespace MainframeEngine;

/// <summary>
/// One level of detail of a <see cref="Tree3D"/>: an internal, unsaved child (no <see cref="Node.Owner"/>, so the scene
/// writer skips it and a viewport click selects the tree) drawing one <see cref="TreeMesh.Lods"/> mesh within its
/// visibility range, so exactly one level draws. Surface 0 (bark) and 1 (leaves) draw with the tree's materials unless
/// <see cref="GeometryInstance3D.MaterialOverride"/> or the mesh's own surface materials say otherwise.
/// </summary>
[EditorIcon("feather")]
public sealed class TreeLod3D : MeshInstance3D
{
    /// <summary>The tree that owns this level (null when detached).</summary>
    public Tree3D? TreeNode { get; internal set; }

    /// <summary>The level: 0 is the finest.</summary>
    public int Lod { get; internal set; }

    internal Material? BarkMaterial;
    internal Material? LeafMaterial;

    internal override Material GetRenderMaterial(Mesh mesh, int surface) =>
        TreeSurfaceMaterial(MaterialOverride, mesh, surface, BarkMaterial, LeafMaterial);

    /// <summary>
    /// The material of a tree surface: the override, else the mesh surface's own material (a hand-made bake), else the
    /// tree's bark (surface 0) or leaf (surface 1) material, else the default.
    /// </summary>
    internal static Material TreeSurfaceMaterial(Material? materialOverride, Mesh mesh, int surface, Material? bark, Material? leaves) =>
        materialOverride ?? mesh.GetSurfaceMaterial(surface) ?? (surface == 0 ? bark : leaves) ?? StandardMaterial3D.Default;
}

/// <summary>
/// One batch of a <see cref="TreeScatter"/>: the trees of one species variant in one chunk at one level of detail, as a
/// <see cref="MultiMeshInstance3D"/> whose visibility range is the level's. Internal and unsaved, like
/// <see cref="TreeLod3D"/>.
/// </summary>
[EditorIcon("stack-2")]
public sealed class TreeScatterBatch3D : MultiMeshInstance3D
{
    /// <summary>The chunk (scatter-local XZ ÷ <see cref="TreeScatter.ChunkSize"/>, floored).</summary>
    public Vector2I Chunk { get; internal set; }

    /// <summary>Index into <see cref="TreeScatter.Species"/>.</summary>
    public int Species { get; internal set; }

    /// <summary>The species variant (seed or baked mesh).</summary>
    public int Variant { get; internal set; }

    /// <summary>The level of detail: 0 is the finest.</summary>
    public int Lod { get; internal set; }

    /// <summary>Levels of the variant (the last one runs to <see cref="TreeScatter.MaxDistance"/>).</summary>
    public int LodCount { get; internal set; }

    internal Material? BarkMaterial;
    internal Material? LeafMaterial;

    internal override Material GetRenderMaterial(Mesh mesh, int surface) =>
        TreeLod3D.TreeSurfaceMaterial(MaterialOverride, mesh, surface, BarkMaterial, LeafMaterial);
}

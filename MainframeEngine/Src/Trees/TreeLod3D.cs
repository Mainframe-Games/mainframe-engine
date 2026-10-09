using System.Numerics;

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

    /// <summary>This level is the variant's octahedral impostor (ADR 0172; the last level).</summary>
    public bool IsImpostor { get; internal set; }

    /// <summary>The variant's last level is an impostor (<see cref="TreeScatter.ImpostorDistance"/>).</summary>
    public bool HasImpostorLevel { get; internal set; }

    internal Material? BarkMaterial;
    internal Material? LeafMaterial;

    // Per instance (TreeLodSelection.PerInstance, ADR 0172): the trees' origins (scatter-local) and the level's own range;
    // the batch draws while any of its trees is in the range widened by the margin (the vertex shader drops the others).
    internal Vector3[]? InstanceOrigins;
    internal float InstanceBegin, InstanceEnd, InstanceMargin;

    // ADR 0179: the coarse shadow level casts into the fine passes from here (camera distance, m), not from where it is
    // drawn: where the scatter's finer casting levels end. Negative: where it is drawn.
    internal float ShadowBegin = -1f;

    internal override bool HasShadowRange => ShadowBegin >= 0f;

    /// <summary>
    /// With a shadow range (the coarse shadow level, ADR 0179): true when one of the batch's trees (per instance) or the
    /// chunk's bounds centre (per chunk) lies at least <see cref="ShadowBegin"/> from the camera, where the finer levels
    /// stop casting; the vertex shader switches each tree at that distance exactly.
    /// </summary>
    internal override bool IsInShadowRange(Vector3 cameraPosition, in Aabb worldBounds)
    {
        if (ShadowBegin < 0f)
            return base.IsInShadowRange(cameraPosition, worldBounds);
        if (InstanceOrigins is not { } origins)
            return Vector3.Distance(cameraPosition, worldBounds.Center) >= ShadowBegin;
        var begin2 = ShadowBegin * ShadowBegin;
        var model = ModelMatrix;
        foreach (var origin in origins)
        {
            if (Vector3.DistanceSquared(cameraPosition, Vector3.Transform(origin, model)) >= begin2)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Per instance: true when one of the batch's trees lies within the level's range ± its fade margin (exact, so a chunk
    /// draws only the levels its trees use); per chunk: the bounds centre's distance against the visibility range. With
    /// the node's own range cleared (both 0: a prewarm showing everything, so its material and pipelines get created at
    /// load), it is drawn, as any node is (its material still drops the trees out of the level's range).
    /// </summary>
    public override bool IsInVisibilityRange(Vector3 cameraPosition, in Aabb worldBounds)
    {
        if (InstanceOrigins is not { } origins || (VisibilityRangeBegin <= 0f && VisibilityRangeEnd <= 0f))
            return base.IsInVisibilityRange(cameraPosition, worldBounds);
        if (InstanceBegin >= float.MaxValue)
            return false;
        var model = ModelMatrix;
        var near = InstanceBegin > 0f ? InstanceBegin - InstanceMargin : float.NegativeInfinity;
        var far = InstanceEnd > 0f ? InstanceEnd + InstanceMargin : float.PositiveInfinity;
        var near2 = near > 0f ? near * near : 0f;
        var far2 = far < float.PositiveInfinity ? far * far : float.PositiveInfinity;
        foreach (var origin in origins)
        {
            var d2 = Vector3.DistanceSquared(cameraPosition, Vector3.Transform(origin, model));
            if (d2 >= near2 && d2 < far2)
                return true;
        }

        return false;
    }

    internal override Material GetRenderMaterial(Mesh mesh, int surface) =>
        TreeLod3D.TreeSurfaceMaterial(MaterialOverride, mesh, surface, BarkMaterial, LeafMaterial);
}

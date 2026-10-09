namespace MainframeEngine;

/// <summary>
/// Draws a <see cref="MainframeEngine.MultiMesh"/> (Godot's <c>MultiMeshInstance3D</c>): every instance of its mesh, as
/// one render item frustum-culled by the bounds of all its instances, with the materials of the mesh's surfaces (or
/// <see cref="GeometryInstance3D.MaterialOverride"/>). Shadows follow <see cref="VisualInstance3D.CastShadows"/>;
/// picking selects this node. The instance buffer is uploaded when the transforms or this node's transform change,
/// never per frame otherwise.
/// </summary>
[EditorIcon("stack-2")]
public class MultiMeshInstance3D : GeometryInstance3D
{
    /// <summary>The instances to draw (Godot's <c>multimesh</c>).</summary>
    [Export]
    public MultiMesh? Multimesh { get; set; }

    internal override Mesh? GetRenderMesh() => Multimesh is { DrawnInstanceCount: > 0 } multimesh ? multimesh.Mesh : null;

    /// <summary>The GPU instance buffer (render thread; created by the mesh renderer).</summary>
    internal MultiMeshGpu? GpuMultiMesh;
}

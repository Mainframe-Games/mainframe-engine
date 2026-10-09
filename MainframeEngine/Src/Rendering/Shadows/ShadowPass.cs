using System.Numerics;

namespace MainframeEngine;

/// <summary>What a <see cref="ShadowPass"/> renders into.</summary>
public enum ShadowPassKind : byte
{
    /// <summary>One layer of the primary directional light's cascade array.</summary>
    Cascade,

    /// <summary>A tile of the shadow atlas (spot light or secondary directional light).</summary>
    AtlasTile,

    /// <summary>One face of a point light's cube map.</summary>
    CubeFace,

    /// <summary>The primary directional light's far shadow (ADR 0167): the layer after the cascades, rendered rarely.</summary>
    FarShadow,
}

/// <summary>
/// One shadow sub-pass of a frame, as planned by <see cref="ShadowSystem.RenderShadows{TState}(LightEnvironment, ICamera?, in Aabb, TState, ShadowCasterCull{TState}, ShadowCasterDraw{TState})"/>:
/// the light view-projection, its frustum (for caster culling) and where it renders.
/// </summary>
public readonly struct ShadowPass
{
    /// <summary>Index of the pass in the frame (also its light-matrix ring slot).</summary>
    public int Index { get; init; }

    public ShadowPassKind Kind { get; init; }

    /// <summary>Index of the light in its type's list of the <see cref="LightEnvironment"/> (the lights UBO order).</summary>
    public int LightIndex { get; init; }

    /// <summary>Cascade index, atlas map index or cube slot.</summary>
    public int Slot { get; init; }

    /// <summary>Cube face (0–5) for <see cref="ShadowPassKind.CubeFace"/>.</summary>
    public int Face { get; init; }

    /// <summary>World → light clip space (Vulkan depth [0, 1]).</summary>
    public Matrix4x4 ViewProjection { get; init; }

    /// <summary>Planes of <see cref="ViewProjection"/>: casters outside it cannot shadow anything this pass covers.</summary>
    public Frustum Frustum { get; init; }

    /// <summary>Viewport origin in the target (atlas tiles), in texels.</summary>
    public int X { get; init; }

    public int Y { get; init; }

    /// <summary>Width and height of the viewport in texels.</summary>
    public int Size { get; init; }

    /// <summary>World position of a point or spot light.</summary>
    public Vector3 LightPosition { get; init; }

    /// <summary>Range of a point or spot light.</summary>
    public float LightRange { get; init; }

    /// <summary>
    /// A coarse pass (ADR 0167): one of the primary light's last <see cref="DirectionalLight.CoarseCascades"/> cascades, or
    /// its far shadow. Instances whose <see cref="GeometryInstance3D.ShadowCasterLod"/> is <see cref="ShadowCasterLod.Fine"/>
    /// skip it; <see cref="ShadowCasterLod.Coarse"/> ones cast only into such passes.
    /// </summary>
    public bool Coarse { get; init; }

    /// <summary>Point-light passes write linear distance and use the point pipelines.</summary>
    public bool IsPoint => Kind == ShadowPassKind.CubeFace;
}

/// <summary>
/// Culls the casters of one pass before any pass is recorded (write per-pass instance data here). Return false when
/// nothing casts into the pass: the system then skips it and treats the map as fully lit.
/// </summary>
public delegate bool ShadowCasterCull<in TState>(TState state, in ShadowPass pass);

/// <summary>Records the casters of one pass (the pass's render pass, viewport and light matrix are bound).</summary>
public delegate void ShadowCasterDraw<in TState>(TState state, Silk.NET.Vulkan.CommandBuffer cb, in ShadowPass pass);

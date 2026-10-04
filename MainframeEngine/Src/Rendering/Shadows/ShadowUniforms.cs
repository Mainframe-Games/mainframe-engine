using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MainframeEngine;

/// <summary>How shadow maps are filtered in the lit shaders (<see cref="ShadowSystem.Filter"/>).</summary>
public enum ShadowFilter
{
    /// <summary>One hardware comparison tap (2×2 bilinear PCF where the depth format filters). Cube maps: one tap.</summary>
    Hard = 0,

    /// <summary>3×3 grid of bilinear comparison taps (a smooth 4×4-texel footprint). Cube maps: the 20-tap disc.</summary>
    Pcf3x3 = 1,

    /// <summary>16-tap Poisson disc of bilinear comparison taps (softest). Cube maps: the 20-tap disc.</summary>
    Poisson16 = 2,
}

/// <summary>One 2D shadow map (a cascade or an atlas tile) as the shaders see it: std140, 96 bytes (<c>ShadowMap2D</c>).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ShadowMapData
{
    /// <summary>World → light clip space.</summary>
    public Matrix4x4 ViewProjection;

    /// <summary>xy = offset, zw = size of the map in the sampled texture's UV space (cascades: 0, 0, 1, 1).</summary>
    public Vector4 Rect;

    /// <summary>
    /// x = texel world size (orthographic) or texel size per unit of distance (perspective), y = 1 for perspective
    /// (0 orthographic; −d an orthographic secondary directional map shadowing up to view depth d), z = depth bias
    /// (texels), w = normal bias (texels).
    /// </summary>
    public Vector4 Params;

    public const int Size = 96;
}

[InlineArray(ShaderLimits.MaxShadowCascades)]
internal struct ShadowCascadeArray
{
    private ShadowMapData _element;
}

[InlineArray(ShaderLimits.MaxShadowAtlasMaps)]
internal struct ShadowAtlasMapArray
{
    private ShadowMapData _element;
}

// ivecN arrays in std140 pad every element to 16 bytes: an ivec4[n] is 4n consecutive ints.
[InlineArray((ShaderLimits.MaxDirectionalLights + 3) / 4 * 4)]
internal struct ShadowDirCodes
{
    private int _element;
}

[InlineArray((ShaderLimits.MaxSpotLights + 3) / 4 * 4)]
internal struct ShadowSpotCodes
{
    private int _element;
}

[InlineArray((ShaderLimits.MaxPointLights + 3) / 4 * 4)]
internal struct ShadowPointCodes
{
    private int _element;
}

[InlineArray(ShaderLimits.MaxShadowPoint)]
internal struct ShadowPointParams
{
    private Vector4 _element;
}

/// <summary>
/// The shadow UBO (set 1, binding 0; <c>ShadowUBO</c> in <c>include/shadows.glsl</c>), std140. Codes map each light
/// of the lights UBO to its shadow: 0 = none.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ShadowUniforms
{
    public ShadowCascadeArray Cascades;
    public ShadowAtlasMapArray AtlasMaps;

    /// <summary>View depth where each cascade ends (the last = the shadow distance).</summary>
    public Vector4 CascadeSplits;

    /// <summary>1 when the cascade has casters (0: lit, its layer was not rendered).</summary>
    public Vector4 CascadeEnabled;

    /// <summary>x = cascade count (0 = no cascades), y = blend band (fraction of a cascade), z = shadow distance, w = 1 debug tint.</summary>
    public Vector4 Csm;

    /// <summary>x = <see cref="ShadowFilter"/>, y = filter radius (texels), z = 1 / cascade resolution, w = 1 / atlas size.</summary>
    public Vector4 Filter;

    /// <summary>Per directional light: 0 none, 1 the cascades, k + 2 atlas map k.</summary>
    public ShadowDirCodes DirCodes;

    /// <summary>Per spot light: 0 none, k + 1 atlas map k.</summary>
    public ShadowSpotCodes SpotCodes;

    /// <summary>Per point light: 0 none, c + 1 cube c.</summary>
    public ShadowPointCodes PointCodes;

    /// <summary>Per cube: x = texel size per unit of distance (2 / resolution), y = depth bias, z = normal bias (texels).</summary>
    public ShadowPointParams PointParams;

    /// <summary>Bytes in the std140 block.</summary>
    public static int Size => Unsafe.SizeOf<ShadowUniforms>();
}

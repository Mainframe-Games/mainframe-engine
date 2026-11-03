// The surface-shader contract for 3D meshes (ADR 0237, ADR 0205's passes): `import mainframe.spatial;` as the first
// statement of a game's .slang file. The engine generates every pass (colour, depth prepass, shadows, point shadows,
// object IDs) and calls the shader's optional functions in each:
//
//     void vertex(inout SurfaceVertex v[, Material m][, out Varyings o])
//     void fragment(inout SurfaceFragment s[, Material m][, Varyings i])
//
// `struct Material` (optional) holds the parameters, with their defaults as literal initializers, and the render modes
// as attributes on the struct; `struct Varyings` (optional) the values vertex passes to fragment. Matrices are the C#
// values: transform with mul(v, M), as in every engine shader. See docs/design/shaders.md#surface-shaders.
module mainframe_spatial;

__exported import mainframe.math;
__exported import mainframe.material;
__exported import mainframe.surface.spatial;

/// Which faces are drawn (`[cull(Cull.Disabled)]`). Disabled lights back faces with the flipped normal.
public enum Cull
{
    Back,
    Front,
    Disabled,
};

/// How the surface blends (`[blend(Blend.Add)]`). Any but Mix makes the surface blended.
public enum Blend
{
    Mix,
    Add,
    PremulAlpha,
};

/// Which surfaces write depth (`[depthDraw(DepthDraw.Always)]`). Opaque: opaque and cut-out ones; Never: none (no prepass).
public enum DepthDraw
{
    Opaque,
    Always,
    Never,
};

/// How the surface uses its alpha (`[alpha(Alpha.Cutout)]`; opaque without the attribute). Cutout discards fragments whose
/// alpha is below alphaScissorThreshold, and casts cut shadows; Blend blends them back to front, without shadows.
public enum Alpha
{
    Opaque,
    Cutout,
    Blend,
};

/// The lighting model (`[specular(Specular.BlinnPhong)]`): the engine's PBR, or its Blinn-Phong (shininess from roughness,
/// strength from specular).
public enum Specular
{
    Pbr,
    BlinnPhong,
};

/// Face culling.
[__AttributeUsage(_AttributeTargets.Struct)]
public struct cullAttribute
{
    Cull mode;
};

/// The blend mode.
[__AttributeUsage(_AttributeTargets.Struct)]
public struct blendAttribute
{
    Blend mode;
};

/// Depth writes.
[__AttributeUsage(_AttributeTargets.Struct)]
public struct depthDrawAttribute
{
    DepthDraw mode;
};

/// The alpha mode.
[__AttributeUsage(_AttributeTargets.Struct)]
public struct alphaAttribute
{
    Alpha mode;
};

/// The lighting model.
[__AttributeUsage(_AttributeTargets.Struct)]
public struct specularAttribute
{
    Specular model;
};

/// No depth test.
[__AttributeUsage(_AttributeTargets.Struct)]
public struct noDepthTestAttribute
{
};

/// albedo + emission, no lights.
[__AttributeUsage(_AttributeTargets.Struct)]
public struct unshadedAttribute
{
};

/// vertex() sees vertex and normal in world space instead of model space.
[__AttributeUsage(_AttributeTargets.Struct)]
public struct worldVertexCoordsAttribute
{
};

/// No fog.
[__AttributeUsage(_AttributeTargets.Struct)]
public struct noFogAttribute
{
};

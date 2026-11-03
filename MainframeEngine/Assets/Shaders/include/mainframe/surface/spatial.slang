// The built-in structs of spatial surface shaders (mainframe.spatial re-exports them; documented there). The engine's
// generated entries import this module alone: they include the engine's own constants (common.slang's PI) textually.
module mainframe_surface_spatial;

/// What vertex() reads and writes. Spaces are model space (world space with [worldVertexCoords]); in the shadow passes
/// "view space" is world space (viewMatrix is the identity).
public struct SurfaceVertex
{
    /// The position; write it to displace the vertex (in every pass, so shadows and depth follow).
    public float3 vertex;
    /// The normal.
    public float3 normal;
    /// The texture coordinate.
    public float2 uv;
    /// The second texture coordinate (meshes have one UV set: the same as uv).
    public float2 uv2;
    /// The vertex colour (white without one).
    public float4 color;
    /// The mesh's custom vertex data.
    public float4 custom0;
    /// Zero.
    public float4 custom1;
    /// Zero.
    public float4 custom2;
    /// Zero.
    public float4 custom3;
    /// A MultiMesh instance's index (zero otherwise).
    public int instanceId;
    /// A MultiMesh instance's custom data (zero otherwise).
    public float4 instanceCustom;
    /// The vertex's index.
    public int vertexId;
    /// Model → world.
    public float4x4 modelMatrix;
    /// Transforms normals model → world: mul(n, modelNormalMatrix).
    public float3x3 modelNormalMatrix;
    /// Model → view.
    public float4x4 modelViewMatrix;
    /// Transforms normals model → view.
    public float3x3 modelViewNormalMatrix;
    /// World → view.
    public float4x4 viewMatrix;
    /// View → world.
    public float4x4 invViewMatrix;
    /// The main camera's view → world (the shadow passes: the camera position, no rotation).
    public float4x4 mainCamInvViewMatrix;
    /// View → clip.
    public float4x4 projectionMatrix;
    /// Clip → view.
    public float4x4 invProjectionMatrix;
    /// The node's origin in world space.
    public float3 nodePositionWorld;
    /// The node's origin in view space.
    public float3 nodePositionView;
    /// The camera's position in world space.
    public float3 cameraPositionWorld;
    /// The camera's forward direction in world space.
    public float3 cameraDirectionWorld;
    /// The viewport's size in pixels.
    public float2 viewportSize;
    /// Seconds (the scene tree's shader time).
    public float time;
};

/// What fragment() reads and writes. Positions and normals are view space (world space in the shadow passes).
public struct SurfaceFragment
{
    /// The fragment's position.
    public float3 vertex;
    /// The surface normal; write it to shade with another.
    public float3 normal;
    /// From the fragment towards the camera.
    public float3 view;
    /// The texture coordinate.
    public float2 uv;
    /// The second texture coordinate (the same as uv).
    public float2 uv2;
    /// The interpolated vertex colour.
    public float4 color;
    /// The window position (pixels) and depth.
    public float4 fragCoord;
    /// True on front faces.
    public bool frontFacing;
    /// The position in the viewport, 0..1.
    public float2 screenUv;
    /// Model → world.
    public float4x4 modelMatrix;
    /// Transforms normals model → world.
    public float3x3 modelNormalMatrix;
    /// Model → view.
    public float4x4 modelViewMatrix;
    /// Transforms normals model → view.
    public float3x3 modelViewNormalMatrix;
    /// World → view.
    public float4x4 viewMatrix;
    /// View → world.
    public float4x4 invViewMatrix;
    /// The main camera's view → world.
    public float4x4 mainCamInvViewMatrix;
    /// View → clip.
    public float4x4 projectionMatrix;
    /// Clip → view.
    public float4x4 invProjectionMatrix;
    /// The node's origin in world space.
    public float3 nodePositionWorld;
    /// The node's origin in view space.
    public float3 nodePositionView;
    /// The camera's position in world space.
    public float3 cameraPositionWorld;
    /// The camera's forward direction in world space.
    public float3 cameraDirectionWorld;
    /// The viewport's size in pixels.
    public float2 viewportSize;
    /// Seconds.
    public float time;
    /// The base colour (1, 1, 1).
    public float3 albedo;
    /// The opacity (1): used with [alpha(Alpha.Cutout)] or [alpha(Alpha.Blend)].
    public float alpha;
    /// The cut-out threshold (0) of [alpha(Alpha.Cutout)].
    public float alphaScissorThreshold;
    /// Metalness, 0..1 (0).
    public float metallic;
    /// Roughness, 0..1 (1).
    public float roughness;
    /// The Blinn-Phong highlight's strength (0.5; [specular(Specular.BlinnPhong)] only).
    public float specular;
    /// Ambient occlusion, 0..1 (1).
    public float ao;
    /// Light the surface emits, added after lighting (0).
    public float3 emission;
    /// A tangent-space normal map value, 0..1, +Y up ((0.5, 0.5, 1): none); the tangent frame comes from screen derivatives.
    public float3 normalMap;
    /// How strongly normalMap bends the normal (1).
    public float normalMapDepth;
    /// The position lights are computed at (vertex).
    public float3 lightVertex;
};

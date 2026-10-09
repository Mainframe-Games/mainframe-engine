// StandardMaterial3D (Blinn-Phong, unshaded or PBR): set 0 frame + lights + sky lighting, set 1 shadows, set 2 material.
// Linear HDR output, fogged (fog.slang). Opaque and cutout surfaces take the screen-space AO (ADR 0165; exactly 1
// without SSAO); blended ones are not in the depth prepass, so the AO under them belongs to what is behind.
#include "frame.slang"
#include "shadows.slang"
#include "lights.slang"
#include "material.slang"
#include "fog.slang"

// AlphaMode of the pipeline: opaque pipelines never discard (early depth test stays on).
[vk::constant_id(0)] const int kAlphaMode = kAlphaOpaque;

// Surfaces with the second vertex stream (MeshExt.vk.vert): the albedo is multiplied by the vertex colour. Without it
// the colour input is a constant white that is never read, so other surfaces shade bit-exactly as before.
[vk::constant_id(1)] const bool kVertexColor = false;

struct FsIn
{
    [[vk::location(0)]] float3 inWorldPos;
    [[vk::location(1)]] float3 inNormal;
    [[vk::location(2)]] float2 inUV;
    [[vk::location(3)]] float4 inColor; // linear RGBA vertex colour
};

[shader("fragment")]
float4 main(FsIn input, float4 fragCoord : SV_Position, bool frontFacing : SV_IsFrontFace) : SV_Target0
{
    float2 uv = materialUv(input.inUV);
    float2 duvdx = ddx(uv);
    float2 duvdy = ddy(uv);
    float3 dpdx = ddx(input.inWorldPos);
    float3 dpdy = ddy(input.inWorldPos);
    float4 albedo = materialAlbedo(uv, duvdx, duvdy);
    if (kVertexColor)
        albedo *= input.inColor;
    if (kAlphaMode == kAlphaCutout && albedo.a < material.params.z)
        discard;

    float3 N = normalize(input.inNormal);
    if (material.flags.z != 0u && !frontFacing)
        N = -N; // double-sided: light the back face with its own normal

    float3 color;
    float ssao = kAlphaMode == kAlphaBlend ? 1.0 : ambientOcclusionAt(fragCoord);
    uint shading = material.flags.y; // uniform: the branches keep derivatives defined
    if (shading == kShadingUnshaded)
    {
        color = albedo.rgb;
    }
    else if (shading == kShadingPbr)
    {
        float3 orm = materialOrm(uv, duvdx, duvdy);
        PbrSurface s;
        s.albedo = albedo.rgb;
        s.ao = orm.x;
        s.roughness = orm.y;
        s.metallic = orm.z;
        s.N = materialNormal(N, uv, dpdx, dpdy, duvdx, duvdy);
        color = shadeLightsPbr(s, N, input.inWorldPos, ssao);
    }
    else
    {
        float3 Nmap = materialNormal(N, uv, dpdx, dpdy, duvdx, duvdy);
        color = shadeLightsBlinnPhong(albedo.rgb, Nmap, N, input.inWorldPos, material.params.x, material.params.y, ssao);
    }

    color += materialEmission(uv, duvdx, duvdy);
    color = applyFog(color, input.inWorldPos);
    return float4(color, kAlphaMode == kAlphaBlend ? albedo.a : 1.0);
}

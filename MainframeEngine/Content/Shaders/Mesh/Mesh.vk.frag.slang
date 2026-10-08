// StandardMaterial3D (Blinn-Phong): set 0 frame + lights, set 1 shadows, set 2 material. Linear HDR output.
#include "frame.slang"
#include "shadows.slang"
#include "lights.slang"
#include "material.slang"

// AlphaMode of the pipeline: opaque pipelines never discard (early depth test stays on).
[vk::constant_id(0)] const int kAlphaMode = kAlphaOpaque;

struct FsIn
{
    [[vk::location(0)]] float3 inWorldPos;
    [[vk::location(1)]] float3 inNormal;
    [[vk::location(2)]] float2 inUV;
};

[shader("fragment")]
float4 main(FsIn input, bool frontFacing : SV_IsFrontFace) : SV_Target0
{
    float2 uv = materialUv(input.inUV);
    float2 duvdx = ddx(uv);
    float2 duvdy = ddy(uv);
    float3 dpdx = ddx(input.inWorldPos);
    float3 dpdy = ddy(input.inWorldPos);
    float4 albedo = materialAlbedo(uv, duvdx, duvdy);
    if (kAlphaMode == kAlphaCutout && albedo.a < material.params.z)
        discard;

    float3 N = normalize(input.inNormal);
    if (material.flags.z != 0u && !frontFacing)
        N = -N; // double-sided: light the back face with its own normal

    float3 color;
    if (material.flags.y != 0u)
    {
        color = albedo.rgb; // unshaded
    }
    else
    {
        float3 Nmap = materialNormal(N, uv, dpdx, dpdy, duvdx, duvdy);
        color = shadeLightsBlinnPhong(albedo.rgb, Nmap, N, input.inWorldPos, material.params.x, material.params.y);
    }

    color += materialEmission(uv, duvdx, duvdy);
    return float4(color, kAlphaMode == kAlphaBlend ? albedo.a : 1.0);
}

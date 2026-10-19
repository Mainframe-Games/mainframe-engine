#include "sky.slang"

[[vk::binding(0, 1)]] SamplerCube cubemap;

[shader("fragment")]
float4 main([[vk::location(0)]] float2 inUV) : SV_Target0 // inUV: NDC
{
    float3 dir = skyRay(inUV);
    float4 color = cubemap.Sample(dir);
    return float4(applySkyFog(color.rgb, dir), color.a);
}

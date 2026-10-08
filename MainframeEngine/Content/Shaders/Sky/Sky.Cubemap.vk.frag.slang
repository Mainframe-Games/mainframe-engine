#include "sky.slang"

[[vk::binding(0, 1)]] SamplerCube cubemap;

[shader("fragment")]
float4 main([[vk::location(0)]] float2 inUV) : SV_Target0 // inUV: NDC
{
    return cubemap.Sample(skyRay(inUV));
}

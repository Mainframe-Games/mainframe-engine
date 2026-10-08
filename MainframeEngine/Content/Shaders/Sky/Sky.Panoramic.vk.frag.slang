#include "sky.slang"

[[vk::binding(0, 1)]] Sampler2D panoramic;

[shader("fragment")]
float4 main([[vk::location(0)]] float2 inUV) : SV_Target0 // inUV: NDC
{
    float3 dir = skyRay(inUV);

    // Equirectangular UV mapping.
    // u: longitude mapped to [0,1], seam at atan2(0,-1) = ±PI
    // v: latitude mapped to [0,1], 0 = zenith (top of image), 1 = nadir
    float u = atan2(dir.z, dir.x) / (2.0 * PI) + 0.5;
    float v = 0.5 - asin(clamp(dir.y, -1.0, 1.0)) / PI;

    return panoramic.Sample(float2(u, v));
}

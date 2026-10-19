// One face of the irradiance cube: the captured sky convolved with a cosine lobe and divided by π (the radiance a white
// Lambert surface facing N reflects), by cosine-weighted sampling of the source's mip chain.
#include "ibl_bake.slang"

[shader("fragment")]
float4 main(float4 position : SV_Position) : SV_Target0
{
    float3 N = cubeFaceDirection(bake.face, position.xy / bake.targetSize);
    float3 sum = float3(0.0);
    for (uint i = 0u; i < bake.sampleCount; i++)
    {
        float2 xi = hammersley(i, bake.sampleCount);
        float phi = 2.0 * PI * xi.x;
        float cosTheta = sqrt(1.0 - xi.y);
        float sinTheta = sqrt(xi.y);
        float3 L = tangentToWorld(float3(cos(phi) * sinTheta, sin(phi) * sinTheta, cosTheta), N);
        sum += source.SampleLevel(L, sourceMip(cosTheta / PI)).rgb;
    }

    return float4(sum / float(bake.sampleCount), 1.0);
}

// One face of one mip of the radiance cube: the captured sky convolved with the GGX lobe of the mip's roughness (split-sum
// approximation, N = V = R), by importance sampling. Roughness 0 copies the sky.
#include "ibl_bake.slang"

[shader("fragment")]
float4 main(float4 position : SV_Position) : SV_Target0
{
    float3 N = cubeFaceDirection(bake.face, position.xy / bake.targetSize);
    if (bake.roughness <= 0.0)
        return float4(source.SampleLevel(N, 0.0).rgb, 1.0);

    float alpha = bake.roughness * bake.roughness;
    float a2 = alpha * alpha;
    float3 sum = float3(0.0);
    float weight = 0.0;
    for (uint i = 0u; i < bake.sampleCount; i++)
    {
        float3 H = importanceSampleGgx(hammersley(i, bake.sampleCount), N, alpha);
        float NdotH = saturate(dot(N, H));
        float3 L = 2.0 * NdotH * H - N;
        float NdotL = dot(N, L);
        if (NdotL <= 0.0)
            continue;
        float d = NdotH * NdotH * (a2 - 1.0) + 1.0;
        float D = a2 / (PI * d * d);
        float pdf = D * 0.25; // D·N·H / (4·V·H) with V = N
        sum += source.SampleLevel(L, sourceMip(pdf)).rgb * NdotL;
        weight += NdotL;
    }

    return float4(sum / max(weight, 1e-4), 1.0);
}

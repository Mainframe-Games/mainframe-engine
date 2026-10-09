// Depth of field (ADR 0168), pass 1 at half resolution: each texel takes one signed circle of confusion (full-resolution
// pixels) from its 2 × 2 scene texels — the nearest one's when any of them is in front of the near plane (the near blur
// dilates over what is behind it), else the farthest one's (a sharp foreground edge never darkens the blurred background
// next to it) — and the mean colour of the texels whose circle is within a pixel of it.
#include "dof.slang"

[[vk::binding(0, 0)]] Sampler2D hdrScene;          // read with Load
[[vk::binding(1, 0)]] Sampler2D<float> sceneDepth; // point

[shader("fragment")]
float4 main(float4 fragCoord : SV_Position) : SV_Target0
{
    uint w, h, levels;
    hdrScene.GetDimensions(0u, w, h, levels);
    int2 last = int2(int(w) - 1, int(h) - 1);
    int2 base = int2(fragCoord.xy) * 2;
    int2 texels[4] = { min(base, last), min(base + int2(1, 0), last), min(base + int2(0, 1), last), min(base + int2(1, 1), last) };
    float coc[4];
    float nearest = 1e9, farthest = -1e9;
    for (int i = 0; i < 4; i++)
    {
        coc[i] = circleOfConfusion(sceneDepth.Load(int3(texels[i], 0)));
        nearest = min(nearest, coc[i]);
        farthest = max(farthest, coc[i]);
    }

    float chosen = nearest < 0.0 ? nearest : farthest;
    float3 color = float3(0.0);
    float weight = 0.0;
    for (int i = 0; i < 4; i++)
    {
        float wi = saturate(1.0 - abs(coc[i] - chosen)) + 1e-4;
        color += hdrScene.Load(int3(texels[i], 0)).rgb * wi;
        weight += wi;
    }

    return float4(color / weight, chosen);
}

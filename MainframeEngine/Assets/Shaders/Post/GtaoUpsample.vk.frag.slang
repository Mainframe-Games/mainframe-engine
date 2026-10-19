// SSAO pass 3 (ADR 0165): the denoised half-resolution AO upsampled to full resolution against the full depth (joint
// bilateral: bilinear weights × depth similarity; half texel h stands for full texel 2h, so even pixels take one texel),
// falling back to the closest-depth texel when none matches (thin objects missing at half resolution), then Godot's
// intensity and power: ao = (1 − intensity · (1 − visibility))^power. RGBA16F out (ADR 0170): r = AO, g = 1 (the contact
// shadow: the ContactShadows effect, when on, copies this image and writes it), b = the view depth (contact shadows and the
// bent normal apply only to the surface it belongs to), a = the view-space bent normal packed (gtaoPackBentNormal; 1 for
// the sky: none). Lit shaders read it at set 0 binding 5.
#include "gtao.slang"

[[vk::binding(0, 0)]] Sampler2D aoDepth;
[[vk::binding(1, 0)]] Sampler2D<float> sceneDepth;

[shader("fragment")]
float4 main(float4 fragCoord : SV_Position) : SV_Target0
{
    int2 p = int2(fragCoord.xy);
    float depth = sceneDepth.Load(int3(p, 0));
    if (depth >= kGtaoSkyDepth)
        return float4(1.0);

    uint w, h, levels;
    aoDepth.GetDimensions(0u, w, h, levels);
    int2 halfSize = int2(int(w), int(h));
    float z = gtaoLinearDepth(depth);
    float invTolerance = 1.0 / (gtao.filter.w * z);
    int2 base = p >> 1;
    float2 f = float2(p & 1) * 0.5;

    float sum = 0.0, weights = 0.0, closest = 3.0e38, closestAo = 1.0;
    float2 bent = float2(0.0), closestBent = float2(0.0);
    [unroll]
    for (int c = 0; c < 4; c++)
    {
        int2 corner = int2(c & 1, c >> 1);
        float bilinear = (corner.x == 1 ? f.x : 1.0 - f.x) * (corner.y == 1 ? f.y : 1.0 - f.y);
        if (bilinear <= 0.0)
            continue;
        float4 s = aoDepth.Load(int3(min(base + corner, halfSize - 1), 0));
        float difference = abs(s.y - z);
        float r = difference * invTolerance;
        float weight = bilinear * exp2(-r * r);
        sum += s.x * weight;
        bent += s.zw * weight;
        weights += weight;
        if (difference < closest)
        {
            closest = difference;
            closestAo = s.x;
            closestBent = s.zw;
        }
    }

    float visibility = saturate(weights > 1e-3 ? sum / weights : closestAo);
    float ao = pow(saturate(1.0 - gtao.filter.x * (1.0 - visibility)), gtao.filter.y);
    float3 bentNormal = gtaoUnpackHemisphere(weights > 1e-3 ? bent / weights : closestBent);
    return float4(ao, 1.0, z, gtaoPackBentNormal(bentNormal.xy));
}

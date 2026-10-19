// Physical sky (ADR 0154): the transmittance LUT, 256 × 64 RGBA16F. Each texel is a (view radius, view zenith
// cosine) pair (transmittanceParams); it holds exp(−optical depth) from that point to the top of the atmosphere.
// Rendered with a fullscreen triangle when the atmosphere's parameters change.
#include "atmosphere.slang"

[[vk::push_constant]] ConstantBuffer<Atmosphere> atmosphere;

static const int kSteps = 40;

[shader("fragment")]
float4 main(float4 fragCoord : SV_Position) : SV_Target0
{
    float bottom = atmosphere.ground.w;
    float top = atmosphere.view.x;
    float2 uv = fragCoord.xy / kTransmittanceLutSize;
    float r, mu;
    transmittanceParams(bottom, top, uv, r, mu);

    float3 origin = float3(0.0, r, 0.0);
    float3 dir = float3(sqrt(max(1.0 - mu * mu, 0.0)), mu, 0.0);
    float distance = max(raySphere(origin, dir, top), 0.0);
    float dt = distance / float(kSteps);
    float3 depth = float3(0.0);
    for (int i = 0; i < kSteps; i++)
    {
        float3 p = origin + dir * ((float(i) + 0.5) * dt);
        depth += sampleMedium(atmosphere, length(p) - bottom).extinction * dt;
    }

    return float4(exp(-depth), 1.0);
}

// Physical sky (ADR 0154): the multiple-scattering LUT, 32 × 32 RGBA16F, indexed by (cos sun zenith, altitude).
// Hillaire 2020 §5.5: from each texel's point, 64 directions over the sphere integrate second-order in-scattered light
// (isotropic phase, plus the lit ground) and the transfer f_ms; the texel holds L₂ / (1 − f_ms), the sum of every
// scattering order. Hillaire reduces the directions with compute; here one fragment loops over all 64.
#include "atmosphere.slang"

[[vk::push_constant]] ConstantBuffer<Atmosphere> atmosphere;
[[vk::binding(0, 0)]] Sampler2D transmittanceLut;

static const int kDirections = 8; // 8 × 8 = 64
static const int kSteps = 20;

[shader("fragment")]
float4 main(float4 fragCoord : SV_Position) : SV_Target0
{
    float bottom = atmosphere.ground.w;
    float top = atmosphere.view.x;
    float2 uv = fragCoord.xy / kMultiScatteringLutSize;
    uv = float2(fromSubUvToUnit(uv.x, kMultiScatteringLutSize.x), fromSubUvToUnit(uv.y, kMultiScatteringLutSize.y));
    float cosSunZenith = clamp(uv.x * 2.0 - 1.0, -1.0, 1.0);
    float r = bottom + 0.005 + saturate(uv.y) * (top - bottom - 0.01);

    float3 sunDir = float3(sqrt(max(1.0 - cosSunZenith * cosSunZenith, 0.0)), cosSunZenith, 0.0);
    float3 origin = float3(0.0, r, 0.0);
    float isotropicPhase = 1.0 / (4.0 * PI);

    float3 sumL = float3(0.0);
    float3 sumF = float3(0.0);
    for (int i = 0; i < kDirections; i++)
    {
        for (int j = 0; j < kDirections; j++)
        {
            // Stratified uniform directions over the sphere.
            float cosTheta = 1.0 - 2.0 * (float(i) + 0.5) / float(kDirections);
            float sinTheta = sqrt(max(1.0 - cosTheta * cosTheta, 0.0));
            float phi = 2.0 * PI * (float(j) + 0.5) / float(kDirections);
            float3 dir = float3(sinTheta * cos(phi), cosTheta, sinTheta * sin(phi));

            float ground = raySphere(origin, dir, bottom);
            float distance = ground >= 0.0 ? ground : max(raySphere(origin, dir, top), 0.0);
            float dt = distance / float(kSteps);
            float3 throughput = float3(1.0);
            float3 l = float3(0.0);
            float3 f = float3(0.0);
            for (int s = 0; s < kSteps; s++)
            {
                float3 p = origin + dir * ((float(s) + 0.5) * dt);
                float pr = length(p);
                float3 up = p / pr;
                Medium m = sampleMedium(atmosphere, pr - bottom);
                float shadow = raySphere(p, sunDir, bottom) >= 0.0 ? 0.0 : 1.0;
                float3 sunT = sampleTransmittance(transmittanceLut, bottom, top, pr, dot(up, sunDir));
                float3 scattered = m.scattering * isotropicPhase * sunT * shadow;
                float3 stepT = exp(-m.extinction * dt);
                float3 extinction = max(m.extinction, float3(1e-9));
                l += throughput * (scattered - scattered * stepT) / extinction;
                f += throughput * (m.scattering - m.scattering * stepT) / extinction;
                throughput *= stepT;
            }

            if (ground >= 0.0)
            {
                // The lit ground reflects (Lambert) back into the atmosphere.
                float3 n = normalize(origin + dir * ground);
                float cosSun = dot(n, sunDir);
                float3 sunT = sampleTransmittance(transmittanceLut, bottom, top, bottom, cosSun);
                l += throughput * sunT * saturate(cosSun) * atmosphere.ground.rgb / PI;
            }

            sumL += l;
            sumF += f;
        }
    }

    // Each direction weighs the sphere's solid angle 4π / 64 times the isotropic phase 1 / 4π.
    float weight = 1.0 / float(kDirections * kDirections);
    float3 l2 = sumL * weight;
    float3 fms = sumF * weight;
    return float4(l2 / max(1.0 - fms, float3(1e-3)), 1.0);
}

// Physical sky (ADR 0154): the sky-view LUT, 192 × 108 RGBA16F. Each texel is a view direction around the camera,
// latitude-warped towards the horizon and parameterized by its azimuth from the sun (skyViewParams), so it depends on
// the camera's altitude and the sun's elevation only. It holds the in-scattered radiance per unit sun illuminance:
// single scattering (Rayleigh + Mie phases, the planet's shadow) plus the multiple-scattering LUT, and the lit ground
// where the ray hits it. Rendered when the sun's elevation or the atmosphere changes.
#include "atmosphere.slang"

[[vk::push_constant]] ConstantBuffer<Atmosphere> atmosphere;
[[vk::binding(0, 0)]] Sampler2D transmittanceLut;
[[vk::binding(1, 0)]] Sampler2D multiScatteringLut;

static const int kSteps = 32;

[shader("fragment")]
float4 main(float4 fragCoord : SV_Position) : SV_Target0
{
    float bottom = atmosphere.ground.w;
    float top = atmosphere.view.x;
    float viewR = atmosphere.view.y;
    float cosSunZenith = atmosphere.view.z;

    float cosViewZenith, cosLightView;
    skyViewParams(viewR, bottom, fragCoord.xy / kSkyViewLutSize, cosViewZenith, cosLightView);
    float sinViewZenith = sqrt(max(1.0 - cosViewZenith * cosViewZenith, 0.0));
    float3 dir = float3(sinViewZenith * cosLightView, cosViewZenith,
                        sinViewZenith * sqrt(max(1.0 - cosLightView * cosLightView, 0.0)));
    float3 sunDir = float3(sqrt(max(1.0 - cosSunZenith * cosSunZenith, 0.0)), cosSunZenith, 0.0);
    float3 origin = float3(0.0, viewR, 0.0);

    float ground = raySphere(origin, dir, bottom);
    float distance = ground >= 0.0 ? ground : max(raySphere(origin, dir, top), 0.0);
    float cosTheta = dot(dir, sunDir);
    float phaseR = rayleighPhase(cosTheta);
    float phaseM = miePhase(atmosphere.mieExtinction.w, cosTheta);

    // Steps grow quadratically with distance: dense near the camera, where the low haze is.
    float3 throughput = float3(1.0);
    float3 radiance = float3(0.0);
    float previous = 0.0;
    for (int s = 0; s < kSteps; s++)
    {
        float next = distance * pow((float(s) + 1.0) / float(kSteps), 2.0);
        float dt = next - previous;
        float3 p = origin + dir * (previous + 0.5 * dt);
        previous = next;

        float pr = length(p);
        float3 up = p / pr;
        float cosSun = dot(up, sunDir);
        Medium m = sampleMedium(atmosphere, pr - bottom);
        float shadow = raySphere(p, sunDir, bottom) >= 0.0 ? 0.0 : 1.0;
        float3 sunT = sampleTransmittance(transmittanceLut, bottom, top, pr, cosSun);
        float3 multiple = multiScatteringLut.SampleLevel(multiScatteringUv(bottom, top, pr, cosSun), 0.0).rgb;
        float3 scattered = (m.rayleighScattering * phaseR + m.mieScattering * phaseM) * sunT * shadow + m.scattering * multiple;
        float3 stepT = exp(-m.extinction * dt);
        radiance += throughput * (scattered - scattered * stepT) / max(m.extinction, float3(1e-9));
        throughput *= stepT;
    }

    if (ground >= 0.0)
    {
        float3 n = normalize(origin + dir * ground);
        float cosSun = dot(n, sunDir);
        float3 sunT = sampleTransmittance(transmittanceLut, bottom, top, bottom, cosSun);
        radiance += throughput * sunT * saturate(cosSun) * atmosphere.ground.rgb / PI;
    }

    return float4(radiance, 1.0);
}

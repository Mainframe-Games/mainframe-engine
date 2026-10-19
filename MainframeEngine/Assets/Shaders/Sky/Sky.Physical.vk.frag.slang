// Physical sky (ADR 0154): the sky-view LUT (Sky/AtmosphereSkyView.vk.frag) looked up in the view direction, scaled
// to the sun's illuminance in the engine's units, plus the sun disc coloured by the atmosphere's transmittance (the C#
// side computes it with AtmosphereModel). SkyParams (sky.slang) carries:
//   skyColor.x/y     = view radius, planet radius (km)
//   horizonColor.rgb = sky radiance scale: π × the sun's linear colour × energy × EnergyMultiplier
//   sunDirection.xyz = world direction towards the sun
//   sunColorIntensity.rgb = the sun disc's radiance (already transmitted and scaled)
//   sun.x            = cos of the disc's angular radius
//   sun.z            = 1 in an image-based lighting capture (no sun disc; ADR 0150)
// Only skyRay depends on the camera, so the pass draws into any view or cube face.
#include "sky.slang"
#include "atmosphere.slang"

[[vk::binding(0, 1)]] Sampler2D skyViewLut;

[shader("fragment")]
float4 main([[vk::location(0)]] float2 inUV) : SV_Target0 // inUV: NDC
{
    float3 dir = skyRay(inUV);
    float viewR = sky.skyColor.x;
    float bottom = sky.skyColor.y;
    float3 sunDir = normalize(sky.sunDirection.xyz);

    // The LUT is parameterized by the azimuth from the sun: the cosine between the horizontal projections.
    float2 viewH = dir.xz;
    float2 sunH = sunDir.xz;
    float cosLightView = dot(viewH, viewH) > 1e-10 && dot(sunH, sunH) > 1e-10 ? dot(normalize(viewH), normalize(sunH)) : 1.0;
    float3 color = skyViewLut.SampleLevel(skyViewUv(viewR, bottom, dir.y, cosLightView), 0.0).rgb * sky.horizonColor.rgb;

    // The sun disc, hidden by the planet.
    if (sky.sun.z < 0.5 && raySphere(float3(0.0, viewR, 0.0), dir, bottom) < 0.0)
    {
        float sunSize = sky.sun.x;
        float softEdge = max(1.0 - sunSize, 0.0001);
        float sunMask = smoothstep(sunSize - softEdge * 0.05, sunSize, dot(dir, sunDir));
        color += sky.sunColorIntensity.rgb * sunMask;
    }

    return float4(applySkyFog(color, dir), 1.0);
}

// The physical sky's atmosphere (ADR 0154): Hillaire 2020, "A Scalable and Production Ready Sky and Atmosphere
// Rendering Technique", in kilometres with +Y up. The look-up tables (Sky/Atmosphere*.vk.frag) and the sky pass
// (Sky/Sky.Physical.vk.frag) share these parameterizations; AtmosphereModel.cs mirrors the integrals on the CPU.
#pragma once

#include "common.slang"

// The LUT passes' push constants (PhysicalSkyLuts.AtmospherePush in C#, 96 bytes).
struct Atmosphere
{
    float4 rayleigh;      // rgb = Rayleigh scattering at the ground (1/km), w = scale height (km)
    float4 mieScattering; // rgb = Mie scattering at the ground (1/km), w = scale height (km)
    float4 mieExtinction; // rgb = Mie extinction at the ground (1/km), w = phase anisotropy g
    float4 ozone;         // rgb = ozone absorption at the layer's peak (1/km)
    float4 ground;        // rgb = ground albedo (linear), w = planet radius (km)
    float4 view;          // x = top of the atmosphere (km from the centre), y = view radius (km), z = cos of the sun's zenith angle
};

static const float2 kTransmittanceLutSize = float2(256.0, 64.0);
static const float2 kMultiScatteringLutSize = float2(32.0, 32.0);
static const float2 kSkyViewLutSize = float2(192.0, 108.0);

// Nearest non-negative distance along unit `dir` from `origin` to a sphere of `radius` at the origin, or -1.
float raySphere(float3 origin, float3 dir, float radius)
{
    float b = dot(origin, dir);
    float c = dot(origin, origin) - radius * radius;
    float discriminant = b * b - c;
    if (discriminant < 0.0)
        return -1.0;
    float s = sqrt(discriminant);
    float near = -b - s;
    float far = -b + s;
    if (near >= 0.0)
        return near;
    return far >= 0.0 ? far : -1.0;
}

// Texel-centre remapping (Hillaire): a [0, 1] parameter to the UV of a LUT of `size` texels and back, so the first and
// last texel centres hold the parameter's ends exactly.
float fromUnitToSubUv(float u, float size) { return (u + 0.5 / size) * (size / (size + 1.0)); }
float fromSubUvToUnit(float u, float size) { return (u - 0.5 / size) * (size / (size - 1.0)); }

float rayleighPhase(float cosTheta) { return 3.0 / (16.0 * PI) * (1.0 + cosTheta * cosTheta); }

// Cornette–Shanks.
float miePhase(float g, float cosTheta)
{
    float g2 = g * g;
    float denominator = pow(max(1.0 + g2 - 2.0 * g * cosTheta, 1e-6), 1.5);
    return 3.0 / (8.0 * PI) * (1.0 - g2) * (1.0 + cosTheta * cosTheta) / ((2.0 + g2) * denominator);
}

struct Medium
{
    float3 rayleighScattering;
    float3 mieScattering;
    float3 scattering;
    float3 extinction;
};

Medium sampleMedium(Atmosphere a, float altitude)
{
    float rayleighDensity = exp(-altitude / a.rayleigh.w);
    float mieDensity = exp(-altitude / a.mieScattering.w);
    float ozoneDensity = max(0.0, 1.0 - abs(altitude - 25.0) / 15.0);
    Medium m;
    m.rayleighScattering = a.rayleigh.rgb * rayleighDensity;
    m.mieScattering = a.mieScattering.rgb * mieDensity;
    m.scattering = m.rayleighScattering + m.mieScattering;
    m.extinction = m.rayleighScattering + a.mieExtinction.rgb * mieDensity + a.ozone.rgb * ozoneDensity;
    return m;
}

// ── Transmittance LUT: (view radius r, view zenith cosine mu) ↔ uv (Bruneton 2017) ─────────────────────────

float2 transmittanceUv(float bottom, float top, float r, float mu)
{
    float h = sqrt(max(top * top - bottom * bottom, 0.0));
    float rho = sqrt(max(r * r - bottom * bottom, 0.0));
    float discriminant = r * r * (mu * mu - 1.0) + top * top;
    float d = max(0.0, -r * mu + sqrt(max(discriminant, 0.0)));
    float dMin = top - r;
    float dMax = rho + h;
    return float2((d - dMin) / max(dMax - dMin, 1e-6), rho / h);
}

void transmittanceParams(float bottom, float top, float2 uv, out float r, out float mu)
{
    float h = sqrt(max(top * top - bottom * bottom, 0.0));
    float rho = h * uv.y;
    r = sqrt(rho * rho + bottom * bottom);
    float dMin = top - r;
    float dMax = rho + h;
    float d = dMin + uv.x * (dMax - dMin);
    mu = d == 0.0 ? 1.0 : (h * h - rho * rho - d * d) / (2.0 * r * d);
    mu = clamp(mu, -1.0, 1.0);
}

float3 sampleTransmittance(Sampler2D lut, float bottom, float top, float r, float mu)
{
    return lut.SampleLevel(transmittanceUv(bottom, top, r, mu), 0.0).rgb;
}

// ── Multiple-scattering LUT: (cos sun zenith, altitude) ↔ uv ────────────────────────────────────────────────

float2 multiScatteringUv(float bottom, float top, float r, float cosSunZenith)
{
    float2 uv = saturate(float2(cosSunZenith * 0.5 + 0.5, (r - bottom) / (top - bottom)));
    return float2(fromUnitToSubUv(uv.x, kMultiScatteringLutSize.x), fromUnitToSubUv(uv.y, kMultiScatteringLutSize.y));
}

// ── Sky-view LUT: (view zenith cosine, cosine of the azimuth from the sun) ↔ uv, latitude-warped around the horizon ──

float2 skyViewUv(float viewR, float bottom, float cosViewZenith, float cosLightView)
{
    float vHorizon = sqrt(max(viewR * viewR - bottom * bottom, 0.0));
    float beta = acos(clamp(vHorizon / viewR, -1.0, 1.0));
    float zenithHorizonAngle = PI - beta;
    float viewZenith = acos(clamp(cosViewZenith, -1.0, 1.0));
    float v;
    if (viewZenith <= zenithHorizonAngle)
    {
        float coord = 1.0 - sqrt(max(1.0 - viewZenith / zenithHorizonAngle, 0.0));
        v = coord * 0.5;
    }
    else
    {
        float coord = sqrt(max((viewZenith - zenithHorizonAngle) / beta, 0.0));
        v = coord * 0.5 + 0.5;
    }
    float u = sqrt(saturate(-cosLightView * 0.5 + 0.5));
    return float2(fromUnitToSubUv(u, kSkyViewLutSize.x), fromUnitToSubUv(v, kSkyViewLutSize.y));
}

void skyViewParams(float viewR, float bottom, float2 uv, out float cosViewZenith, out float cosLightView)
{
    uv = float2(fromSubUvToUnit(uv.x, kSkyViewLutSize.x), fromSubUvToUnit(uv.y, kSkyViewLutSize.y));
    float vHorizon = sqrt(max(viewR * viewR - bottom * bottom, 0.0));
    float beta = acos(clamp(vHorizon / viewR, -1.0, 1.0));
    float zenithHorizonAngle = PI - beta;
    if (uv.y < 0.5)
    {
        float coord = 1.0 - 2.0 * uv.y;
        coord = 1.0 - coord * coord;
        cosViewZenith = cos(zenithHorizonAngle * coord);
    }
    else
    {
        float coord = uv.y * 2.0 - 1.0;
        cosViewZenith = cos(zenithHorizonAngle + beta * coord * coord);
    }
    float coord = uv.x * uv.x;
    cosLightView = -(coord * 2.0 - 1.0);
}

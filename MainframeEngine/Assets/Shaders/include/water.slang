// WaterMaterial3D's shared helpers (ADR 0159, ADR 0173): the parameters packed into the material block
// (MaterialParams.From(WaterMaterial3D)), the flow-advected ripple normals, the glints of every light and the light
// arriving at the water. Water/Water.vk.frag (the look without scene textures) and Water/WaterScene.vk.frag (refraction,
// screen-space reflections and caustics through the scene colour and depth copy) both include it.
#pragma once

#include "frame.slang"
#include "shadows.slang"
#include "lights.slang"
#include "material.slang"

// The water parameters inside the material block.
float3 waterScatterColor() { return material.albedo.rgb; }   // linear
float  waterScatterStrength() { return material.albedo.a; }
float3 waterAbsorption() { return material.emission.rgb; }     // per metre
float  waterReflectionStrength() { return material.emission.a; }
float  waterInvScaleNear() { return material.uvTransform.x; }  // 1 / metres per repeat
float  waterInvScaleFar() { return material.uvTransform.y; }
float  waterNormalStrength() { return material.uvTransform.z; }
float  waterRoughness() { return material.uvTransform.w; }
float  waterFlowCycle() { return material.params.x; }          // seconds
float  waterFlowScale() { return material.params.y; }
float  waterShoreFoam() { return material.params.z; }          // column metres
float  waterFoamStrength() { return material.params.w; }
float  waterInvFoamScale() { return material.pbr.x; }
float  waterSoftEdge() { return material.pbr.y; }              // column metres
float  waterWindDrift() { return material.pbr.z; }

static const float kWaterF0 = 0.02; // IOR 1.33
static const float kFoamBand = 0.12; // ribbon UV.u from either edge where bank foam starts

struct FsIn
{
    [[vk::location(0)]] float3 inWorldPos;
    [[vk::location(1)]] float3 inNormal;
    [[vk::location(2)]] float2 inUV;
    [[vk::location(3)]] float4 inWater; // x = column depth, yz = world XZ flow (m/s), w = foam
};

// Smooth value noise in 0..1 (hides the common pulse of the two flow phases).
float waterHash(float2 p)
{
    float3 p3 = frac(float3(p.x, p.y, p.x) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return frac((p3.x + p3.y) * p3.z);
}

float waterNoise(float2 p)
{
    float2 i = floor(p);
    float2 f = frac(p);
    f = f * f * (3.0 - 2.0 * f);
    float a = waterHash(i);
    float b = waterHash(i + float2(1.0, 0.0));
    float c = waterHash(i + float2(0.0, 1.0));
    float d = waterHash(i + float2(1.0, 1.0));
    return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
}

float3 waterTap(float2 uv, float2 duvdx, float2 duvdy)
{
    return normalTexture.SampleGrad(materialSampler, uv, duvdx, duvdy).xyz * 2.0 - 1.0;
}

// The far layer's rotation (36.87°): its ripples never line up with the near layer's.
static const float2x2 kFarRotation = float2x2(0.8, -0.6, 0.6, 0.8);

// Ripple normal in tangent space (+Y = towards −Z in world XZ): two scales of the normal map, each advected along the
// flow in two phases (Vlachos, "Water Flow in Portal 2"), whiteout-blended.
float3 waterRipples(float2 xz, float2 dx, float2 dy, float2 off0, float2 off1, float w0, float w1)
{
    float near = waterInvScaleNear();
    float far = waterInvScaleFar();
    float3 n0 = waterTap((xz - off0) * near, dx * near, dy * near) * w0 +
                waterTap((xz - off1) * near, dx * near, dy * near) * w1;
    float2 f0 = mul(kFarRotation, xz - off0 * 0.5) * far;
    float2 f1 = mul(kFarRotation, xz - off1 * 0.5) * far;
    float2 fdx = mul(kFarRotation, dx) * far;
    float2 fdy = mul(kFarRotation, dy) * far;
    float3 n1 = waterTap(f0, fdx, fdy) * w0 + waterTap(f1, fdx, fdy) * w1;
    n1.xy = mul(n1.xy, kFarRotation) * 0.6; // back from the rotated frame; the swell is gentler than the ripples
    float3 n = float3(n0.xy + n1.xy, max(n0.z * n1.z, 1e-3));
    n.xy *= waterNormalStrength();
    return normalize(n);
}

// Specular glints of every light (GGX, F0 = 0.02), shadowed like the PBR light loop. `sunShadow` returns the first
// directional light's shadow (1 when there is none), for the scattered light and the foam.
float3 waterLights(float3 N, float3 Ngeo, float3 V, float3 worldPos, float roughness, out float sunShadow)
{
    PbrSurface s;
    s.albedo = float3(0.0);
    s.metallic = 0.0;
    s.roughness = roughness;
    s.ao = 1.0;
    s.N = N;
    float  a = roughness * roughness;
    float  a2 = a * a;
    float3 F0 = float3(kWaterF0);
    float  NdotV = max(dot(N, V), 1e-4);
    bool   shadowsOn = lights.counts.w == 0;
    float3 result = float3(0.0);
    sunShadow = 1.0;

    for (int i = 0; i < lights.counts.x; i++)
    {
        DirLight l = lights.dir[i];
        float3 L = normalize(-l.directionIntensity.xyz);
        float shadow = shadowsOn ? dirShadow(i, worldPos, Ngeo, L) : 1.0;
        shadow = lerp(1.0, shadow, l.color.w);
        if (i == 0)
            sunShadow = shadow;
        result += pbrLight(s, F0, a2, V, NdotV, L, l.color.xyz * l.directionIntensity.w * shadow);
    }

    for (int i = 0; i < lights.counts.y; i++)
    {
        PointLight l = lights.point[i];
        float3 toLight = l.positionRange.xyz - worldPos;
        float dist = length(toLight);
        float shadow = shadowsOn ? pointShadow(i, worldPos, Ngeo, l.positionRange.xyz, l.positionRange.w) : 1.0;
        float atten = attenuate(dist, l.positionRange.w);
        result += pbrLight(s, F0, a2, V, NdotV, toLight / max(dist, 1e-4), l.colorIntensity.xyz * l.colorIntensity.w * atten * shadow);
    }

    for (int i = 0; i < lights.counts.z; i++)
    {
        SpotLight l = lights.spot[i];
        float3 lightPos = l.positionRange.xyz;
        float3 toLight = lightPos - worldPos;
        float dist = length(toLight);
        float3 L = toLight / max(dist, 1e-4);
        float shadow = shadowsOn ? spotShadow(i, worldPos, Ngeo, L, lightPos) : 1.0;
        shadow = lerp(1.0, shadow, l.outerPad.y);
        float atten = attenuate(dist, l.positionRange.w) * spotCone(l, L);
        result += pbrLight(s, F0, a2, V, NdotV, L, l.colorInner.xyz * l.directionIntensity.w * atten * shadow);
    }

    if (shadowsOn)
        result *= shadowCascadeTint(worldPos);
    return result;
}

// Light arriving at a horizontal-ish surface with normal N: the sky's diffuse light × `ao` plus the sun, shadowed.
float3 waterDiffuseLight(float3 N, float sunShadow, float ao)
{
    float3 light = iblDiffuse(N) * ao;
    if (lights.counts.x > 0)
    {
        DirLight l = lights.dir[0];
        float3 L = normalize(-l.directionIntensity.xyz);
        light += l.color.xyz * l.directionIntensity.w * saturate(dot(N, L)) * sunShadow;
    }
    return light;
}

// The two flow phases half a cycle apart (Vlachos), weighted so each is invisible while it resets: the world-XZ offsets
// of the ripple and foam lookups and their weights.
struct WaterFlow
{
    float2 off0;
    float2 off1;
    float  w0;
    float  w1;
};

WaterFlow waterFlow(float2 vertexFlow, float2 xz)
{
    float cycle = waterFlowCycle();
    float2 flow = vertexFlow * waterFlowScale() + frame.wind.xz * (frame.wind.w * waterWindDrift());
    float t = frame.clip.z / cycle + waterNoise(xz * 0.35);
    float p0 = frac(t);
    float p1 = frac(t + 0.5);
    WaterFlow f;
    f.w0 = 1.0 - abs(2.0 * p0 - 1.0);
    f.w1 = 1.0 - f.w0;
    f.off0 = flow * (p0 * cycle);
    f.off1 = flow * (p1 * cycle);
    return f;
}

// The rippled surface normal (world space) around the geometric normal Ng: a tangent frame with T along +X and B along
// -Z for an upward normal (the map's +Y).
float3 waterSurfaceNormal(float3 Ng, float2 xz, float2 dx, float2 dy, WaterFlow f)
{
    float3 T = normalize(float3(1.0, 0.0, 0.0) - Ng * Ng.x);
    float3 B = cross(Ng, T);
    float3 n = waterRipples(xz, dx, dy, f.off0, f.off1, f.w0, f.w1);
    return normalize(T * n.x + B * n.y + Ng * n.z);
}

// Foam coverage 0..1: shore (shallow column, the ribbon's edges), vertex foam (rapids, plunges) and fast flow mask the
// flow-advected foam texture.
float waterFoam(float column, float2 uv, float4 water, float2 xz, float2 dx, float2 dy, WaterFlow f)
{
    float invFoam = waterInvFoamScale();
    float foamTexture = albedoTexture.SampleGrad(materialSampler, (xz - f.off0) * invFoam, dx * invFoam, dy * invFoam).r * f.w0 +
                        albedoTexture.SampleGrad(materialSampler, (xz - f.off1) * invFoam, dx * invFoam, dy * invFoam).r * f.w1;
    float shoreDistance = waterShoreFoam();
    float shore = shoreDistance > 0.0 ? 1.0 - saturate(column / shoreDistance) : 0.0;
    float bank = 1.0 - abs(uv.x * 2.0 - 1.0); // 0 at the ribbon's edges (ponds use u = 0.5)
    shore = max(shore, shoreDistance > 0.0 ? 1.0 - saturate(bank / kFoamBand) : 0.0);
    float rapids = saturate((length(water.yz) - 1.5) / 2.5) * 0.5;
    float mask = saturate(max(max(shore, water.w), rapids) * waterFoamStrength());
    return smoothstep(1.0 - mask, 1.35 - mask, foamTexture) * saturate(mask * 2.0);
}

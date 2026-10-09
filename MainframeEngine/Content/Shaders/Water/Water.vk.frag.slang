// WaterMaterial3D (ADR 0159), the fallback look of docs/design/future/water.md (no SceneTextures): set 0 frame + lights
// + sky lighting, set 1 shadows, set 2 material (the StandardMaterial3D layout: the parameters per MaterialParams.From
// (WaterMaterial3D) below, the foam mask in the albedo slot, the ripple normals in the normal slot). Alpha-blended over
// what lies behind (straight alpha): the output colour × alpha is the light the water adds, 1 − alpha the share of the
// background that shows through. Linear HDR output.
#include "frame.slang"
#include "shadows.slang"
#include "lights.slang"
#include "material.slang"
#include "fog.slang"

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

// Light arriving at a horizontal-ish surface with normal N: the sky's diffuse light plus the sun, shadowed.
float3 waterDiffuseLight(float3 N, float sunShadow)
{
    float3 light = iblDiffuse(N);
    if (lights.counts.x > 0)
    {
        DirLight l = lights.dir[0];
        float3 L = normalize(-l.directionIntensity.xyz);
        light += l.color.xyz * l.directionIntensity.w * saturate(dot(N, L)) * sunShadow;
    }
    return light;
}

[shader("fragment")]
float4 main(FsIn input) : SV_Target0
{
    float3 P = input.inWorldPos;
    float2 xz = P.xz;
    float2 dx = ddx(xz);
    float2 dy = ddy(xz);
    float column = input.inWater.x;

    // Two flow phases half a cycle apart, weighted so each is invisible while it resets.
    float cycle = waterFlowCycle();
    float2 flow = input.inWater.yz * waterFlowScale() + frame.wind.xz * (frame.wind.w * waterWindDrift());
    float t = frame.clip.z / cycle + waterNoise(xz * 0.35);
    float p0 = frac(t);
    float p1 = frac(t + 0.5);
    float w0 = 1.0 - abs(2.0 * p0 - 1.0);
    float w1 = 1.0 - w0;
    float2 off0 = flow * (p0 * cycle);
    float2 off1 = flow * (p1 * cycle);

    // Tangent frame from the geometric normal: T along +X, B along −Z for an upward normal (the map's +Y).
    float3 Ng = normalize(input.inNormal);
    float3 T = normalize(float3(1.0, 0.0, 0.0) - Ng * Ng.x);
    float3 B = cross(Ng, T);
    float3 n = waterRipples(xz, dx, dy, off0, off1, w0, w1);
    float3 N = normalize(T * n.x + B * n.y + Ng * n.z);

    float3 V = normalize(frame.cameraPosition.xyz - P);
    float NdotV = max(dot(N, V), 1e-4);
    float roughness = clamp(waterRoughness(), 0.02, 1.0);

    // Fresnel (Schlick, F0 = 0.02) and the sky along the reflected ray × the split-sum BRDF.
    float F = kWaterF0 + (1.0 - kWaterF0) * pow(1.0 - NdotV, 5.0);
    float3 R = reflect(-V, N);
    R.y = abs(R.y); // ripples never reflect the ground below the horizon
    float2 brdf = iblBrdf(NdotV, roughness);
    float3 reflection = iblSpecular(R, roughness) * (kWaterF0 * brdf.x + brdf.y) * waterReflectionStrength();
    float sunShadow;
    float3 glints = waterLights(N, Ng, V, P, roughness, sunShadow);

    // The water body: Beer–Lambert over the column along the view ray, light scattered in where light is absorbed.
    float cosView = max(dot(Ng, V), 0.1);
    float3 transmittance = exp(-waterAbsorption() * (column / cosView));
    float3 scatter = waterScatterColor() * waterScatterStrength() * waterDiffuseLight(float3(0.0, 1.0, 0.0), sunShadow) *
                     (1.0 - transmittance);
    float through = (transmittance.r + transmittance.g + transmittance.b) / 3.0;

    // Premultiplied: what the water adds, and how much of the background still shows.
    float3 added = scatter * (1.0 - F) + reflection + glints;
    float keep = through * (1.0 - F);

    // Foam: shore (shallow column, the ribbon's edges), vertex foam (rapids, falls) and fast flow, masking the
    // flow-advected foam texture; lit as a rough white surface.
    float invFoam = waterInvFoamScale();
    float foamTexture = albedoTexture.SampleGrad(materialSampler, (xz - off0) * invFoam, dx * invFoam, dy * invFoam).r * w0 +
                        albedoTexture.SampleGrad(materialSampler, (xz - off1) * invFoam, dx * invFoam, dy * invFoam).r * w1;
    float shoreDistance = waterShoreFoam();
    float shore = shoreDistance > 0.0 ? 1.0 - saturate(column / shoreDistance) : 0.0;
    float bank = 1.0 - abs(input.inUV.x * 2.0 - 1.0); // 0 at the ribbon's edges (ponds use u = 0.5)
    shore = max(shore, shoreDistance > 0.0 ? 1.0 - saturate(bank / kFoamBand) : 0.0);
    float rapids = saturate((length(input.inWater.yz) - 1.5) / 2.5) * 0.5;
    float mask = saturate(max(max(shore, input.inWater.w), rapids) * waterFoamStrength());
    float foam = smoothstep(1.0 - mask, 1.35 - mask, foamTexture) * saturate(mask * 2.0);
    float3 foamColor = 0.85 * waterDiffuseLight(Ng, sunShadow);
    added = lerp(added, foamColor, foam);
    keep *= 1.0 - foam;

    // Soft edges: the surface fades in over the first SoftEdgeDistance of column.
    float softEdge = waterSoftEdge();
    float edge = softEdge > 0.0 ? smoothstep(0.0, softEdge, column) : 1.0;
    added *= edge;
    keep = lerp(1.0, keep, edge);

    float alpha = 1.0 - keep;
    if (alpha <= 1e-4)
        return float4(0.0);
    float3 color = applyFog(added / alpha, P);
    return float4(color, alpha);
}

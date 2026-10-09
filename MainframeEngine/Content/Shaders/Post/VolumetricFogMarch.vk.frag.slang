// Volumetric fog (ADR 0171, G8e.3), pass 1 of 3: the ray march at half resolution. From the camera to the surface the
// scene pass drew (or the fog's length) in pc.ray.z steps that crowd towards the camera (step i ends at (i / n)^spread of
// the ray), each sampled at an interleaved-gradient-noise offset (per frame with temporal reprojection, which averages
// it). Per step: the extinction = density × the height fog's profile × a wind-drifted noise; the sun's visibility from
// one comparison tap of its shadow cascade (or the far shadow); in-scattered light = albedo × extinction × (the sun ×
// visibility × Henyey–Greenstein phase + the sky's ambient light × ambient inject) + emission × extinction, integrated
// over the step as Hillaire (2015) does. Writes rgb = in-scattered radiance, a = transmittance.
// Set 0 is the view's frame set (camera, lights, sky lighting), set 1 the shadow set: what the lit shaders read.
#include "volumetric_fog.slang"
#include "environment.slang"
#include "shadows.slang"

[[vk::binding(0, 2)]] Sampler2D<float> sceneDepth; // point
[[vk::binding(1, 2)]] Sampler3D noiseVolume;        // 32³, tiling, trilinear

struct March
{
    float4 scattering; // rgb = albedo × density (1/m), a = density (extinction, 1/m)
    float4 emission;   // rgb = emission × density, a = Henyey–Greenstein g
    float4 ray;        // x = length (m), y = detail spread, z = steps, w = noise frame (< 0: fixed per pixel)
    float4 height;     // x = fog height (m), y = height density (1/m), z = ambient inject, w = 0
    float4 noise;      // x = 1 / noise scale (1/m), y = noise strength
    float4 sizes;      // xy = this target's size, zw = the scene's (pixels)
};

[[vk::push_constant]] ConstantBuffer<March> pc;

static const float kPi = 3.14159265;

float interleavedNoise(float2 pixel)
{
    return frac(52.9829189 * frac(dot(pixel, float2(0.06711056, 0.00583715))));
}

float henyeyGreenstein(float g, float cosTheta)
{
    float g2 = g * g;
    float d = 1.0 + g2 - 2.0 * g * cosTheta;
    return (1.0 - g2) / (4.0 * kPi * d * sqrt(d));
}

// The fog's density at p relative to pc.scattering.a: the height fog's shape × the noise.
float densityAt(float3 p, float3 drift)
{
    float h = exp(-pc.height.y * max(p.y - pc.height.x, 0.0));
    if (pc.noise.y > 0.0)
    {
        float n = noiseVolume.SampleLevel((p - drift) * pc.noise.x, 0.0).r;
        h *= max(1.0 + pc.noise.y * (2.0 * n - 1.0), 0.0);
    }
    return h;
}

// The shadowed directional light's visibility at a point in the air: one comparison tap (2 × 2 bilinear PCF) of the
// cascade covering it, or of the far shadow past the cascades; lit outside every map.
float sunVisibility(float3 p, float3 L)
{
    int count = int(shadow.csm.x);
    int c = shadowCascadeIndex(shadowViewDepth(p));
    ShadowMap2D map;
    float layer;
    if (c < count)
    {
        c = clampCascade(c);
        if (shadow.cascadeEnabled[c] == 0.0)
            return 1.0;
        map = shadow.cascades[c];
        layer = float(c);
    }
    else
    {
        if (shadow.farParams.x == 0.0)
            return 1.0;
        map = shadow.farMap;
        layer = kFarShadowLayer;
    }

    // Towards the light by the map's depth bias (in texels), so the air just above a lit surface is not self-shadowed.
    float3 q = p + L * (map.params.z * map.params.x);
    float4 ls = mul(float4(q, 1.0), map.viewProj);
    float3 coord = float3(ls.xy / ls.w * 0.5 + 0.5, ls.z / ls.w);
    if (any(coord < float3(0.0)) || any(coord > float3(1.0)))
        return 1.0;
    return cascadeCmp(coord.xy, layer, coord.z);
}

[shader("fragment")]
float4 main(float4 fragCoord : SV_Position) : SV_Target0
{
    float2 sceneSize = pc.sizes.zw;
    int2 pixel = volumetricPixel(int2(fragCoord.xy), sceneSize);
    float depth = sceneDepth.Load(int3(pixel, 0));

    float3 origin, dir;
    volumetricRay(pixel, sceneSize, origin, dir);
    float rayLength = pc.ray.x;
    if (depth < 1.0)
        rayLength = min(rayLength, length(volumetricWorldPosition(pixel, depth, sceneSize) - origin));

    // The sun: the directional light with the cascades (code 1), else the first one, unshadowed.
    int dirCount = min(lights.counts.x, MAX_DIR_LIGHTS);
    int sun = -1;
    for (int i = 0; i < MAX_DIR_LIGHTS; i++)
    {
        if (i < dirCount && sun < 0 && shadow.dirCodes[0][clamp(i, 0, 3)] == 1)
            sun = i;
    }
    bool shadowed = sun >= 0 && lights.counts.w == 0; // counts.w = 1: this view has no shadow maps
    sun = sun >= 0 ? sun : (dirCount > 0 ? 0 : -1);
    float3 sunLight = float3(0.0);
    float3 L = float3(0.0, 1.0, 0.0);
    float opacity = 0.0;
    float phase = 0.0;
    if (sun >= 0)
    {
        DirLight light = lights.dir[clamp(sun, 0, MAX_DIR_LIGHTS - 1)];
        L = normalize(-light.directionIntensity.xyz);
        sunLight = light.color.rgb * light.directionIntensity.w;
        opacity = light.color.w;
        phase = henyeyGreenstein(pc.emission.a, dot(dir, L));
    }

    // Ambient light from the sky: the mean of the irradiance above and below (a white Lambert surface's radiance).
    float3 ambient = pc.height.z > 0.0 ? 0.5 * (iblDiffuse(float3(0.0, 1.0, 0.0)) + iblDiffuse(float3(0.0, -1.0, 0.0))) * pc.height.z
                                       : float3(0.0);
    float3 drift = frame.wind.xyz * (frame.wind.w * frame.clip.z); // the noise drifts with the wind, 1 m/s per unit strength

    int steps = int(pc.ray.z);
    float jitter = pc.ray.w < 0.0 ? interleavedNoise(fragCoord.xy) : interleavedNoise(fragCoord.xy + 5.588238 * pc.ray.w);
    float3 scattered = float3(0.0);
    float transmittance = 1.0;
    float previous = 0.0;
    for (int s = 0; s < steps; s++)
    {
        float end = rayLength * pow(float(s + 1) / float(steps), pc.ray.y);
        float ds = end - previous;
        float t = previous + ds * jitter;
        previous = end;
        float3 p = origin + dir * t;
        float extinction = pc.scattering.a * densityAt(p, drift);
        if (extinction <= 1e-7 || ds <= 0.0)
            continue;

        float visibility = 1.0;
        if (shadowed)
            visibility = lerp(1.0, sunVisibility(p, L), opacity);
        float3 light = sunLight * (visibility * phase) + ambient;
        float relative = extinction / pc.scattering.a;
        float3 source = pc.scattering.rgb * relative * light + pc.emission.rgb * relative;
        float stepTransmittance = exp(-extinction * ds);
        scattered += transmittance * source * (1.0 - stepTransmittance) / extinction;
        transmittance *= stepTransmittance;
    }

    return float4(scattered, transmittance);
}

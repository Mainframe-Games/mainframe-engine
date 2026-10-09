// What lies behind refracting water (ADR 0173): set 3 of the water-scene pipeline layout (MeshRenderer's, bound only by
// refracting WaterMaterial3D draws and spray cards) is the view's scene copy (WaterSceneTextures in C#), and the push
// block carries the material's refraction, caustic and reflection settings and the view's quality.
//   sceneCopy  R16G16B16A16_SFLOAT mip chain: rgb = HDR scene colour after the opaques, a = linear view depth (m);
//              level 0 exact, further levels box-filtered colour and nearest (minimum) depth; the sky is 60 000 m
// Matrices are frame.slang's (row-major, mul(v, M)); screen UV (0,0) is the top-left (ndcToScreenUv).
#pragma once

#include "frame.slang"

[[vk::binding(0, 3)]] Sampler2D sceneCopy;

// MeshRenderer.WaterScenePush in C# (64 bytes, vertex + fragment push range at offset 0).
struct WaterScenePush
{
    float4 refraction; // x = strength (1 = water's IOR), y = blur (RefractionRoughness), z = the copy's last level, w = 1 when sceneCopy is the view's
    float4 caustics;   // x = 1 / scale (1/m), y = strength, z = max depth (m), w = 1 for screen-space reflections
    float4 ssr;        // x = steps, y = binary refinement steps, z = max distance (m), w = thickness (m)
    float4 extra;      // x = the surface's TAA reactivity (scene alpha 1 - x), yzw = 0
};

[[vk::push_constant]] ConstantBuffer<WaterScenePush> waterScene;

static const float kSceneSkyDepth = 60000.0; // WaterSceneTextures.SkyDepth

// The copy's linear view depth at a pixel of a level.
float sceneDepthAt(int2 pixel, int level)
{
    return sceneCopy.Load(int3(pixel, level)).a;
}

// True for an orthographic projection (row-vector: clip.w does not depend on the view depth).
bool sceneIsOrtho()
{
    return frame.projection[2][3] == 0.0;
}

// The view-space position of the point at screen UV `uv` whose linear view depth is `depth` (metres in front).
float3 sceneViewPosition(float2 uv, float depth)
{
    float4 q = mul(float4(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, 0.5, 1.0), frame.invProjection);
    float3 p = q.xyz / q.w;
    if (sceneIsOrtho())
        return float3(p.xy, -depth);
    return p * (depth / max(-p.z, 1e-6));
}

// Screen UV and linear depth of a view-space point (this frame's projection, jittered like the copy).
float2 sceneUv(float3 viewPos)
{
    float4 clip = mul(float4(viewPos, 1.0), frame.projection);
    return ndcToScreenUv(clip.xy / clip.w);
}

// World position of a view-space point.
float3 sceneWorldPosition(float3 viewPos)
{
    return frame.cameraPosition.xyz + mul(viewPos, (float3x3)frame.invViewRotation);
}

// Interleaved gradient noise (Jimenez 2014) in [0, 1), shifted per TAA sample so the history averages it.
float sceneNoise(float2 pixel)
{
    float2 p = pixel + 5.588238 * frame.temporal.z;
    return frac(52.9829189 * frac(dot(p, float2(0.06711056, 0.00583715))));
}

// Screen-space reflection (ADR 0173): the reflected ray from `viewPos` along `R` (view space, unit), clipped to the near
// plane, to `waterScene.ssr.z` metres and to the screen, is marched in equal steps of screen distance (perspective-
// correct depth along the projected segment) against level 1 of the copy (half resolution, nearest depth), from a
// per-pixel jittered start; the first step whose segment crosses the scene's surface (less a thickness) is refined by
// bisection. rgb = the reflected scene colour (blurrier with roughness), a = confidence: 0 for a miss, fading towards
// the screen edges, the march's end, rays back towards the camera and rough surfaces.
float4 sceneReflection(float3 viewPos, float3 R, float roughness, float2 pixel)
{
    float facing = 1.0 - smoothstep(0.0, 0.4, R.z); // rays back towards the camera leave what the screen shows
    float rough = 1.0 - smoothstep(0.3, 0.6, roughness);
    if (facing * rough <= 0.0 || waterScene.refraction.z < 1.0)
        return float4(0.0);

    float maxDistance = waterScene.ssr.z;
    float near = frame.clip.x;
    if (R.z > 1e-4)
        maxDistance = min(maxDistance, (-near * 1.5 - viewPos.z) / R.z); // stop before the near plane
    if (maxDistance <= 0.0)
        return float4(0.0);

    // The segment in screen space: UVs, and 1/w and z/w to interpolate the depth perspective-correctly.
    float3 end = viewPos + R * maxDistance;
    float4 clip0 = mul(float4(viewPos, 1.0), frame.projection);
    float4 clip1 = mul(float4(end, 1.0), frame.projection);
    float k0 = 1.0 / clip0.w;
    float k1 = 1.0 / clip1.w;
    float2 uv0 = ndcToScreenUv(clip0.xy * k0);
    float2 uv1 = ndcToScreenUv(clip1.xy * k1);
    float z0 = viewPos.z * k0;
    float z1 = end.z * k1;

    // Leave the screen no later than its edge.
    float2 delta = uv1 - uv0;
    float reach = 1.0;
    if (delta.x > 1e-6) reach = min(reach, (1.0 - uv0.x) / delta.x);
    if (delta.x < -1e-6) reach = min(reach, -uv0.x / delta.x);
    if (delta.y > 1e-6) reach = min(reach, (1.0 - uv0.y) / delta.y);
    if (delta.y < -1e-6) reach = min(reach, -uv0.y / delta.y);
    reach = saturate(reach);

    int steps = int(waterScene.ssr.x);
    int2 size = max(int2(frame.viewport.xy) >> 1, int2(1));
    float jitter = sceneNoise(pixel);
    float previous = 0.0;
    float previousZ = -viewPos.z;
    float hit = -1.0;
    for (int i = 0; i < steps; i++)
    {
        float f = reach * (float(i) + jitter) / float(steps);
        float2 uv = lerp(uv0, uv1, f);
        float rayZ = -lerp(z0, z1, f) / lerp(k0, k1, f);
        float sceneZ = sceneDepthAt(clamp(int2(uv * float2(size)), int2(0), size - 1), 1);
        // A crossing: the scene's surface lies within the depths this step swept (less a thickness), so long steps
        // never jump over a thin object, and rays passing behind one by more than the thickness go on.
        float thickness = waterScene.ssr.w + 0.03 * rayZ;
        if (rayZ > sceneZ && sceneZ > min(previousZ, rayZ) - thickness)
        {
            hit = f;
            break;
        }
        previous = f;
        previousZ = rayZ;
    }

    if (hit < 0.0)
        return float4(0.0);

    // Bisection between the last step in front of the scene and the first behind it.
    float lo = previous;
    float hi = hit;
    int refine = int(waterScene.ssr.y);
    for (int i = 0; i < refine; i++)
    {
        float mid = 0.5 * (lo + hi);
        float2 uv = lerp(uv0, uv1, mid);
        float rayZ = -lerp(z0, z1, mid) / lerp(k0, k1, mid);
        float sceneZ = sceneDepthAt(clamp(int2(uv * float2(size)), int2(0), size - 1), 1);
        if (rayZ > sceneZ)
            hi = mid;
        else
            lo = mid;
    }

    float2 uv = saturate(lerp(uv0, uv1, hi));
    float level = min(1.0 + roughness * 4.0, waterScene.refraction.z);
    float3 color = sceneCopy.SampleLevel(uv, level).rgb;
    float2 edge = abs(uv * 2.0 - 1.0);
    float confidence = (1.0 - smoothstep(0.85, 1.0, max(edge.x, edge.y))) * (1.0 - smoothstep(0.8, 1.0, hi)) * facing * rough;
    return float4(color, confidence);
}

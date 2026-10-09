// Shared by the sky image-based lighting bake passes (SkyRadiance in C#, ADR 0150): cube face directions, the
// Hammersley sequence, GGX importance sampling and the push block. Each pass is a fullscreen triangle
// (Post/Fullscreen.vk.vert) into one face (and mip) of a cube; the source is the captured sky cube with its mip chain.
#pragma once

#include "common.slang"

struct BakePush
{
    uint  face;        // 0..5: +X, -X, +Y, -Y, +Z, -Z (Vulkan's cube layer order)
    float roughness;   // perceptual roughness of the target mip (prefilter only)
    float targetSize;  // pixels across the target face
    float sourceSize;  // pixels across the source's mip 0
    uint  sampleCount;
    float sourceMaxMip;
};

[[vk::push_constant]] ConstantBuffer<BakePush> bake;
[[vk::binding(0, 0)]] SamplerCube source;

// Direction through uv (0..1, v down the image) of cube face `face` (Vulkan / OpenGL cube addressing).
float3 cubeFaceDirection(uint face, float2 uv)
{
    float s = uv.x * 2.0 - 1.0;
    float t = uv.y * 2.0 - 1.0;
    float3 d;
    if (face == 0u)      d = float3(1.0, -t, -s);
    else if (face == 1u) d = float3(-1.0, -t, s);
    else if (face == 2u) d = float3(s, 1.0, t);
    else if (face == 3u) d = float3(s, -1.0, -t);
    else if (face == 4u) d = float3(s, -t, 1.0);
    else                 d = float3(-s, -t, -1.0);
    return normalize(d);
}

float2 hammersley(uint i, uint n)
{
    return float2(float(i) / float(n), float(reversebits(i)) * 2.3283064365386963e-10);
}

// Tangent-space direction (z = up) around N.
float3 tangentToWorld(float3 v, float3 N)
{
    float3 up = abs(N.z) < 0.999 ? float3(0.0, 0.0, 1.0) : float3(1.0, 0.0, 0.0);
    float3 T = normalize(cross(up, N));
    float3 B = cross(N, T);
    return T * v.x + B * v.y + N * v.z;
}

// A GGX-distributed half vector around N (α = perceptual roughness²).
float3 importanceSampleGgx(float2 xi, float3 N, float alpha)
{
    float a2 = alpha * alpha;
    float phi = 2.0 * PI * xi.x;
    float cosTheta = sqrt((1.0 - xi.y) / (1.0 + (a2 - 1.0) * xi.y));
    float sinTheta = sqrt(1.0 - cosTheta * cosTheta);
    return tangentToWorld(float3(cos(phi) * sinTheta, sin(phi) * sinTheta, cosTheta), N);
}

// Source mip whose texels cover the solid angle of one sample of probability density `pdf` (filtered importance
// sampling: fewer samples without fireflies).
float sourceMip(float pdf)
{
    float texel = 4.0 * PI / (6.0 * bake.sourceSize * bake.sourceSize);
    float perSample = 1.0 / (float(bake.sampleCount) * pdf + 1e-6);
    return clamp(0.5 * log2(perSample / texel) + 1.0, 0.0, bake.sourceMaxMip);
}

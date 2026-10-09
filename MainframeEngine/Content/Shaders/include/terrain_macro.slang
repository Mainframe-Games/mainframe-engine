// The world's terrain macro texture at set 0 binding 7 (ADR 0175, G8e.7's RVT-lite; TerrainMacroTexture in C#): the
// terrain's surface from above, two layers over the whole terrain. Layer 0: albedo stored as its square root (rgb),
// roughness (a). Layer 1: the world normal's x and z (× 0.5 + 0.5), the ground height as a 16-bit code (b high byte,
// a low byte). A sampled image without a sampler: callers pass theirs (the material's), and the UV is clamped here.
// Include frame.slang first.
#pragma once

[[vk::binding(7, 0)]] Texture2DArray terrainMacro;

// True when a terrain macro texture is bound for the view's world (uniform).
bool terrainMacroActive()
{
    return frame.terrainMacroHeight.w > 0.5;
}

// The macro UV of a world position (0..1 inside the terrain).
float2 terrainMacroUv(float3 P)
{
    return (P.xz - frame.terrainMacroRect.xy) * frame.terrainMacroRect.zw;
}

// The ground height below `uv`, bilinear between texel centres from four loads of the 16-bit code.
float terrainMacroGround(float2 uv)
{
    float n = frame.terrainMacroHeight.z;
    float2 t = clamp(uv * n - 0.5, float2(0.0), float2(n - 1.0));
    int2 i = min(int2(t), int2(int(n) - 2));
    float2 f = t - float2(i);
    float2 c00 = terrainMacro.Load(int4(i.x, i.y, 1, 0)).ba;
    float2 c10 = terrainMacro.Load(int4(i.x + 1, i.y, 1, 0)).ba;
    float2 c01 = terrainMacro.Load(int4(i.x, i.y + 1, 1, 0)).ba;
    float2 c11 = terrainMacro.Load(int4(i.x + 1, i.y + 1, 1, 0)).ba;
    const float2 kCode = float2(255.0 * 256.0, 255.0); // unorm bytes back to the 16-bit code
    float a = dot(c00, kCode), b = dot(c10, kCode), c = dot(c01, kCode), d = dot(c11, kCode);
    float code = lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
    return frame.terrainMacroHeight.x + code * frame.terrainMacroHeight.y;
}

struct TerrainMacroTexel
{
    float3 albedo;  // linear
    float roughness;
    float3 N;       // world
    float ground;   // world height of the ground below
};

// The macro texel below P (world), filtered with `s` at the gradients of P's XZ.
TerrainMacroTexel terrainMacroSample(SamplerState s, float3 P, float3 dPdx, float3 dPdy)
{
    float2 uv = terrainMacroUv(P);
    float half = 0.5 / frame.terrainMacroHeight.z;
    float2 c = clamp(uv, float2(half), float2(1.0 - half));
    float2 gx = dPdx.xz * frame.terrainMacroRect.zw;
    float2 gy = dPdy.xz * frame.terrainMacroRect.zw;
    float4 a = terrainMacro.SampleGrad(s, float3(c, 0.0), gx, gy);
    float2 nxz = terrainMacro.SampleGrad(s, float3(c, 1.0), gx, gy).rg * 2.0 - 1.0;
    TerrainMacroTexel t;
    t.albedo = a.rgb * a.rgb;
    t.roughness = a.a;
    t.N = float3(nxz.x, sqrt(saturate(1.0 - dot(nxz, nxz))), nxz.y);
    t.ground = terrainMacroGround(c);
    return t;
}

uint terrainBlendPcg(uint v)
{
    uint state = v * 747796405u + 2891336453u;
    uint word = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;
    return (word >> 22u) ^ word;
}

float terrainBlendHash(int3 p)
{
    return float(terrainBlendPcg(uint(p.x) + terrainBlendPcg(uint(p.y) + terrainBlendPcg(uint(p.z))))) * (1.0 / 4294967296.0);
}

// Trilinear value noise (0..1).
float terrainBlendNoise(float3 p)
{
    float3 i = floor(p);
    float3 f = p - i;
    float3 u = f * f * (3.0 - 2.0 * f);
    int3 c = int3(i);
    float x00 = lerp(terrainBlendHash(c), terrainBlendHash(c + int3(1, 0, 0)), u.x);
    float x10 = lerp(terrainBlendHash(c + int3(0, 1, 0)), terrainBlendHash(c + int3(1, 1, 0)), u.x);
    float x01 = lerp(terrainBlendHash(c + int3(0, 0, 1)), terrainBlendHash(c + int3(1, 0, 1)), u.x);
    float x11 = lerp(terrainBlendHash(c + int3(0, 1, 1)), terrainBlendHash(c + int3(1, 1, 1)), u.x);
    return lerp(lerp(x00, x10, u.y), lerp(x01, x11, u.y), u.z);
}

// How much of the ground a surface point takes (0..strength): full at the ground, gone `height` above it, the edge
// broken up by two octaves of noise (±60 % of the height), a little higher on faces that look up (soil and moss settle
// on tops) than on overhangs.
float terrainBlendFactor(float3 P, float3 N, float ground, float strength, float height)
{
    float above = P.y - ground;
    float noise = terrainBlendNoise(P * 3.1) * 0.65 + terrainBlendNoise(P * 9.7 + 13.0) * 0.35;
    float edge = height * (1.0 + 0.35 * saturate(N.y)) + (noise - 0.5) * 1.2 * height;
    return strength * (1.0 - smoothstep(0.0, max(edge, 1e-3), above));
}

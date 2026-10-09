// Foliage wind (ADR 0151): the vertex offset FoliageMaterial3D's colour and shadow passes apply, so leaves and their
// shadows sway together. A port of Ez Tree's leaf shader (https://github.com/dgreenheck/ez-tree, src/lib/tree.js,
// MIT, (c) Daniel Greenheck): three sines with a simplex-noise phase over the position, plus a branch bend Ez Tree
// does not have. simplex3 is Ashima Arts' webgl-noise (https://github.com/ashima/webgl-noise, MIT, (c) 2011 Ashima
// Arts, Stefan Gustavson), as Ez Tree uses it.
//
// Deviations from Ez Tree: the noise is sampled in world space (instances of one mesh sway out of step), the frequency
// is in Hz (WorldEnvironment.WindFrequency), and the sway is scaled to metres (kLeafFlutter, kBranchBend).
#pragma once

#include "common.slang"

float3 windMod289(float3 x) { return x - floor(x * (1.0 / 289.0)) * 289.0; }
float4 windMod289(float4 x) { return x - floor(x * (1.0 / 289.0)) * 289.0; }
float4 windPermute(float4 x) { return windMod289(((x * 34.0) + 1.0) * x); }
float4 windTaylorInvSqrt(float4 r) { return 1.79284291400159 - 0.85373472095314 * r; }

// Simplex noise in 3D, about -1..1.
float simplex3(float3 v)
{
    const float2 C = float2(1.0 / 6.0, 1.0 / 3.0);
    const float4 D = float4(0.0, 0.5, 1.0, 2.0);

    // First corner
    float3 i = floor(v + dot(v, C.yyy));
    float3 x0 = v - i + dot(i, C.xxx);

    // Other corners
    float3 g = step(x0.yzx, x0.xyz);
    float3 l = 1.0 - g;
    float3 i1 = min(g.xyz, l.zxy);
    float3 i2 = max(g.xyz, l.zxy);

    float3 x1 = x0 - i1 + C.xxx;
    float3 x2 = x0 - i2 + C.yyy;
    float3 x3 = x0 - D.yyy;

    // Permutations
    i = windMod289(i);
    float4 p = windPermute(windPermute(windPermute(
                   i.z + float4(0.0, i1.z, i2.z, 1.0))
               + i.y + float4(0.0, i1.y, i2.y, 1.0))
               + i.x + float4(0.0, i1.x, i2.x, 1.0));

    // Gradients: 7x7 points over a square, mapped onto an octahedron.
    float n_ = 0.142857142857; // 1/7
    float3 ns = n_ * D.wyz - D.xzx;

    float4 j = p - 49.0 * floor(p * ns.z * ns.z); // mod(p, 7*7)

    float4 x_ = floor(j * ns.z);
    float4 y_ = floor(j - 7.0 * x_); // mod(j, N)

    float4 x = x_ * ns.x + ns.yyyy;
    float4 y = y_ * ns.x + ns.yyyy;
    float4 h = 1.0 - abs(x) - abs(y);

    float4 b0 = float4(x.xy, y.xy);
    float4 b1 = float4(x.zw, y.zw);

    float4 s0 = floor(b0) * 2.0 + 1.0;
    float4 s1 = floor(b1) * 2.0 + 1.0;
    float4 sh = -step(h, float4(0.0));

    float4 a0 = b0.xzyw + s0.xzyw * sh.xxyy;
    float4 a1 = b1.xzyw + s1.xzyw * sh.zzww;

    float3 g0 = float3(a0.xy, h.x);
    float3 g1 = float3(a0.zw, h.y);
    float3 g2 = float3(a1.xy, h.z);
    float3 g3 = float3(a1.zw, h.w);

    // Normalise gradients
    float4 norm = windTaylorInvSqrt(float4(dot(g0, g0), dot(g1, g1), dot(g2, g2), dot(g3, g3)));
    g0 *= norm.x;
    g1 *= norm.y;
    g2 *= norm.z;
    g3 *= norm.w;

    // Mix contributions from the four corners
    float4 m = max(0.6 - float4(dot(x0, x0), dot(x1, x1), dot(x2, x2), dot(x3, x3)), 0.0);
    m = m * m;
    return 42.0 * dot(m * m, float4(dot(g0, x0), dot(g1, x1), dot(g2, x2), dot(g3, x3)));
}

// Metres of leaf flutter (at the leaf tip) and of branch bend (at a twig tip, Custom0.x = 1) per unit of wind strength.
static const float kLeafFlutter = 0.12;
static const float kBranchBend = 0.35;

// The world-space offset of a foliage vertex at `worldPos` (before the sway).
//   custom0:   x = wind weight (0 at the trunk base, 1 at the tips), y = branch level / 4 (1 = leaf), z = phase 0..1
//   uv:        the vertex UV, v = 0 at the leaf tip (Ez Tree's uv.y, flipped for the top-left origin)
//   wind:      FrameData.wind (xyz = direction, w = strength); windParams: x = Hz, y = turbulence, z = noise scale (m)
//   time:      seconds (FrameData.clip.z)
//   strength, bend: the material's WindStrength and WindBranchBend scales
float3 windOffset(float3 worldPos, float4 custom0, float2 uv, float4 wind, float4 windParams, float time, float strength,
                  float bend)
{
    float s = wind.w * strength;
    if (s <= 0.0)
        return float3(0.0);

    float3 dir = wind.xyz;
    float3 side = cross(float3(0.0, 1.0, 0.0), dir);
    side = dot(side, side) > 1e-6 ? normalize(side) : float3(1.0, 0.0, 0.0);
    float w = 2.0 * PI * windParams.x; // rad/s
    float t = time;

    // Leaf flutter: Ez Tree's three sines, phased by noise over the position; turbulence adds a sideways share.
    float o = 2.0 * 3.14 * simplex3(worldPos / max(windParams.z, 1e-3));
    float sway = 0.5 * sin(t * w + o) + 0.3 * sin(2.0 * t * w + 1.3 * o) + 0.2 * sin(5.0 * t * w + 1.5 * o);
    float leaf = step(0.999, custom0.y);
    float3 flutter = leaf * saturate(1.0 - uv.y) * kLeafFlutter * s * sway *
                     (dir + windParams.y * side * sin(1.7 * t * w + 0.7 * o));

    // Branch bend: a lean downwind with a slow oscillation, weighted by the squared wind weight, phased per branch.
    float weight = saturate(custom0.x);
    float3 lean = bend * weight * weight * kBranchBend * s *
                  (0.6 + 0.4 * sin(0.37 * t * w + 2.0 * PI * custom0.z)) * dir;
    return flutter + lean;
}

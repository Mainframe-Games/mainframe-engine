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

// Leaf flutter at `worldPos` for the effective strength `s` (wind × material): Ez Tree's three sines, phased by noise
// over the position; turbulence adds a sideways share. Leaves (y = 1) flutter more towards their tip (v = 0); leaf-cluster
// cards (ADR 0172) carry their tip factor in y = 0.75..1 (0 at the twig's base), since an atlas cell's v does not span
// 0..1; bark (y <= 0.75) does not flutter.
float3 windFlutter(float3 worldPos, float4 custom0, float2 uv, float4 wind, float4 windParams, float time, float s)
{
    float3 dir = wind.xyz;
    float3 side = cross(float3(0.0, 1.0, 0.0), dir);
    side = dot(side, side) > 1e-6 ? normalize(side) : float3(1.0, 0.0, 0.0);
    float w = 2.0 * PI * windParams.x; // rad/s
    float t = time;
    float o = 2.0 * 3.14 * simplex3(worldPos / max(windParams.z, 1e-3));
    float sway = 0.5 * sin(t * w + o) + 0.3 * sin(2.0 * t * w + 1.3 * o) + 0.2 * sin(5.0 * t * w + 1.5 * o);
    float leaf = step(0.999, custom0.y);
    float tip = leaf * saturate(1.0 - uv.y) + (1.0 - leaf) * saturate((custom0.y - 0.75) * 4.0);
    return tip * kLeafFlutter * s * sway * (dir + windParams.y * side * sin(1.7 * t * w + 0.7 * o));
}

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

    float3 flutter = windFlutter(worldPos, custom0, uv, wind, windParams, time, s);

    // Branch bend: a lean downwind with a slow oscillation, weighted by the squared wind weight, phased per branch.
    float w = 2.0 * PI * windParams.x; // rad/s
    float weight = saturate(custom0.x);
    float3 lean = bend * weight * weight * kBranchBend * s *
                  (0.6 + 0.4 * sin(0.37 * time * w + 2.0 * PI * custom0.z)) * wind.xyz;
    return flutter + lean;
}

// ── Hierarchical wind (ADR 0172, Pivot Painter-like) ──────────────────────────────────────────────────────────────

// Radians of bend per unit of wind strength / stiffness: a level-1 branch around its pivot, a level-2 twig around its own;
// the trunk's sway per unit of strength and per 10 m of height (it bends: the angle grows with height).
static const float kBranchAngle = 0.22;
static const float kTwigAngle = 0.32;
static const float kTrunkSway = 0.010;

// Stiffness at or above which a pivot does not bend (trunk vertices carry it).
static const float kRigid = 1000.0;

float windHash(float3 p) { return frac(sin(dot(p, float3(12.9898, 78.233, 37.719))) * 43758.5453); }

// `p` turned by `angle` (radians, right-handed) about the unit `axis` through `pivot` (Rodrigues).
float3 windRotate(float3 p, float3 pivot, float3 axis, float angle)
{
    float3 v = p - pivot;
    float c = cos(angle);
    float s = sin(angle);
    return pivot + v * c + cross(axis, v) * s + axis * dot(axis, v) * (1.0 - c);
}

// The axis that leans `along` (a branch direction) towards the wind; zero when they are parallel.
float3 windLeanAxis(float3 along, float3 dir)
{
    float3 axis = cross(along, dir);
    float len = length(axis);
    return len > 1e-4 ? axis / len : float3(0.0);
}

// The world position of a foliage vertex under the hierarchical wind (rest pose → swayed), from the root down as
// rotations, so branches keep their length: the twig bends about its level-2 pivot (custom2), the branch about its
// level-1 pivot (custom1; both object space, w = stiffness), the trunk sways about the instance origin (more with
// height), then leaves flutter. A slow gust noise rolls along the wind over the instance origins, so neighbouring trees
// move together. Every vertex of one branch turns the same way, so leaves stay on their twigs.
//   model: the instance's model matrix (pivots and origin), trunk: the material's trunk sway scale.
float3 windHierarchy(float3 worldPos, float4x4 model, float4 custom0, float4 custom1, float4 custom2, float2 uv, float4 wind,
                     float4 windParams, float time, float strength, float bend, float trunk)
{
    float s = wind.w * strength;
    if (s <= 0.0)
        return worldPos;

    float3 dir = wind.xyz;
    float3 origin = model[3].xyz;
    float w = 2.0 * PI * windParams.x;
    float t = time;
    float gust = 1.0 + 0.4 * simplex3(float3(origin.xz * 0.015 - dir.xz * t * 0.08, t * 0.05));
    float sg = s * gust;

    float3 p = worldPos;
    float3 pivot1 = mul(float4(custom1.xyz, 1.0), model).xyz;
    float3 pivot2 = mul(float4(custom2.xyz, 1.0), model).xyz;
    if (custom2.w > 0.0 && custom2.w < kRigid)
    {
        float3 axis = windLeanAxis(normalize(p - pivot2 + float3(0.0, 1e-3, 0.0)), dir);
        float angle = bend * sg * kTwigAngle / custom2.w * (0.6 + 0.4 * sin(2.1 * t * w + 2.0 * PI * windHash(pivot2)));
        p = windRotate(p, pivot2, axis, angle);
    }

    if (custom1.w < kRigid)
    {
        float3 along = pivot2 - pivot1;
        along = dot(along, along) > 1e-6 ? normalize(along) : normalize(worldPos - pivot1 + float3(0.0, 1e-3, 0.0));
        float3 axis = windLeanAxis(along, dir);
        float angle = bend * sg * kBranchAngle / custom1.w * (0.6 + 0.4 * sin(1.3 * t * w + 2.0 * PI * windHash(pivot1)));
        p = windRotate(p, pivot1, axis, angle);
    }

    float height = max(worldPos.y - origin.y, 0.0);
    float sway = trunk * sg * kTrunkSway * (height / 10.0) * (0.7 + 0.3 * sin(0.6 * t * w + 2.0 * PI * windHash(origin)));
    p = windRotate(p, origin, windLeanAxis(float3(0.0, 1.0, 0.0), dir), sway);

    return p + windFlutter(worldPos, custom0, uv, wind, windParams, time, s);
}

// The swayed world position of a foliage vertex: the hierarchical wind with pivots (custom1.w > 0), else the simple wind.
float3 windPosition(float3 worldPos, float4x4 model, float4 custom0, float4 custom1, float4 custom2, float2 uv, float4 wind,
                    float4 windParams, float time, float strength, float bend, float trunk)
{
    if (custom1.w > 0.0)
        return windHierarchy(worldPos, model, custom0, custom1, custom2, uv, wind, windParams, time, strength, bend, trunk);
    return worldPos + windOffset(worldPos, custom0, uv, wind, windParams, time, strength, bend);
}

// The spatial upscale (ADR 0174, SpatialUpscaleEffect): the render-resolution HDR scene colour to the output resolution,
// into a pooled output-sized target that then becomes the output colour. Paired with Fullscreen.vk.vert; one output pixel
// per fragment.
//
// Mode 0 is a bilinear tap. Mode 1 is EASU, the edge-adaptive spatial upsampling of AMD FidelityFX FSR 1 (MIT; the
// algorithm of ffx_fsr1.h's FsrEasuF, re-implemented in Slang with plain loads instead of gathers): a 12-tap window around
// the output pixel's position in the input, an edge direction and length from the luma of the four nearest 2 x 2 quads,
// a Lanczos-2 approximation stretched along the edge, and deringing to the min/max of the four nearest texels. EASU expects
// perceptual values, so it runs on a reversible tonemap of the HDR colour (c / (1 + max(c))) and undoes it at the end.
//
// EASU: Copyright (c) 2021 Advanced Micro Devices, Inc. All rights reserved. MIT licence (AMD FidelityFX FSR 1.0); the
// full notice is in THIRD_PARTY_NOTICES.md.
#include "common.slang"

[[vk::binding(0, 0)]] Sampler2D source; // linear HDR at render resolution (bilinear sampler)

struct Upscale
{
    float2 inputSize;  // render resolution, pixels
    float2 outputSize; // output resolution, pixels
    uint mode;         // 0 bilinear, 1 EASU
};

[[vk::push_constant]] ConstantBuffer<Upscale> pc;

static const float kMaxInput = 65000.0;

float3 tonemapIn(float3 c)
{
    c = clamp(c, float3(0.0), float3(kMaxInput));
    return c / (1.0 + max(c.r, max(c.g, c.b)));
}

float3 tonemapOut(float3 c)
{
    return c / max(1.0 - max(c.r, max(c.g, c.b)), 1.0 / 65536.0);
}

float3 tap(int2 p, int2 last)
{
    return tonemapIn(source.Load(int3(clamp(p, int2(0), last), 0)).rgb);
}

// FSR's luma times two: 0.5 r + g + 0.5 b.
float luma2(float3 c)
{
    return c.b * 0.5 + (c.r * 0.5 + c.g);
}

// FsrEasuSetF: the bilinear weight w of one 2 x 2 quad's corner, the '+' gradient around its centre c (a above, b left,
// d right, e below): direction from the outer differences, length from how one-sided the change is.
void easuSet(inout float2 dir, inout float len, float w, float lA, float lB, float lC, float lD, float lE)
{
    float dc = lD - lC;
    float cb = lC - lB;
    float lenX = max(abs(dc), abs(cb));
    lenX = 1.0 / max(lenX, 1e-8);
    float dirX = lD - lB;
    dir.x += dirX * w;
    lenX = saturate(abs(dirX) * lenX);
    lenX *= lenX;
    len += lenX * w;

    float ec = lE - lC;
    float ca = lC - lA;
    float lenY = max(abs(ec), abs(ca));
    lenY = 1.0 / max(lenY, 1e-8);
    float dirY = lE - lA;
    dir.y += dirY * w;
    lenY = saturate(abs(dirY) * lenY);
    lenY *= lenY;
    len += lenY * w;
}

// FsrEasuTapF: one tap at offset `off` from the position, rotated into the edge's frame and stretched by len2.
void easuTap(inout float3 aC, inout float aW, float2 off, float2 dir, float2 len2, float lob, float clp, float3 c)
{
    float2 v;
    v.x = off.x * dir.x + off.y * dir.y;
    v.y = off.x * -dir.y + off.y * dir.x;
    v *= len2;
    float d2 = min(v.x * v.x + v.y * v.y, clp);
    // (25/16 (2/5 x^2 - 1)^2 - (25/16 - 1)) (lob x^2 - 1)^2: Lanczos 2 without sin, rcp or sqrt.
    float wB = 2.0 / 5.0 * d2 - 1.0;
    float wA = lob * d2 - 1.0;
    wB *= wB;
    wA *= wA;
    wB = 25.0 / 16.0 * wB - (25.0 / 16.0 - 1.0);
    float w = wB * wA;
    aC += c * w;
    aW += w;
}

float3 easu(float2 fragCoord)
{
    int2 last = int2(pc.inputSize) - 1;
    float2 scale = pc.inputSize / pc.outputSize;
    // The output pixel's centre in input pixels, relative to the centre of texel f (FSR's con0: ip * scale + 0.5 scale - 0.5).
    float2 pp = floor(fragCoord) * scale + (0.5 * scale - 0.5);
    float2 fp = floor(pp);
    pp -= fp;
    int2 f0 = int2(fp);

    //    b c
    //  e f g h
    //  i j k l
    //    n o
    float3 b = tap(f0 + int2(0, -1), last);
    float3 c = tap(f0 + int2(1, -1), last);
    float3 e = tap(f0 + int2(-1, 0), last);
    float3 f = tap(f0, last);
    float3 g = tap(f0 + int2(1, 0), last);
    float3 h = tap(f0 + int2(2, 0), last);
    float3 i = tap(f0 + int2(-1, 1), last);
    float3 j = tap(f0 + int2(0, 1), last);
    float3 k = tap(f0 + int2(1, 1), last);
    float3 l = tap(f0 + int2(2, 1), last);
    float3 n = tap(f0 + int2(0, 2), last);
    float3 o = tap(f0 + int2(1, 2), last);

    float bL = luma2(b), cL = luma2(c), eL = luma2(e), fL = luma2(f), gL = luma2(g), hL = luma2(h);
    float iL = luma2(i), jL = luma2(j), kL = luma2(k), lL = luma2(l), nL = luma2(n), oL = luma2(o);

    float2 dir = float2(0.0);
    float len = 0.0;
    easuSet(dir, len, (1.0 - pp.x) * (1.0 - pp.y), bL, eL, fL, gL, jL);
    easuSet(dir, len, pp.x * (1.0 - pp.y), cL, fL, gL, hL, kL);
    easuSet(dir, len, (1.0 - pp.x) * pp.y, fL, iL, jL, kL, nL);
    easuSet(dir, len, pp.x * pp.y, gL, jL, kL, lL, oL);

    // Normalise the direction (no direction: along x), shape the length.
    float2 dir2 = dir * dir;
    float dirR = dir2.x + dir2.y;
    bool zero = dirR < 1.0 / 32768.0;
    dirR = zero ? 1.0 : rsqrt(dirR);
    dir.x = zero ? 1.0 : dir.x;
    dir *= dirR;
    len = len * 0.5;
    len *= len;
    // Stretch the kernel: 1 along an axis, sqrt(2) on the diagonal; anisotropy grows with the edge.
    float stretch = (dir.x * dir.x + dir.y * dir.y) / max(max(abs(dir.x), abs(dir.y)), 1e-8);
    float2 len2 = float2(1.0 + (stretch - 1.0) * len, 1.0 - 0.5 * len);
    float lob = 0.5 + ((1.0 / 4.0 - 0.04) - 0.5) * len;
    float clp = 1.0 / lob;

    float3 min4 = min(min(f, g), min(j, k));
    float3 max4 = max(max(f, g), max(j, k));

    float3 aC = float3(0.0);
    float aW = 0.0;
    easuTap(aC, aW, float2(0.0, -1.0) - pp, dir, len2, lob, clp, b);
    easuTap(aC, aW, float2(1.0, -1.0) - pp, dir, len2, lob, clp, c);
    easuTap(aC, aW, float2(-1.0, 1.0) - pp, dir, len2, lob, clp, i);
    easuTap(aC, aW, float2(0.0, 1.0) - pp, dir, len2, lob, clp, j);
    easuTap(aC, aW, float2(0.0, 0.0) - pp, dir, len2, lob, clp, f);
    easuTap(aC, aW, float2(-1.0, 0.0) - pp, dir, len2, lob, clp, e);
    easuTap(aC, aW, float2(1.0, 1.0) - pp, dir, len2, lob, clp, k);
    easuTap(aC, aW, float2(2.0, 1.0) - pp, dir, len2, lob, clp, l);
    easuTap(aC, aW, float2(2.0, 0.0) - pp, dir, len2, lob, clp, h);
    easuTap(aC, aW, float2(1.0, 0.0) - pp, dir, len2, lob, clp, g);
    easuTap(aC, aW, float2(1.0, 2.0) - pp, dir, len2, lob, clp, o);
    easuTap(aC, aW, float2(0.0, 2.0) - pp, dir, len2, lob, clp, n);

    // Normalise and dering.
    float3 result = min(max4, max(min4, aC / aW));
    return tonemapOut(result);
}

[shader("fragment")]
float4 main(float4 fragCoord : SV_Position) : SV_Target0
{
    if (pc.mode == 1u)
        return float4(easu(fragCoord.xy), 1.0);
    return float4(source.SampleLevel(fragCoord.xy / pc.outputSize, 0.0).rgb, 1.0);
}

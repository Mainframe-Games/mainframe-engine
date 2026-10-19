// The colour grade and film effects (ADR 0168), one AfterTonemap pass on the display-encoded LDR image, in this order:
// lateral chromatic aberration (red and blue read radially apart), Godot's adjustments (brightness, contrast, saturation
// on the display value, as Godot 4's tonemap.glsl apply_bcs), the 3D colour-correction LUT (trilinear, texel-centre
// mapped, mixed by strength), the vignette (in linear light) and film grain (temporally varying value noise, strongest
// in the mid-tones). Writes display values, or linear ones when the output view encodes sRGB itself.
#include "common.slang"

[[vk::binding(0, 0)]] Sampler2D ldr; // the stage's LDR input, bilinear, clamp to edge
[[vk::binding(0, 1)]] Sampler3D lut; // the colour-correction LUT (an identity 2³ when there is none)

struct Grade
{
    float2 rcpSize;      // 1 / image size
    float  aspect;       // width / height
    uint   flags;        // 1: adjustments, 2: LUT, 4: decode sRGB before writing
    float  brightness;
    float  contrast;
    float  saturation;
    float  lutStrength;
    float  lutScale;     // (N − 1) / N: maps 0–1 onto the LUT's texel centres
    float  lutOffset;    // 0.5 / N
    float  vignette;     // intensity (0: off)
    float  roundness;
    float  grain;        // intensity in display values (0: off)
    float  grainSize;    // pixels
    uint   grainSeed;    // the frame number
    float  aberration;   // pixels at the corners (0: off)
};

[[vk::push_constant]] ConstantBuffer<Grade> pc;

static const float3 kLuma = float3(0.2126, 0.7152, 0.0722);

// pcg3d (Jarzynski & Olano 2020): three well-mixed 32-bit hashes.
uint3 pcg3d(uint3 v)
{
    v = v * 1664525u + 1013904223u;
    v.x += v.y * v.z; v.y += v.z * v.x; v.z += v.x * v.y;
    v ^= v >> 16u;
    v.x += v.y * v.z; v.y += v.z * v.x; v.z += v.x * v.y;
    return v;
}

// A value in [−1, 1] per lattice point and frame.
float latticeNoise(int2 cell, uint seed)
{
    return float(pcg3d(uint3(uint(cell.x), uint(cell.y), seed)).x) * (2.0 / 4294967295.0) - 1.0;
}

// Smooth value noise over cells of `grainSize` pixels, scaled back up towards the lattice's spread.
float grainNoise(float2 pixel)
{
    float2 p = pixel / max(pc.grainSize, 0.25);
    int2 i = int2(floor(p));
    float2 f = frac(p);
    f = f * f * (3.0 - 2.0 * f);
    float a = latticeNoise(i, pc.grainSeed);
    float b = latticeNoise(i + int2(1, 0), pc.grainSeed);
    float c = latticeNoise(i + int2(0, 1), pc.grainSeed);
    float d = latticeNoise(i + int2(1, 1), pc.grainSeed);
    return clamp(lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y) * 1.6, -1.0, 1.0);
}

[shader("fragment")]
float4 main(float4 fragCoord : SV_Position) : SV_Target0
{
    float2 uv = fragCoord.xy * pc.rcpSize;
    float3 color;
    if (pc.aberration > 0.0)
    {
        // Radial, |offset| = aberration pixels at a corner (|uv − 0.5| = √0.5 there).
        float2 offset = (uv - 0.5) * (pc.aberration * 1.41421356) * pc.rcpSize;
        color = float3(ldr.SampleLevel(uv + offset, 0.0).r, ldr.SampleLevel(uv, 0.0).g, ldr.SampleLevel(uv - offset, 0.0).b);
    }
    else
    {
        color = ldr.Load(int3(int2(fragCoord.xy), 0)).rgb;
    }

    if ((pc.flags & 1u) != 0u)
    {
        color = color * pc.brightness;
        color = lerp(float3(0.5), color, pc.contrast);
        color = lerp(float3(dot(float3(1.0), color) * 0.33333), color, pc.saturation);
    }

    if ((pc.flags & 2u) != 0u)
    {
        color = saturate(color);
        float3 graded = lut.SampleLevel(color * pc.lutScale + pc.lutOffset, 0.0).rgb;
        color = lerp(color, graded, pc.lutStrength);
    }

    color = saturate(color);
    if (pc.vignette > 0.0)
    {
        float stretch = lerp(1.0, pc.aspect, saturate(pc.roundness));
        float2 d = (uv - 0.5) * float2(stretch, 1.0);
        float2 corner = float2(0.5 * stretch, 0.5);
        float r2 = dot(d, d) / dot(corner, corner); // 0 at the centre, 1 in the corners
        float factor = saturate(1.0 - pc.vignette * r2 * sqrt(r2));
        color = linearToSrgb(srgbToLinear(color) * factor);
    }

    if (pc.grain > 0.0)
    {
        float luma = dot(color, kLuma);
        float response = 0.25 + 3.0 * luma * (1.0 - luma); // 0.25 in black and white, 1 in the mid-tones
        color = saturate(color + grainNoise(fragCoord.xy) * pc.grain * response);
    }

    if ((pc.flags & 4u) != 0u)
        color = srgbToLinear(color);
    return float4(color, 1.0);
}

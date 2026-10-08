// Shared constants and colour-space helpers. Include with: #include "common.slang"
#pragma once

#include "limits.slang"

static const float PI = 3.14159265358979323846;

// sRGB transfer functions (IEC 61966-2-1), per channel. Colours authored in sRGB (UI pickers, vertex colours,
// System.Drawing.Color) become linear before lighting; the swapchain (or tonemap) encodes back.
float3 srgbToLinear(float3 c)
{
    c = max(c, float3(0.0));
    return lerp(c / 12.92, pow((c + 0.055) / 1.055, float3(2.4)), step(float3(0.04045), c));
}

float3 linearToSrgb(float3 c)
{
    c = max(c, float3(0.0));
    return lerp(c * 12.92, 1.055 * pow(c, float3(1.0 / 2.4)) - 0.055, step(float3(0.0031308), c));
}

// GLSL's mod: x - y * floor(x / y) (the sign follows y). HLSL's fmod differs for negative operands; never use it.
float glsl_mod(float x, float y) { return x - y * floor(x / y); }
float2 glsl_mod(float2 x, float2 y) { return x - y * floor(x / y); }
float3 glsl_mod(float3 x, float3 y) { return x - y * floor(x / y); }
float4 glsl_mod(float4 x, float4 y) { return x - y * floor(x / y); }

// textureSize(s, lod).xy of a 2D texture.
int2 textureSize2D(Sampler2D s, int lod)
{
    uint w, h, levels;
    s.GetDimensions(uint(lod), w, h, levels);
    return int2(int(w), int(h));
}

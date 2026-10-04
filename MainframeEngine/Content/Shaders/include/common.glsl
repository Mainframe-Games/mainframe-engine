// Shared constants and colour-space helpers. Include with: #extension GL_GOOGLE_include_directive : require
#ifndef MF_COMMON_GLSL
#define MF_COMMON_GLSL

#include "limits.glsl"

const float PI = 3.14159265358979323846;

// sRGB transfer functions (IEC 61966-2-1), per channel. Colours authored in sRGB (UI pickers, vertex colours,
// System.Drawing.Color) become linear before lighting; the swapchain (or tonemap) encodes back.
vec3 srgbToLinear(vec3 c)
{
    c = max(c, vec3(0.0));
    return mix(c / 12.92, pow((c + 0.055) / 1.055, vec3(2.4)), step(vec3(0.04045), c));
}

vec3 linearToSrgb(vec3 c)
{
    c = max(c, vec3(0.0));
    return mix(c * 12.92, 1.055 * pow(c, vec3(1.0 / 2.4)) - 0.055, step(vec3(0.0031308), c));
}

#endif

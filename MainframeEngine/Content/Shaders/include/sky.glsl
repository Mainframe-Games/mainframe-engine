// Shared by the sky fragment shaders: SkyEnvironment's push constants (96 bytes) and the view ray of a
// fullscreen-triangle fragment, reconstructed from the per-frame camera (set 0).
#ifndef MF_SKY_GLSL
#define MF_SKY_GLSL

#include "frame.glsl"

layout(push_constant) uniform SkyParams {
    vec4 skyColor;          // rgb = zenith colour (sRGB-authored)
    vec4 horizonColor;      // rgb = horizon colour
    vec4 groundColor;       // rgb = ground / nadir colour
    vec4 sunDirection;      // xyz = world-space direction toward the sun (normalized)
    vec4 sunColorIntensity; // rgb = sun colour, a = intensity multiplier
    vec4 sun;               // x = cos(sun angular radius), y = horizon sharpness
} sky;

// World-space view direction through NDC position `ndc` (x, y in [-1, 1]; +y up with the flipped viewport).
vec3 skyRay(vec2 ndc)
{
    vec4 viewPos = frame.invProjection * vec4(ndc, 1.0, 1.0);
    viewPos /= viewPos.w;
    return normalize(mat3(frame.invViewRotation) * viewPos.xyz);
}

#endif

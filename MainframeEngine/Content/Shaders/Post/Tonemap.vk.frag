#version 450
#extension GL_GOOGLE_include_directive : require

// HDR scene (linear Rec.709, RGBA16F) → display: exposure, ACES filmic tonemap (Stephen Hill's fit of the RRT +
// sRGB ODT; ADR in memory/decisions), then sRGB encoding — by the swapchain's sRGB view, or here when the
// swapchain is UNORM. Mirrored in C# by ColorSpace.AcesFitted for tests.
#include "common.glsl"

layout(location = 0) in vec2 inNdc;
layout(location = 0) out vec4 outColor;

layout(set = 0, binding = 0) uniform sampler2D hdrScene;

layout(push_constant) uniform Tonemap {
    float exposure;
    uint  encodeSrgb; // 1: the target stores raw values (UNORM), so encode here
} pc;

// sRGB/Rec.709 → ACES AP1 with the RRT's saturation folded in (column-major: the HLSL rows are the columns).
const mat3 kAcesInput = mat3(
    0.59719, 0.07600, 0.02840,
    0.35458, 0.90834, 0.13383,
    0.04823, 0.01566, 0.83777);

// ODT → Rec.709.
const mat3 kAcesOutput = mat3(
     1.60475, -0.10208, -0.00327,
    -0.53108,  1.10813, -0.07276,
    -0.07367, -0.00605,  1.07602);

vec3 rrtAndOdtFit(vec3 v)
{
    vec3 a = v * (v + 0.0245786) - 0.000090537;
    vec3 b = v * (0.983729 * v + 0.4329510) + 0.238081;
    return a / b;
}

vec3 acesFitted(vec3 color)
{
    color = kAcesInput * color;
    color = rrtAndOdtFit(color);
    color = kAcesOutput * color;
    return clamp(color, 0.0, 1.0);
}

void main()
{
    vec3 hdr = texelFetch(hdrScene, ivec2(gl_FragCoord.xy), 0).rgb;
    vec3 color = acesFitted(max(hdr, vec3(0.0)) * pc.exposure);
    if (pc.encodeSrgb != 0u)
        color = linearToSrgb(color);
    outColor = vec4(color, 1.0);
}

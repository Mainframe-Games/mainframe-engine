#version 450
#extension GL_GOOGLE_include_directive : require

#include "sky.glsl"

layout(location = 0) in  vec2 inUV; // NDC
layout(location = 0) out vec4 outColor;

void main()
{
    vec3 worldDir = skyRay(inUV);
    float y = worldDir.y; // -1 = straight down, +1 = straight up
    float sharpness = sky.sun.y;

    // Colours are sRGB-authored; the gradient is built in linear space (HDR scene target).
    vec3 skyColor     = srgbToLinear(sky.skyColor.rgb);
    vec3 horizonColor = srgbToLinear(sky.horizonColor.rgb);
    vec3 groundColor  = srgbToLinear(sky.groundColor.rgb);

    // --- Sky / horizon / ground gradient: horizon colour at y = 0, blending to the zenith above and the ground below ---
    vec3 skyBlend    = mix(horizonColor, skyColor,    clamp( y * sharpness, 0.0, 1.0));
    vec3 groundBlend = mix(horizonColor, groundColor, clamp(-y * sharpness, 0.0, 1.0));
    vec3 color = y >= 0.0 ? skyBlend : groundBlend;

    // --- Sun disk ---
    vec3  sunDir  = normalize(sky.sunDirection.xyz);
    float sunDot  = dot(worldDir, sunDir);
    float sunSize = sky.sun.x;
    // Soft edge: transition over a tiny cosine band just inside the sun boundary.
    float softEdge = max(1.0 - sunSize, 0.0001);
    float sunMask  = smoothstep(sunSize - softEdge * 0.05, sunSize, sunDot);
    color += srgbToLinear(sky.sunColorIntensity.rgb) * sky.sunColorIntensity.a * sunMask; // HDR: tonemapped later

    outColor = vec4(color, 1.0);
}

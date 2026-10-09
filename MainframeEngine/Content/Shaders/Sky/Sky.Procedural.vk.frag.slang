#include "sky.slang"

[shader("fragment")]
float4 main([[vk::location(0)]] float2 inUV) : SV_Target0 // inUV: NDC
{
    float3 worldDir = skyRay(inUV);
    float y = worldDir.y; // -1 = straight down, +1 = straight up
    float sharpness = sky.sun.y;

    // Colours are sRGB-authored; the gradient is built in linear space (HDR scene target).
    float3 skyColor     = srgbToLinear(sky.skyColor.rgb);
    float3 horizonColor = srgbToLinear(sky.horizonColor.rgb);
    float3 groundColor  = srgbToLinear(sky.groundColor.rgb);

    // --- Sky / horizon / ground gradient: horizon colour at y = 0, blending to the zenith above and the ground below ---
    float3 skyBlend    = lerp(horizonColor, skyColor,    clamp( y * sharpness, 0.0, 1.0));
    float3 groundBlend = lerp(horizonColor, groundColor, clamp(-y * sharpness, 0.0, 1.0));
    float3 color = y >= 0.0 ? skyBlend : groundBlend;

    // --- Sun disk ---
    float3 sunDir  = normalize(sky.sunDirection.xyz);
    float  sunDot  = dot(worldDir, sunDir);
    float  sunSize = sky.sun.x;
    // Soft edge: transition over a tiny cosine band just inside the sun boundary.
    float softEdge = max(1.0 - sunSize, 0.0001);
    float sunMask  = smoothstep(sunSize - softEdge * 0.05, sunSize, sunDot) * (1.0 - sky.sun.z); // none in a capture
    color += srgbToLinear(sky.sunColorIntensity.rgb) * sky.sunColorIntensity.a * sunMask; // HDR: tonemapped later

    return float4(applySkyFog(color, worldDir), 1.0);
}

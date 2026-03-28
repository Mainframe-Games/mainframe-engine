#version 450

layout(location = 0) in  vec2 inUV;
layout(location = 0) out vec4 outColor;

layout(set = 0, binding = 0) uniform SkyUbo {
    mat4  invProj;          // inverse projection matrix
    mat4  invViewRot;       // inverse view-rotation (translation stripped)
    vec4  skyColor;         // rgb = zenith color
    vec4  horizonColor;     // rgb = horizon color
    vec4  groundColor;      // rgb = ground / nadir color
    vec4  sunDirection;     // xyz = world-space direction toward the sun (normalized)
    vec4  sunColorIntensity;// rgb = sun color,  a = intensity multiplier
    float sunSize;          // cos(sun angular radius)
    float horizonSharpness; // larger = sharper horizon transition
    float pad0, pad1;
} ubo;

void main()
{
    // --- Reconstruct world-space view direction ---
    vec4 viewPos = ubo.invProj * vec4(inUV, 1.0, 1.0);
    viewPos /= viewPos.w;
    vec3 worldDir = normalize(mat3(ubo.invViewRot) * viewPos.xyz);

    float y = worldDir.y; // -1 = straight down, +1 = straight up

    // --- Sky / horizon / ground gradient ---
    vec3 skyBlend = mix(ubo.horizonColor.rgb, ubo.skyColor.rgb,
                        clamp(y * ubo.horizonSharpness, 0.0, 1.0));
    vec3 groundBlend = mix(ubo.groundColor.rgb, ubo.horizonColor.rgb,
                           clamp(-y * ubo.horizonSharpness, 0.0, 1.0));
    vec3 color = y >= 0.0 ? skyBlend : groundBlend;

    // --- Sun disk ---
    vec3  sunDir = normalize(ubo.sunDirection.xyz);
    float sunDot = dot(worldDir, sunDir);
    // Soft edge: transition over a tiny cosine band just inside the sun boundary.
    float softEdge = max(1.0 - ubo.sunSize, 0.0001);
    float sunMask  = smoothstep(ubo.sunSize - softEdge * 0.05, ubo.sunSize, sunDot);
    color += ubo.sunColorIntensity.rgb * ubo.sunColorIntensity.a * sunMask;

    outColor = vec4(color, 1.0);
}

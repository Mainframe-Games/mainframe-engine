#version 450

#define PI 3.14159265358979323846

layout(location = 0) in  vec2 inUV;
layout(location = 0) out vec4 outColor;

layout(set = 0, binding = 0) uniform SkyUbo {
    mat4  invProj;
    mat4  invViewRot;
    vec4  skyColor; vec4 horizonColor; vec4 groundColor;
    vec4  sunDirection; vec4 sunColorIntensity;
    float sunSize; float horizonSharpness;
    float pad0, pad1;
} ubo;

layout(set = 1, binding = 0) uniform sampler2D panoramic;

void main()
{
    // Reconstruct world-space view direction
    vec4 viewPos = ubo.invProj * vec4(inUV, 1.0, 1.0);
    viewPos /= viewPos.w;
    vec3 dir = normalize(mat3(ubo.invViewRot) * viewPos.xyz);

    // Equirectangular UV mapping.
    // u: longitude mapped to [0,1], seam at atan2(0,-1) = ±PI
    // v: latitude mapped to [0,1], 0 = zenith (top of image), 1 = nadir
    float u = atan(dir.z, dir.x) / (2.0 * PI) + 0.5;
    float v = 0.5 - asin(clamp(dir.y, -1.0, 1.0)) / PI;

    outColor = texture(panoramic, vec2(u, v));
}

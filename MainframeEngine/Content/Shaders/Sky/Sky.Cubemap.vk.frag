#version 450

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

layout(set = 1, binding = 0) uniform samplerCube cubemap;

void main()
{
    // Reconstruct world-space view direction
    vec4 viewPos = ubo.invProj * vec4(inUV, 1.0, 1.0);
    viewPos /= viewPos.w;
    vec3 dir = normalize(mat3(ubo.invViewRot) * viewPos.xyz);

    outColor = texture(cubemap, dir);
}

#version 450
#extension GL_GOOGLE_include_directive : require

#include "sky.glsl"

layout(location = 0) in  vec2 inUV; // NDC
layout(location = 0) out vec4 outColor;

layout(set = 1, binding = 0) uniform samplerCube cubemap;

void main()
{
    outColor = texture(cubemap, skyRay(inUV));
}

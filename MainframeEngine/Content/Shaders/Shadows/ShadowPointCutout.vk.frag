#version 450
#extension GL_GOOGLE_include_directive : require

// Point-light shadow pass of cutout materials: the alpha test of ShadowCutout.vk.frag, then the linear distance of
// ShadowPoint.vk.frag.
#define MATERIAL_SET 1
#include "material.glsl"

layout(location = 0) in vec3 inFragWorldPos;
layout(location = 1) in vec2 inUV;

layout(push_constant) uniform PC {
    mat4 model;
    vec4 lightPosRange; // xyz = light world position, w = range
} pc;

void main()
{
    vec2 uv = materialUv(inUV);
    if (materialAlbedo(uv, dFdx(uv), dFdy(uv)).a < material.params.z)
        discard;
    gl_FragDepth = length(inFragWorldPos - pc.lightPosRange.xyz) / pc.lightPosRange.w;
}

#version 450

layout(location = 0) in vec3 inFragWorldPos;

layout(push_constant) uniform PC {
    mat4  model;
    vec4  lightPosRange; // xyz = light world position, w = range
} pc;

void main()
{
    // Store linear distance [0,1] so it can be compared directly in the main pass.
    gl_FragDepth = length(inFragWorldPos - pc.lightPosRange.xyz) / pc.lightPosRange.w;
}

#version 450

layout(location = 0) in vec3 inPosition;

layout(set = 0, binding = 0) uniform ViewProjection {
    mat4 view;
    mat4 projection;
} vp;

layout(push_constant) uniform PushConstants {
    mat4 model;
    vec4 color;
} pc;

void main()
{
    gl_Position   = vp.projection * vp.view * pc.model * vec4(inPosition, 1.0);
}

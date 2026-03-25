#version 450

layout(location = 0) in vec3 aPos;
layout(location = 1) in vec2 aTexCoord;

layout(location = 0) out vec2 fragTexCoord;

layout(push_constant) uniform PC {
    mat4 model;
} pc;

void main() {
    gl_Position = pc.model * vec4(aPos, 1.0);
    fragTexCoord = aTexCoord;
}

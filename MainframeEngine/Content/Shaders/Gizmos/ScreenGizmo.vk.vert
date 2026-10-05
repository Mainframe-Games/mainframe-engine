#version 450

layout(location = 0) in vec2 inPosition; // framebuffer pixels, top-left origin
layout(location = 1) in vec4 inColor;    // sRGB, straight alpha

// pixel -> clip space: scale = 2 / extent, translate = (-1, -1)
layout(push_constant) uniform Push {
    vec2 scale;
    vec2 translate;
} push;

layout(location = 0) out vec4 fragColor;

void main() {
    fragColor = inColor;
    gl_Position = vec4(inPosition * push.scale + push.translate, 0.0, 1.0);
}

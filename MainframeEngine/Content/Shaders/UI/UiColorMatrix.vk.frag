#version 450

// Colour-matrix filters (brightness, contrast, invert, grayscale, sepia, hue-rotate, saturate), from RmlUi's GL3
// backend: an RGB transform applied directly in premultiplied space (the constant column scales with alpha).
layout(location = 0) in vec2 inTexCoord;

layout(set = 0, binding = 0) uniform sampler2D source;

layout(push_constant) uniform ColorMatrixPush {
    layout(offset = 16) mat4 colorMatrix;
} pc;

layout(location = 0) out vec4 outColor;

void main()
{
    vec4 texel = texture(source, inTexCoord);
    outColor = vec4(vec3(pc.colorMatrix * texel), texel.a);
}

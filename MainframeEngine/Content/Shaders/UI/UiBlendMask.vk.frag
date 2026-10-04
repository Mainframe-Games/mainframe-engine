#version 450

// mask-image: the layer multiplied by the mask layer's alpha.
layout(location = 0) in vec2 inTexCoord;

layout(set = 0, binding = 0) uniform sampler2D source;
layout(set = 1, binding = 0) uniform sampler2D mask;

layout(location = 0) out vec4 outColor;

void main()
{
    outColor = texture(source, inTexCoord) * texture(mask, inTexCoord).a;
}

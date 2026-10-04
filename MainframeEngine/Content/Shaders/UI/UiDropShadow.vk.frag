#version 450

// drop-shadow: the layer's alpha tinted with the (premultiplied) shadow colour, sampled at an offset and limited to
// the filtered region. Blurred afterwards when the shadow has a radius.
layout(location = 0) in vec2 inTexCoord;

layout(set = 0, binding = 0) uniform sampler2D source;

layout(push_constant) uniform DropShadowPush {
    layout(offset = 16) vec4 color;
    vec2 texCoordMin;
    vec2 texCoordMax;
} pc;

layout(location = 0) out vec4 outColor;

void main()
{
    vec2 inRegion = step(pc.texCoordMin, inTexCoord) * step(inTexCoord, pc.texCoordMax);
    outColor = texture(source, inTexCoord).a * inRegion.x * inRegion.y * pc.color;
}

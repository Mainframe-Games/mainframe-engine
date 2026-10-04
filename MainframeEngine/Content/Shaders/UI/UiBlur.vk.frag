#version 450

// One separable Gaussian pass (7 taps) of the blur / drop-shadow filters, from RmlUi's GL3 backend. Taps outside
// [texCoordMin, texCoordMax] (the filtered region) contribute nothing, so nothing bleeds in from outside it.
#define BLUR_SIZE 7
#define BLUR_NUM_WEIGHTS 4

layout(location = 0) in vec2 inTexCoord;

layout(set = 0, binding = 0) uniform sampler2D source;

layout(push_constant) uniform BlurPush {
    layout(offset = 16) vec2 texelOffset;
    vec2 texCoordMin;
    vec2 texCoordMax;
    layout(offset = 48) vec4 weights; // BLUR_NUM_WEIGHTS
} pc;

layout(location = 0) out vec4 outColor;

void main()
{
    vec4 color = vec4(0.0);
    for (int i = 0; i < BLUR_SIZE; i++)
    {
        vec2 uv = inTexCoord - float(i - BLUR_NUM_WEIGHTS + 1) * pc.texelOffset;
        vec2 inRegion = step(pc.texCoordMin, uv) * step(uv, pc.texCoordMax);
        color += texture(source, uv) * inRegion.x * inRegion.y * pc.weights[abs(i - BLUR_NUM_WEIGHTS + 1)];
    }
    outColor = color;
}

// One separable Gaussian pass (7 taps) of the blur / drop-shadow filters, from RmlUi's GL3 backend. Taps outside
// [texCoordMin, texCoordMax] (the filtered region) contribute nothing, so nothing bleeds in from outside it.
#define BLUR_SIZE 7
#define BLUR_NUM_WEIGHTS 4

[[vk::binding(0, 0)]] Sampler2D source;

struct BlurPush
{
    [[vk::offset(16)]] float2 texelOffset;
    float2 texCoordMin;
    float2 texCoordMax;
    [[vk::offset(48)]] float4 weights; // BLUR_NUM_WEIGHTS
};

[[vk::push_constant]] ConstantBuffer<BlurPush> pc;

[shader("fragment")]
float4 main([[vk::location(0)]] float2 inTexCoord) : SV_Target0
{
    float4 color = float4(0.0);
    for (int i = 0; i < BLUR_SIZE; i++)
    {
        float2 uv = inTexCoord - float(i - BLUR_NUM_WEIGHTS + 1) * pc.texelOffset;
        float2 inRegion = step(pc.texCoordMin, uv) * step(uv, pc.texCoordMax);
        color += source.Sample(uv) * inRegion.x * inRegion.y * pc.weights[abs(i - BLUR_NUM_WEIGHTS + 1)];
    }
    return color;
}

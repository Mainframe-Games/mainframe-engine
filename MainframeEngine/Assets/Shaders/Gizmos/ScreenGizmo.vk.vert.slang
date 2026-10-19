// pixel -> clip space: scale = 2 / extent, translate = (-1, -1)
struct Push
{
    float2 scale;
    float2 translate;
};

[[vk::push_constant]] ConstantBuffer<Push> push;

struct VsOut
{
    float4 position : SV_Position;
    [[vk::location(0)]] float4 fragColor;
};

[shader("vertex")]
VsOut main([[vk::location(0)]] float2 inPosition, // framebuffer pixels, top-left origin
           [[vk::location(1)]] float4 inColor)    // sRGB, straight alpha
{
    VsOut o;
    o.fragColor = inColor;
    o.position = float4(inPosition * push.scale + push.translate, 0.0, 1.0);
    return o;
}

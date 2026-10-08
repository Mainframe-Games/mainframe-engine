// RmlUi geometry (game UI, docs/design/game-ui.md). Vertices are Rml::Vertex: position in context pixels, premultiplied
// sRGB-encoded RGBA8 colour, texture coordinate. The transform is projection (pixels → clip, y down, no flip) times the
// element's RCSS transform, computed on the CPU (row-major: mul(v, transform)).
struct UiPush
{
    float4x4 transform;
    float2   translate;
    uint     textureFlags; // read by the fragment shader
};

[[vk::push_constant]] ConstantBuffer<UiPush> pc;

struct VsOut
{
    float4 position : SV_Position;
    [[vk::location(0)]] float2 outTexCoord;
    [[vk::location(1)]] float4 outColor;
};

[shader("vertex")]
VsOut main([[vk::location(0)]] float2 inPosition, [[vk::location(1)]] float4 inColor, [[vk::location(2)]] float2 inTexCoord)
{
    VsOut o;
    o.outTexCoord = inTexCoord;
    o.outColor = inColor;
    o.position = mul(float4(inPosition + pc.translate, 0.0, 1.0), pc.transform);
    return o;
}

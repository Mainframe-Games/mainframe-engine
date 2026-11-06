// G11.5: resolves a world-space UI panel (UiPanel3D) from the UI layer it was drawn into (premultiplied, sRGB-encoded)
// into the panel's own image, which 3D materials sample through an sRGB view. Opaque panels composite over their
// background colour (sRGB, straight alpha); transparent ones are un-premultiplied (straight alpha, alpha-blended meshes).
[[vk::binding(0, 0)]] Sampler2D source;

struct ResolvePush
{
    [[vk::offset(16)]] float4 background; // sRGB-encoded, opaque
    uint transparent;
};

[[vk::push_constant]] ConstantBuffer<ResolvePush> pc;

[shader("fragment")]
float4 main([[vk::location(0)]] float2 inTexCoord) : SV_Target0
{
    float4 color = source.Sample(inTexCoord);
    if (pc.transparent != 0u)
        return color.a > 0.0 ? float4(saturate(color.rgb / color.a), color.a) : float4(0.0);
    return float4(color.rgb + pc.background.rgb * (1.0 - color.a), 1.0);
}

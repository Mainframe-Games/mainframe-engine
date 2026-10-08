// Fullscreen triangle for UI layer composition and filters. The viewport selects the target rectangle; texture
// coordinates span [0, 1] over the viewport and are mapped by uvOffset + uv · uvScale (sub-rectangle copies, the blur's
// downscale/upscale, the drop-shadow offset).
struct FullscreenPush
{
    float2 uvOffset;
    float2 uvScale;
};

[[vk::push_constant]] ConstantBuffer<FullscreenPush> pc;

struct VsOut
{
    float4 position : SV_Position;
    [[vk::location(0)]] float2 outTexCoord;
};

[shader("vertex")]
VsOut main(int vertexIndex : SV_VulkanVertexID)
{
    float2 uv = float2(float((vertexIndex << 1) & 2), float(vertexIndex & 2)); // (0,0) (2,0) (0,2)
    VsOut o;
    o.position = float4(uv * 2.0 - 1.0, 0.0, 1.0);
    o.outTexCoord = pc.uvOffset + uv * pc.uvScale;
    return o;
}

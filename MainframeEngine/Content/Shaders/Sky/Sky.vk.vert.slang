// Fullscreen triangle — positions are baked in, no vertex buffer required.
struct VsOut
{
    float4 position : SV_Position;
    [[vk::location(0)]] float2 outUV; // NDC xy passed to fragment for ray reconstruction
};

[shader("vertex")]
VsOut main(uint vertexIndex : SV_VulkanVertexID)
{
    const float2 positions[3] = {
        float2(-1.0, -1.0),
        float2( 3.0, -1.0),
        float2(-1.0,  3.0)
    };
    float2 pos = positions[vertexIndex];
    VsOut o;
    o.position = float4(pos, 0.0, 1.0); // drawn first so z value is irrelevant
    o.outUV = pos; // NDC [-1, 1] forwarded to fragment
    return o;
}

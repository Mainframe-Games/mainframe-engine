// Fullscreen triangle for post-processing passes: no vertex buffer, three vertices cover the viewport.
// outNdc is the NDC position; fragment shaders that read a same-sized image use SV_Position instead.
struct VsOut
{
    float4 position : SV_Position;
    [[vk::location(0)]] float2 outNdc;
};

[shader("vertex")]
VsOut main(uint vertexIndex : SV_VulkanVertexID)
{
    const float2 positions[3] = { float2(-1.0, -1.0), float2(3.0, -1.0), float2(-1.0, 3.0) };
    float2 pos = positions[vertexIndex];
    VsOut o;
    o.position = float4(pos, 0.0, 1.0);
    o.outNdc = pos;
    return o;
}

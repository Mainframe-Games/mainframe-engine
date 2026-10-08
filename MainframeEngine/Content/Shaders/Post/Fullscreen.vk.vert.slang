// Fullscreen triangle for post-processing passes: no vertex buffer, three vertices cover the viewport. Its fragment
// shaders read the same-sized image at SV_Position, so it outputs nothing else (an output no fragment shader reads is
// a validation warning: Slang drops unread fragment inputs).
[shader("vertex")]
float4 main(uint vertexIndex : SV_VulkanVertexID) : SV_Position
{
    const float2 positions[3] = { float2(-1.0, -1.0), float2(3.0, -1.0), float2(-1.0, 3.0) };
    return float4(positions[vertexIndex], 0.0, 1.0);
}

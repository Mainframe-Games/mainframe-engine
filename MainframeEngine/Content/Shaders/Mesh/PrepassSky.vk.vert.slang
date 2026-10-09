// The sky's velocity in the depth prepass (ADR 0163): a fullscreen triangle at the far plane (depth 1), depth-tested
// EQUAL against the cleared depth, so it covers only the pixels no geometry was drawn into.
[shader("vertex")]
float4 main(uint vertexIndex : SV_VulkanVertexID) : SV_Position
{
    const float2 positions[3] = { float2(-1.0, -1.0), float2(3.0, -1.0), float2(-1.0, 3.0) };
    return float4(positions[vertexIndex], 1.0, 1.0);
}

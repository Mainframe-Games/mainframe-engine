// Batched Label3D glyph quads (Text3DRenderer, ADR 0258): see include/text3d.slang. Corners 0..5 make two triangles of
// the glyph rectangle (bottom-left, bottom-right, top-right; bottom-left, top-right, top-left), counter-clockwise facing
// +Z like the mesh Label3D builds.
#include "text3d.slang"

struct VsIn
{
    [[vk::location(0)]] float4 rect;  // x0, y0 (bottom), x1, y1 (top), label space (world units)
    [[vk::location(1)]] float4 uv;    // u0, v0 (top), u1, v1 (bottom)
    [[vk::location(2)]] uint2 glyph;  // x = record index | outline bit, y = page slot in the group
};

[shader("vertex")]
TextVsOut main(VsIn input, uint vertexId : SV_VulkanVertexID)
{
    // Corner index: 0 bottom-left, 1 bottom-right, 2 top-right, 3 top-left.
    uint corner = vertexId < 3u ? vertexId : (vertexId == 3u ? 0u : vertexId - 2u);
    bool right = corner == 1u || corner == 2u;
    bool top = corner >= 2u;
    float2 p = float2(right ? input.rect.z : input.rect.x, top ? input.rect.w : input.rect.y);
    float2 uv = float2(right ? input.uv.z : input.uv.x, top ? input.uv.y : input.uv.w);

    uint recordIndex = input.glyph.x & ~kTextOutlineBit;
    TextLabelRecord record = labels[recordIndex];
    float4 world = mul(float4(p, 0.0, 1.0), record.model);

    TextVsOut output;
    output.position = mul(world, frame.viewProjection);
#ifndef TEXT3D_OBJECT_ID
    output.worldPos = world.xyz;
#endif
    output.uv = uv;
    output.color = (input.glyph.x & kTextOutlineBit) != 0u ? record.outlineColor : record.color;
    output.page = input.glyph.y;
#ifdef TEXT3D_OBJECT_ID
    output.objectId = record.info.x;
#endif
    return output;
}

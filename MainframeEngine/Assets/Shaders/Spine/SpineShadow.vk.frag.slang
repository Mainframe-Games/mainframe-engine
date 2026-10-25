// Directional/spot shadow pass of a SpineNode (ADR 0204): discards where the atlas alpha × the slot tint's alpha is
// below SpineNode.ShadowAlphaThreshold, so attachments cast their silhouettes rather than their quads. A threshold of
// 0 never discards (the whole triangles cast). A premultiplied (UNORM) page has the same alpha as a straight one.
[[vk::binding(0, 1)]] Sampler2D uTexture;

[shader("fragment")]
void main([[vk::location(0)]] float2 inUV, [[vk::location(1)]] float2 inAlphaTest)
{
    if (uTexture.Sample(inUV).a * inAlphaTest.x < inAlphaTest.y)
        discard;
}

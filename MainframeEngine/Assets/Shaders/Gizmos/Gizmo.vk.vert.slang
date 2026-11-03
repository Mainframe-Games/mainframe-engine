// Editor gizmos (GizmoDrawList): world-space triangles, and lines expanded here into screen-space quads with a width in
// pixels. Drawn with the shared set 0 camera (frame.viewProjection, frame.viewport).
#include "frame.slang"

struct VsOut
{
    float4 position : SV_Position;
    [[vk::location(0)]] float4 color;
    [[vk::location(1)]] float2 uv;
    // Lines: x = along the segment from its lexicographically first end (pixels), y = across it (pixels), z = half the
    // width, w = the segment's length in pixels. Triangles: (0, 0, large, 0), full coverage.
    [[vk::location(2)]] noperspective float4 segment;
};

bool lexLess(float3 u, float3 v)
{
    return u.x != v.x ? u.x < v.x : (u.y != v.y ? u.y < v.y : u.z < v.z);
}

[shader("vertex")]
VsOut main([[vk::location(0)]] float3 inPosition,
           [[vk::location(1)]] float3 inOther,  // the other end of this vertex's line
           [[vk::location(2)]] float4 inColor,  // sRGB, straight alpha
           [[vk::location(3)]] float2 inUv,
           [[vk::location(4)]] float2 inLine)   // x = side (±1), y = width (pixels); 0 = a triangle vertex
{
    VsOut o;
    o.color = inColor;
    o.uv = inUv;
    float4 p = mul(float4(inPosition, 1.0), frame.viewProjection);
    if (inLine.y <= 0.0)
    {
        o.position = p;
        o.segment = float4(0.0, 0.0, 1e4, 0.0);
        return o;
    }

    // Clip the segment to the near plane (Vulkan depth 0) so an end behind the camera does not flip the direction.
    float4 q = mul(float4(inOther, 1.0), frame.viewProjection);
    if (p.z < 0.0 && q.z < 0.0)
    {
        o.position = float4(0.0, 0.0, -1.0, 1.0); // both ends behind the camera: nothing
        o.segment = float4(0.0);
        return o;
    }

    if (p.z < 0.0)
        p = lerp(p, q, p.z / (p.z - q.z));
    else if (q.z < 0.0)
        q = lerp(q, p, q.z / (q.z - p.z));

    // Both ends work in the frame of the segment from its lexicographically first end to the other, so they agree on
    // which side is which and on the along coordinate.
    bool first = lexLess(inPosition, inOther);
    float2 halfSize = frame.viewport.xy * 0.5;
    float2 ps = p.xy / p.w * halfSize; // pixels from the centre
    float2 qs = q.xy / q.w * halfSize;
    float2 delta = first ? qs - ps : ps - qs;
    float len = length(delta);
    float2 dir = len > 1e-4 ? delta / len : float2(1.0, 0.0);
    float2 normal = float2(-dir.y, dir.x);
    float halfWidth = inLine.y * 0.5;
    float ext = halfWidth + 1.0; // one more pixel for the anti-aliased edge
    float2 offset = normal * (inLine.x * ext) + dir * (first ? -ext : ext);

    o.position = float4((ps + offset) / halfSize * p.w, p.z, p.w);
    o.segment = float4(first ? -ext : len + ext, inLine.x * ext, halfWidth, len);
    return o;
}

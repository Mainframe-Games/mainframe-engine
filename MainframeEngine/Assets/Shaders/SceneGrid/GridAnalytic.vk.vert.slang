// The editor grid (Grid3D.Style = Analytic): one quad on the plane y = planeY, centred under the camera and as wide as
// the far plane allows; the fragment shader draws the lines.
#include "frame.slang"

struct Push
{
    float4 params; // x = pixel scale (line widths are in points), y = plane height, z = base cell size, w = unused
};

[[vk::push_constant]] ConstantBuffer<Push> push;

struct VsOut
{
    float4 position : SV_Position;
    [[vk::location(0)]] float3 world;
};

[shader("vertex")]
VsOut main(uint vertexIndex : SV_VulkanVertexID)
{
    // Two triangles: corners (-1,-1) (1,-1) (1,1) (-1,-1) (1,1) (-1,1).
    static const float2 kCorners[6] = { float2(-1, -1), float2(1, -1), float2(1, 1), float2(-1, -1), float2(1, 1), float2(-1, 1) };
    float extent = frame.clip.y; // the far plane: nothing beyond it is drawn anyway
    float2 xz = frame.cameraPosition.xz + kCorners[vertexIndex] * extent;
    VsOut o;
    o.world = float3(xz.x, push.params.y, xz.y);
    o.position = mul(float4(o.world, 1.0), frame.viewProjection);
    return o;
}

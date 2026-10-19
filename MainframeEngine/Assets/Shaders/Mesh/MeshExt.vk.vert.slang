// Mesh.vk.vert for surfaces with the second vertex stream (VertexLayoutId.MeshInstancedExt, ADR 0151): the same
// outputs, plus the vertex colour (binding 2, RGBA8, authored in sRGB) as linear RGBA for Mesh.vk.frag, which multiplies
// the albedo by it (specialization constant kVertexColor). Custom0 is not read: only foliage uses it.
#include "frame.slang"
#include "common.slang"

struct VsIn
{
    [[vk::location(0)]] float3 inPosition;
    [[vk::location(1)]] float3 inNormal;
    [[vk::location(2)]] float2 inUV;
    // System.Numerics row-vector matrix, uploaded as-is: its rows are the rows of the Slang matrix.
    [[vk::location(3)]] float4 inModel0;
    [[vk::location(4)]] float4 inModel1;
    [[vk::location(5)]] float4 inModel2;
    [[vk::location(6)]] float4 inModel3;
    [[vk::location(8)]] float4 inColor;
};

struct VsOut
{
    float4 position : SV_Position;
    [[vk::location(0)]] float3 outWorldPos;
    [[vk::location(1)]] float3 outNormal;
    [[vk::location(2)]] float2 outUV;
    [[vk::location(3)]] float4 outColor;
};

[shader("vertex")]
VsOut main(VsIn input)
{
    float4x4 model = float4x4(input.inModel0, input.inModel1, input.inModel2, input.inModel3);
    float4 worldPos = mul(float4(input.inPosition, 1.0), model);

    // As Mesh.vk.vert: the cofactor normal matrix, signed by the determinant.
    float3x3 m = (float3x3)model;
    float3x3 cofactor = float3x3(cross(m[1], m[2]), cross(m[2], m[0]), cross(m[0], m[1]));
    float det = dot(m[0], cofactor[0]);

    VsOut o;
    o.outWorldPos = worldPos.xyz;
    o.outNormal = mul(input.inNormal, cofactor) * (det < 0.0 ? -1.0 : 1.0);
    o.outUV = input.inUV;
    o.outColor = float4(srgbToLinear(input.inColor.rgb), input.inColor.a);
    o.position = mul(worldPos, frame.viewProjection);
    return o;
}

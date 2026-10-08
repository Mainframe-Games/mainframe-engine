// Batched mesh instances (MeshRenderer): set 0 frame (camera), MeshVertex at binding 0, MeshInstanceData at binding 1
// (instance rate: model matrix + object id). One instanced draw per (pipeline, material, mesh surface) run.
#include "frame.slang"

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
    [[vk::location(7)]] uint inObjectId;
};

struct VsOut
{
    float4 position : SV_Position;
    [[vk::location(0)]] float3 outWorldPos;
    [[vk::location(1)]] float3 outNormal;
    [[vk::location(2)]] float2 outUV;
};

[shader("vertex")]
VsOut main(VsIn input)
{
    float4x4 model = float4x4(input.inModel0, input.inModel1, input.inModel2, input.inModel3);
    float4 worldPos = mul(float4(input.inPosition, 1.0), model);

    // Normal matrix = cofactor of the upper 3x3 (∝ inverse transpose; handles non-uniform scale), with the
    // determinant's sign so mirrored instances keep outward normals.
    float3x3 m = (float3x3)model;
    float3x3 cofactor = float3x3(cross(m[1], m[2]), cross(m[2], m[0]), cross(m[0], m[1]));
    float det = dot(m[0], cofactor[0]);

    VsOut o;
    o.outWorldPos = worldPos.xyz;
    o.outNormal = mul(input.inNormal, cofactor) * (det < 0.0 ? -1.0 : 1.0);
    o.outUV = input.inUV;
    o.position = mul(worldPos, frame.viewProjection);
    return o;
}

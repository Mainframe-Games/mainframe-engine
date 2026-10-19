// WaterMaterial3D (ADR 0159): Mesh.vk.vert plus custom0 from the second vertex stream (binding 2, location 9):
// x = water column depth (m), yz = surface flow along the mesh's local X and Z (m/s), w = foam. The flow leaves here in
// world XZ (the model's rotation; water meshes are not scaled).
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
    [[vk::location(9)]] float4 inCustom0;
};

struct VsOut
{
    float4 position : SV_Position;
    [[vk::location(0)]] float3 outWorldPos;
    [[vk::location(1)]] float3 outNormal;
    [[vk::location(2)]] float2 outUV;
    [[vk::location(3)]] float4 outWater; // x = column depth, yz = world XZ flow, w = foam
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
    float3 flow = mul(float3(input.inCustom0.y, 0.0, input.inCustom0.z), m);

    VsOut o;
    o.outWorldPos = worldPos.xyz;
    o.outNormal = mul(input.inNormal, cofactor) * (det < 0.0 ? -1.0 : 1.0);
    o.outUV = input.inUV;
    o.outWater = float4(max(input.inCustom0.x, 0.0), flow.x, flow.z, saturate(input.inCustom0.w));
    o.position = mul(worldPos, frame.viewProjection);
    return o;
}

// FoliageMaterial3D (ADR 0151): Mesh.vk.vert plus the second vertex stream (binding 2: colour, custom0) and the wind
// (include/wind.slang) from the frame's wind and time. Set 2 is the StandardMaterial3D layout; foliage packs its wind
// scales into material.emission (y = strength, z = branch bend; see include/foliage.slang).
#include "frame.slang"
#include "material.slang"
#include "foliage.slang"
#include "wind.slang"

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
    [[vk::location(9)]] float4 inCustom0;
};

struct VsOut
{
    float4 position : SV_Position;
    [[vk::location(0)]] float3 outWorldPos;
    [[vk::location(1)]] float3 outNormal;
    [[vk::location(2)]] float2 outUV;
    [[vk::location(3)]] float4 outColor; // linear RGBA
    [[vk::location(4)]] float outAo;
};

[shader("vertex")]
VsOut main(VsIn input)
{
    float4x4 model = float4x4(input.inModel0, input.inModel1, input.inModel2, input.inModel3);
    float4 worldPos = mul(float4(input.inPosition, 1.0), model);
    worldPos.xyz += windOffset(worldPos.xyz, input.inCustom0, input.inUV, frame.wind, frame.windParams, frame.clip.z,
                               foliageWindStrength(), foliageBranchBend());

    float3x3 m = (float3x3)model;
    float3x3 cofactor = float3x3(cross(m[1], m[2]), cross(m[2], m[0]), cross(m[0], m[1]));
    float det = dot(m[0], cofactor[0]);

    VsOut o;
    o.outWorldPos = worldPos.xyz;
    o.outNormal = mul(input.inNormal, cofactor) * (det < 0.0 ? -1.0 : 1.0);
    o.outUV = input.inUV;
    o.outColor = float4(srgbToLinear(input.inColor.rgb), input.inColor.a);
    o.outAo = input.inCustom0.w > 0.0 ? saturate(input.inCustom0.w) : 1.0; // 0: no AO data (missing stream)
    o.position = mul(worldPos, frame.viewProjection);
    return o;
}

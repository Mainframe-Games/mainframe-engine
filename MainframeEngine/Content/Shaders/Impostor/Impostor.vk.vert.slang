// ImpostorMaterial3D (ADR 0172): the quad of a TreeImpostor turned to the camera about the baked bounding sphere's
// centre, with the three nearest baked views (include/impostor.slang). Instances outside the material's per-instance
// visibility range collapse. Pairs with Impostor.vk.frag.
#include "frame.slang"
#include "material.slang"
#include "foliage.slang"
#include "impostor.slang"

struct VsIn
{
    [[vk::location(2)]] float2 inUV; // the quad corner (0..1, v down)
    // System.Numerics row-vector matrix, uploaded as-is: its rows are the rows of the Slang matrix.
    [[vk::location(3)]] float4 inModel0;
    [[vk::location(4)]] float4 inModel1;
    [[vk::location(5)]] float4 inModel2;
    [[vk::location(6)]] float4 inModel3;
};

struct VsOut
{
    float4 position : SV_Position;
    [[vk::location(0)]] float3 outWorldPos;
    [[vk::location(1)]] float2 outLocal0;
    [[vk::location(2)]] float2 outLocal1;
    [[vk::location(3)]] float2 outLocal2;
    [[vk::location(4)]] nointerpolation float4 outCells01;
    [[vk::location(5)]] nointerpolation float2 outCell2;
    [[vk::location(6)]] nointerpolation float3 outWeights;
    [[vk::location(7)]] nointerpolation float3 outRow0; // the instance's rotation (normals to world space)
    [[vk::location(8)]] nointerpolation float3 outRow1;
    [[vk::location(9)]] nointerpolation float3 outRow2;
    [[vk::location(10)]] nointerpolation float4 outView; // xyz towards the camera, w = the radius in world units
    [[vk::location(11)]] nointerpolation float outDistance;
};

[shader("vertex")]
VsOut main(VsIn input)
{
    float4x4 model = float4x4(input.inModel0, input.inModel1, input.inModel2, input.inModel3);
    ImpostorVertex v = impostorVertex(input.inUV, model, frame.cameraPosition.xyz);

    VsOut o;
    o.outWorldPos = v.worldPos.xyz;
    o.outLocal0 = v.local0;
    o.outLocal1 = v.local1;
    o.outLocal2 = v.local2;
    o.outCells01 = v.cells01;
    o.outCell2 = v.cell2;
    o.outWeights = v.weights;
    o.outRow0 = normalize(input.inModel0.xyz);
    o.outRow1 = normalize(input.inModel1.xyz);
    o.outRow2 = normalize(input.inModel2.xyz);
    o.outView = float4(v.viewWorld, v.radiusWorld);
    o.outDistance = v.distance;
    o.position = distanceFadeVisible(material.foliage1, v.distance) ? mul(v.worldPos, frame.viewProjection) : kCollapsedPosition;
    return o;
}

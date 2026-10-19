// Depth prepass (ADR 0163) of ImpostorMaterial3D (ADR 0172): Impostor.vk.vert's clip position (the same expressions: the
// scene pass tests this depth EQUAL), the three views for the alpha test, and the velocity's two clip positions (this
// frame's unjittered one, and last frame's through the previous model and view-projection). Pairs with ImpostorDepth.vk.frag.
#include "frame.slang"
#include "material.slang"
#include "foliage.slang"
#include "impostor.slang"

struct VsIn
{
    [[vk::location(2)]] float2 inUV;
    [[vk::location(3)]] float4 inModel0;
    [[vk::location(4)]] float4 inModel1;
    [[vk::location(5)]] float4 inModel2;
    [[vk::location(6)]] float4 inModel3;
    [[vk::location(10)]] float4 inPrevModel0;
    [[vk::location(11)]] float4 inPrevModel1;
    [[vk::location(12)]] float4 inPrevModel2;
    [[vk::location(13)]] float4 inPrevModel3;
};

struct VsOut
{
    float4 position : SV_Position;
    [[vk::location(0)]] float4 outCurrent;
    [[vk::location(1)]] float4 outPrevious;
    [[vk::location(2)]] float2 outLocal0;
    [[vk::location(3)]] float2 outLocal1;
    [[vk::location(4)]] float2 outLocal2;
    [[vk::location(5)]] nointerpolation float4 outCells01;
    [[vk::location(6)]] nointerpolation float2 outCell2;
    [[vk::location(7)]] nointerpolation float3 outWeights;
    [[vk::location(8)]] nointerpolation float outDistance;
};

[shader("vertex")]
VsOut main(VsIn input)
{
    float4x4 model = float4x4(input.inModel0, input.inModel1, input.inModel2, input.inModel3);
    float4x4 prevModel = float4x4(input.inPrevModel0, input.inPrevModel1, input.inPrevModel2, input.inPrevModel3);
    ImpostorVertex v = impostorVertex(input.inUV, model, frame.cameraPosition.xyz);
    float4 prevWorldPos = mul(float4(impostorCenter() + v.offset, 1.0), prevModel);

    VsOut o;
    o.position = distanceFadeVisible(material.foliage1, v.distance) ? mul(v.worldPos, frame.viewProjection) : kCollapsedPosition;
    o.outCurrent = unjitterClip(o.position);
    o.outPrevious = mul(prevWorldPos, frame.prevViewProjection);
    o.outLocal0 = v.local0;
    o.outLocal1 = v.local1;
    o.outLocal2 = v.local2;
    o.outCells01 = v.cells01;
    o.outCell2 = v.cell2;
    o.outWeights = v.weights;
    o.outDistance = v.distance;
    return o;
}

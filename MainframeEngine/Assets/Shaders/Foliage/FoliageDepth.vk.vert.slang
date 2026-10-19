// Depth prepass (ADR 0163) of FoliageMaterial3D: Foliage.vk.vert's clip position (the same expressions: the scene pass
// tests this depth EQUAL), and the velocity's two clip positions: this frame's unjittered one, and last frame's from the
// previous model, the previous wind and time (FrameData.prevWind, prevWindParams, temporal.x), so swaying leaves get
// their real motion. Pairs with Foliage/FoliageDepth.vk.frag (cutout, vertex colour alpha, the per-instance visibility
// range's bands: instances outside it collapse here, ADR 0172).
#include "frame.slang"
#include "material.slang"
#include "foliage.slang"
#include "wind.slang"

struct VsIn
{
    [[vk::location(0)]] float3 inPosition;
    [[vk::location(2)]] float2 inUV;
    // System.Numerics row-vector matrix, uploaded as-is: its rows are the rows of the Slang matrix.
    [[vk::location(3)]] float4 inModel0;
    [[vk::location(4)]] float4 inModel1;
    [[vk::location(5)]] float4 inModel2;
    [[vk::location(6)]] float4 inModel3;
    [[vk::location(8)]] float4 inColor;
    [[vk::location(9)]] float4 inCustom0;
    [[vk::location(14)]] float4 inCustom1;
    [[vk::location(15)]] float4 inCustom2;
    [[vk::location(10)]] float4 inPrevModel0;
    [[vk::location(11)]] float4 inPrevModel1;
    [[vk::location(12)]] float4 inPrevModel2;
    [[vk::location(13)]] float4 inPrevModel3;
};

struct VsOut
{
    float4 position : SV_Position;
    [[vk::location(0)]] float2 outUV;
    [[vk::location(1)]] float4 outCurrent;
    [[vk::location(2)]] float4 outPrevious;
    [[vk::location(3)]] float outAlpha;
    [[vk::location(4)]] float outDistance;
};

[shader("vertex")]
VsOut main(VsIn input)
{
    // Instances outside the level's range collapse before the wind (ADR 0172).
    float distance = length(frame.cameraPosition.xyz - input.inModel3.xyz);
    VsOut o = (VsOut)0;
    if (!distanceFadeVisible(material.foliage1, distance))
    {
        o.position = kCollapsedPosition;
        return o;
    }

    float4x4 model = float4x4(input.inModel0, input.inModel1, input.inModel2, input.inModel3);
    float4 worldPos = mul(float4(input.inPosition, 1.0), model);
    worldPos.xyz = windPosition(worldPos.xyz, model, input.inCustom0, input.inCustom1, input.inCustom2, input.inUV, frame.wind,
                                frame.windParams, frame.clip.z, foliageWindStrength(), foliageBranchBend(), foliageTrunkSway());

    float4x4 prevModel = float4x4(input.inPrevModel0, input.inPrevModel1, input.inPrevModel2, input.inPrevModel3);
    float4 prevWorldPos = mul(float4(input.inPosition, 1.0), prevModel);
    prevWorldPos.xyz = windPosition(prevWorldPos.xyz, prevModel, input.inCustom0, input.inCustom1, input.inCustom2, input.inUV,
                                    frame.prevWind, frame.prevWindParams, frame.temporal.x, foliageWindStrength(), foliageBranchBend(),
                                    foliageTrunkSway());

    o.outUV = input.inUV;
    o.outAlpha = input.inColor.a;
    o.outDistance = distance;
    o.position = mul(worldPos, frame.viewProjection);
    o.outCurrent = unjitterClip(o.position);
    o.outPrevious = mul(prevWorldPos, frame.prevViewProjection);
    return o;
}

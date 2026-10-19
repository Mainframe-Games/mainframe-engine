// Depth prepass (ADR 0163) of batched mesh instances: the clip position exactly as Mesh.vk.vert computes it (the scene
// pass tests the prepass depth LESS_OR_EQUAL, cutouts EQUAL, so the two must agree bit for bit: keep the expressions
// identical), plus this frame's unjittered and last frame's clip positions for the velocity (MeshDepth.vk.frag).
// Binding 1: MeshInstanceData (this frame's model, locations 3-6); binding 3: the same layout with last frame's model
// (locations 10-13; a multimesh binds its own instances there: static instances move with the camera only).
#include "frame.slang"

struct VsIn
{
    [[vk::location(0)]] float3 inPosition;
    [[vk::location(2)]] float2 inUV;
    // System.Numerics row-vector matrix, uploaded as-is: its rows are the rows of the Slang matrix.
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
    [[vk::location(0)]] float2 outUV;
    [[vk::location(1)]] float4 outCurrent;  // this frame's clip position without the jitter
    [[vk::location(2)]] float4 outPrevious; // last frame's clip position (unjittered)
    [[vk::location(3)]] float outAlpha;     // the vertex colour's alpha (1: no colour stream)
};

[shader("vertex")]
VsOut main(VsIn input)
{
    float4x4 model = float4x4(input.inModel0, input.inModel1, input.inModel2, input.inModel3);
    float4 worldPos = mul(float4(input.inPosition, 1.0), model);
    float4x4 prevModel = float4x4(input.inPrevModel0, input.inPrevModel1, input.inPrevModel2, input.inPrevModel3);

    VsOut o;
    o.outUV = input.inUV;
    o.outAlpha = 1.0;
    o.position = mul(worldPos, frame.viewProjection);
    o.outCurrent = unjitterClip(o.position);
    o.outPrevious = mul(mul(float4(input.inPosition, 1.0), prevModel), frame.prevViewProjection);
    return o;
}

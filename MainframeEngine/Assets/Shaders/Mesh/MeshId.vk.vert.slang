// Object-ID pass (editor picking): Mesh.vk.vert's position and UV with the instance's object id, and nothing the
// ID fragment shader does not read (an output no fragment shader reads is a validation warning: Slang drops unread
// fragment inputs). Same pipeline layout as Mesh.vk.vert; its vertex attributes are VertexLayouts.MeshIdAttributes (no
// normal).
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
    [[vk::location(7)]] uint inObjectId;
};

struct VsOut
{
    float4 position : SV_Position;
    [[vk::location(2)]] float2 outUV;
    [[vk::location(3)]] nointerpolation uint outObjectId;
};

[shader("vertex")]
VsOut main(VsIn input)
{
    float4x4 model = float4x4(input.inModel0, input.inModel1, input.inModel2, input.inModel3);
    VsOut o;
    o.outUV = input.inUV;
    o.outObjectId = input.inObjectId;
    o.position = mul(mul(float4(input.inPosition, 1.0), model), frame.viewProjection);
    return o;
}

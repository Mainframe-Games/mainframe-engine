// SprayCards3D (ADR 0173): camera-facing cards. Each card's four vertices share its centre (position, mesh space); the
// normal stream holds the corner (x, y = ±1) and the card's size in metres (z), Custom0 its phase (x, 0..1), rise scale
// (y) and drift (zw, metres per cycle along world X and Z). Over its cycle (SprayMaterial3D.Cycle) a card rises, grows and
// fades in and out. Writes what Spray.vk.frag reads.
#include "frame.slang"
#include "material.slang"

struct VsIn
{
    [[vk::location(0)]] float3 inPosition;
    [[vk::location(1)]] float3 inCorner;
    [[vk::location(2)]] float2 inUV;
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
    [[vk::location(1)]] float2 outUV;
    [[vk::location(2)]] float4 outCard; // x = fade (0..1), y = phase, z = cycle time (0..1), w = view depth
};

static const float kPi = 3.14159265;

[shader("vertex")]
VsOut main(VsIn input)
{
    float4x4 model = float4x4(input.inModel0, input.inModel1, input.inModel2, input.inModel3);
    float3 centre = mul(float4(input.inPosition, 1.0), model).xyz;

    float cycle = max(material.uvTransform.x, 0.05);
    float t = frac(frame.clip.z / cycle + input.inCustom0.x);
    centre += float3(input.inCustom0.z, material.uvTransform.y * input.inCustom0.y, input.inCustom0.w) * t;
    float size = input.inCorner.z * lerp(1.0, material.uvTransform.z, t);

    // The camera's right and up in world space: the view matrix's rotation columns (row-vector convention).
    float3 right = float3(frame.view[0][0], frame.view[1][0], frame.view[2][0]);
    float3 up = float3(frame.view[0][1], frame.view[1][1], frame.view[2][1]);
    float3 world = centre + (right * input.inCorner.x + up * input.inCorner.y) * (size * 0.5);

    VsOut o;
    o.outWorldPos = world;
    o.outUV = input.inUV;
    float viewDepth = -mul(float4(world, 1.0), frame.view).z;
    o.outCard = float4(sin(kPi * t), input.inCustom0.x, t, viewDepth);
    o.position = mul(float4(world, 1.0), frame.viewProjection);
    return o;
}

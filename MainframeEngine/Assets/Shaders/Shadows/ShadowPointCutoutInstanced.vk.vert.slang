// Alpha-tested point-light shadow casters (cutout materials) in instanced batches: ShadowPointInstanced plus the UV.
struct LightVP
{
    float4x4 lightViewProj;
};

[[vk::binding(0, 0)]] ConstantBuffer<LightVP> lvp;

struct VsIn
{
    [[vk::location(0)]] float3 inPosition;
    [[vk::location(1)]] float4 inModel0;
    [[vk::location(2)]] float4 inModel1;
    [[vk::location(3)]] float4 inModel2;
    [[vk::location(4)]] float4 inModel3;
    [[vk::location(5)]] float2 inUV;
};

struct VsOut
{
    float4 position : SV_Position;
    [[vk::location(0)]] float3 outFragWorldPos;
    [[vk::location(1)]] float2 outUV;
};

[shader("vertex")]
VsOut main(VsIn input)
{
    float4x4 model = float4x4(input.inModel0, input.inModel1, input.inModel2, input.inModel3);
    float4 worldPos = mul(float4(input.inPosition, 1.0), model);
    VsOut o;
    o.outFragWorldPos = worldPos.xyz;
    o.outUV = input.inUV;
    o.position = mul(worldPos, lvp.lightViewProj);
    return o;
}

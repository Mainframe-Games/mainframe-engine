struct PC
{
    float4x4 model;
    float4   lightPosRange; // xyz = light world position, w = range
};

[[vk::push_constant]] ConstantBuffer<PC> pc;

[shader("fragment")]
float main([[vk::location(0)]] float3 inFragWorldPos) : SV_Depth
{
    // Store linear distance [0,1] so it can be compared directly in the main pass.
    return length(inFragWorldPos - pc.lightPosRange.xyz) / pc.lightPosRange.w;
}

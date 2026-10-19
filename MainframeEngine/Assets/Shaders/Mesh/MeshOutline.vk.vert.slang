// OutlineMaterial3D (ADR 0132): Godot's inverted-hull outline shader. Same inputs and outputs as Mesh.vk.vert; the
// vertex is pushed along its clip-space normal by the material's width in pixels, and the pipeline culls front faces,
// so the surface drawn before hides all but a rim of constant screen width. Shaded by Mesh.vk.frag (unshaded path).
#include "frame.slang"
#include "material.slang"

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
    [[vk::location(7)]] uint inObjectId;
};

struct VsOut
{
    float4 position : SV_Position;
    [[vk::location(0)]] float3 outWorldPos;
    [[vk::location(1)]] float3 outNormal;
    [[vk::location(2)]] float2 outUV;
    [[vk::location(3)]] float4 outColor; // unused: the outline is unshaded
};

[shader("vertex")]
VsOut main(VsIn input)
{
    float4x4 model = float4x4(input.inModel0, input.inModel1, input.inModel2, input.inModel3);
    float4 worldPos = mul(float4(input.inPosition, 1.0), model);
    float4 clipPosition = mul(worldPos, frame.viewProjection);

    // As Godot's shader: clip_normal = mat3(PROJECTION) * (mat3(MODELVIEW) * NORMAL), then
    // offset = normalize(clip_normal.xy) / VIEWPORT_SIZE * w * width * 2 (2/size clip units per pixel).
    float3 clipNormal = mul(mul(mul(input.inNormal, (float3x3)model), (float3x3)frame.view), (float3x3)frame.projection);
    float width = asfloat(material.flags.w);
    if (dot(clipNormal.xy, clipNormal.xy) > 0.0)
        clipPosition.xy += normalize(clipNormal.xy) * frame.viewport.zw * clipPosition.w * width * 2.0;

    VsOut o;
    o.outWorldPos = worldPos.xyz;
    o.outNormal = mul(input.inNormal, (float3x3)model); // unused: the outline is unshaded
    o.outUV = input.inUV;
    o.outColor = float4(1.0);
    o.position = clipPosition;
    return o;
}

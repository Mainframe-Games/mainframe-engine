// Level 0 of the water scene copy (ADR 0173, WaterSceneTextures in C#): the scene colour after the opaque half of the
// scene pass, and its depth as a linear view depth in metres (through the view's inverse projection, set 0, so
// perspective, orthographic and jittered projections all linearise exactly). The sky (depth 1) writes kSkyDepth.
// Paired with Post/Fullscreen.vk.vert; one texel per pixel.
#include "frame.slang"

[[vk::binding(0, 1)]] Sampler2D sceneColor;
[[vk::binding(1, 1)]] Sampler2D sceneDepth;

static const float kSkyDepth = 60000.0; // WaterSceneTextures.SkyDepth

[shader("fragment")]
float4 main(float4 fragCoord : SV_Position) : SV_Target0
{
    int2 p = int2(fragCoord.xy);
    float3 color = sceneColor.Load(int3(p, 0)).rgb;
    float d = sceneDepth.Load(int3(p, 0)).r;
    float z = kSkyDepth;
    if (d < 1.0)
    {
        float2 uv = fragCoord.xy * frame.viewport.zw;
        float4 q = mul(float4(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, d, 1.0), frame.invProjection);
        z = clamp(-q.z / q.w, 0.0, kSkyDepth);
    }
    return float4(color, z);
}

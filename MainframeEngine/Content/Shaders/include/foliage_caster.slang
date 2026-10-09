// The push constants of the foliage and impostor shadow casters (MeshRenderer.FoliageCasterWind, offset 0, 64 bytes):
// the casters' set 0 is the light matrix, so the frame's wind, time and camera come this way. Include material.slang and
// foliage.slang first.
#pragma once

struct CasterWind
{
    float4 wind;       // FrameData.wind
    float4 windParams; // FrameData.windParams
    float4 time;       // x = seconds, yzw = the view's camera position (per-instance visibility ranges)
    float4 pass;       // x = 1 in a coarse pass (ADR 0167: no range test), yzw = direction towards the light (directional)
};

[[vk::push_constant]] ConstantBuffer<CasterWind> pc;

// True when an instance at `origin` casts with this material (ADR 0172): inside its per-instance visibility range, with a
// hard switch at the range's ends (shadows do not dither), or always in a coarse pass (the coarse level casts for every
// instance there, and only it does).
bool casterInstanceVisible(float3 origin)
{
    float4 range = material.foliage1;
    if (range.w <= 0.0 || pc.pass.x > 0.5)
        return true;
    float distance = length(pc.time.yzw - origin);
    return (range.x <= 0.0 || distance >= range.x) && (range.y <= 0.0 || distance < range.y);
}

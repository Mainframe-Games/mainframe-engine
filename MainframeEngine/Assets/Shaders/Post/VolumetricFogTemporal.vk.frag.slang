// Volumetric fog (ADR 0171), pass 2 of 3: temporal reprojection at half resolution. The texel's surface point (or the
// point at the fog's length, for the sky) is projected with last frame's unjittered view-projection (frame set 0); the
// history there (bilinear) is clipped towards the 3 × 3 neighbourhood's variance box (mean ± γσ, inside its min/max, every
// channel including the transmittance) and blended with this frame's march by the reprojection amount. Off-screen or
// without a history (pc.params.x = 0) the march passes through.
#include "volumetric_fog.slang"

[[vk::binding(0, 1)]] Sampler2D current;            // this frame's march (point)
[[vk::binding(1, 1)]] Sampler2D history;            // last frame's result (bilinear)
[[vk::binding(2, 1)]] Sampler2D<float> sceneDepth;  // point

struct Temporal
{
    float4 params; // x = history weight (0: none), y = clip gamma, zw = this target's size
    float4 scene;  // xy = the scene's size, z = the fog's length (m)
};

[[vk::push_constant]] ConstantBuffer<Temporal> pc;

[shader("fragment")]
float4 main(float4 fragCoord : SV_Position) : SV_Target0
{
    int2 texel = int2(fragCoord.xy);
    int2 last = int2(pc.params.zw) - int2(1);
    float4 c = current.Load(int3(texel, 0));
    if (pc.params.x <= 0.0)
        return c;

    float4 m1 = float4(0.0);
    float4 m2 = float4(0.0);
    float4 lo = c;
    float4 hi = c;
    for (int y = -1; y <= 1; y++)
    {
        for (int x = -1; x <= 1; x++)
        {
            float4 s = current.Load(int3(clamp(texel + int2(x, y), int2(0), last), 0));
            m1 += s;
            m2 += s * s;
            lo = min(lo, s);
            hi = max(hi, s);
        }
    }

    float2 sceneSize = pc.scene.xy;
    int2 pixel = volumetricPixel(texel, sceneSize);
    float depth = sceneDepth.Load(int3(pixel, 0));
    float3 origin, dir;
    volumetricRay(pixel, sceneSize, origin, dir);
    float3 p = depth < 1.0 ? volumetricWorldPosition(pixel, depth, sceneSize) : origin + dir * pc.scene.z;
    float4 clip = mul(float4(p, 1.0), frame.prevViewProjection);
    if (!(clip.w > 1e-5))
        return c;
    float2 uv = ndcToScreenUv(clip.xy / clip.w);
    if (any(uv < float2(0.0)) || any(uv > float2(1.0)))
        return c;

    float4 mean = m1 / 9.0;
    float4 sigma = sqrt(max(m2 / 9.0 - mean * mean, float4(0.0)));
    float4 boxLo = max(mean - pc.params.y * sigma, lo);
    float4 boxHi = min(mean + pc.params.y * sigma, hi);
    float4 h = clamp(history.SampleLevel(uv, 0.0), boxLo, boxHi);
    return lerp(c, h, pc.params.x);
}

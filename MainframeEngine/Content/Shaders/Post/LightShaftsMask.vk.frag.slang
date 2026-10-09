// Light shafts (ADR 0160), pass 1: the occlusion/brightness mask at half resolution. Each texel is the mean of its 2 × 2
// scene texels that are sky (depth still at the cleared far plane, 1), each capped in luminance so the sun disc does not
// swamp the sky around it, weighted by a falloff around the sun's screen position (aspect-corrected screen distance).
// Leaves, trunks and anything else that wrote depth cut the mask, so the blur that follows streaks only real gaps.
// ADR 0174: the colour is at output resolution and the depth at render resolution, which is smaller while the view
// upscales: a texel's depth is the render texel under its centre (the same texel at native resolution).
[[vk::binding(0, 0)]] Sampler2D hdrScene;
[[vk::binding(1, 0)]] Sampler2D<float> sceneDepth;

struct Shafts
{
    float2 sunUv;        // the sun in scene UV (0,0 top-left); may lie off-screen
    float  aspect;       // target width / height
    float  radius;       // falloff radius in screen heights
    float  luminanceCap;
    float  step;         // blur only
    float  tapDecay;     // blur only
    uint   taps;         // blur only
};

[[vk::push_constant]] ConstantBuffer<Shafts> pc;

static const float3 kLuminance = float3(0.2126, 0.7152, 0.0722);

[shader("fragment")]
float4 main(float4 fragCoord : SV_Position) : SV_Target0
{
    uint w, h, levels;
    hdrScene.GetDimensions(0u, w, h, levels);
    int2 last = int2(int(w) - 1, int(h) - 1);
    uint dw, dh, dlevels;
    sceneDepth.GetDimensions(0u, dw, dh, dlevels);
    float2 toDepth = float2(float(dw), float(dh)) / float2(float(w), float(h));
    int2 lastDepth = int2(int(dw) - 1, int(dh) - 1);
    int2 base = int2(fragCoord.xy) * 2;

    float3 sky = float3(0.0);
    for (int y = 0; y < 2; y++)
    {
        for (int x = 0; x < 2; x++)
        {
            int2 p = min(base + int2(x, y), last);
            int2 dp = min(int2((float2(p) + 0.5) * toDepth), lastDepth);
            if (sceneDepth.Load(int3(dp, 0)) < 1.0)
                continue;
            float3 c = max(hdrScene.Load(int3(p, 0)).rgb, float3(0.0));
            float lum = dot(c, kLuminance);
            sky += c * min(1.0, pc.luminanceCap / max(lum, 1e-6));
        }
    }

    float2 size = float2(float(max(w / 2u, 1u)), float(max(h / 2u, 1u)));
    float2 toSun = (fragCoord.xy / size - pc.sunUv) * float2(pc.aspect, 1.0);
    float falloff = 1.0 - smoothstep(0.0, pc.radius, length(toSun));
    return float4(sky * 0.25 * falloff * falloff, 1.0);
}

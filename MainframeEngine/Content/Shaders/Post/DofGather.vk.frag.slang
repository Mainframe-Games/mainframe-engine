// Depth of field (ADR 0168), pass 2 at half resolution: a scatter-as-gather bokeh over a golden-angle (Vogel) disc of
// `taps` samples out to the full blur radius, after Dennis Gustafsson's single-pass bokeh: a sample counts where its own
// circle of confusion reaches this texel; a sample behind this texel (larger signed CoC) is limited to twice this
// texel's circle, so a blurred background never smears over a sharp foreground, while a blurred foreground spreads over
// what is behind it. Samples that do not reach keep the running mean (constant weight per tap: an even disc, round
// bokeh). Alpha: how far a near (foreground) sample's blur spread over this texel, in full-resolution pixels, which the
// composite uses to blend foreground blur over sharp pixels.
#include "dof.slang"

[[vk::binding(0, 0)]] Sampler2D prefiltered; // DofPrefilter's output: colour + signed CoC; bilinear, clamp to edge

static const float kGoldenAngle = 2.39996323;

[shader("fragment")]
float4 main(float4 fragCoord : SV_Position) : SV_Target0
{
    uint w, h, levels;
    prefiltered.GetDimensions(0u, w, h, levels);
    float2 size = float2(float(w), float(h));
    float4 center = prefiltered.Load(int3(int2(fragCoord.xy), 0));
    float centerSize = abs(center.a) * 0.5; // half-resolution pixels
    float radius = pc.maxRadius * 0.5;
    float3 sum = center.rgb;
    float count = 1.0;
    float nearSpread = 0.0;
    uint taps = max(pc.taps, 1u);
    for (uint i = 0u; i < taps; i++)
    {
        float r = sqrt((float(i) + 0.5) / float(taps)) * radius;
        float angle = float(i) * kGoldenAngle;
        float2 offset = float2(cos(angle), sin(angle)) * r;
        float4 s = prefiltered.SampleLevel((fragCoord.xy + offset) / size, 0.0);
        float sampleSize = abs(s.a) * 0.5;
        if (s.a > center.a)
            sampleSize = min(sampleSize, centerSize * 2.0); // behind this texel: no bleeding over a sharper one
        float m = smoothstep(r - 0.5, r + 0.5, sampleSize);
        sum += lerp(sum / count, s.rgb, m);
        count += 1.0;
        if (s.a < 0.0 && s.a < center.a)
            nearSpread = max(nearSpread, m * abs(s.a));
    }

    return float4(sum / count, nearSpread);
}

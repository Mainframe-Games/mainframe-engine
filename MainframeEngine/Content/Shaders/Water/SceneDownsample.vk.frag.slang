// A further level of the water scene copy (ADR 0173): the 2 × 2 texels of the previous level under each texel; the
// colour is their mean (refraction blur, the reflected colour at half resolution), the view depth their minimum, so a
// ray marched against a coarse level never steps through thin geometry. Paired with Post/Fullscreen.vk.vert.
[[vk::binding(0, 0)]] Sampler2D source; // the previous level (a one-level view)

[shader("fragment")]
float4 main(float4 fragCoord : SV_Position) : SV_Target0
{
    uint width, height;
    source.GetDimensions(width, height);
    int2 last = int2(width, height) - 1;
    int2 p = int2(fragCoord.xy) * 2;
    float4 a = source.Load(int3(min(p, last), 0));
    float4 b = source.Load(int3(min(p + int2(1, 0), last), 0));
    float4 c = source.Load(int3(min(p + int2(0, 1), last), 0));
    float4 d = source.Load(int3(min(p + int2(1, 1), last), 0));
    return float4((a.rgb + b.rgb + c.rgb + d.rgb) * 0.25, min(min(a.a, b.a), min(c.a, d.a)));
}

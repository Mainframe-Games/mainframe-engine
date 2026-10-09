// Light shafts (ADR 0160), passes 2 and 3: Mitchell's radial blur (GPU Gems 3 ch. 13). Each texel averages `taps`
// samples of the source along its ray towards the sun, `step` × (texel − sun) apart, the i-th weighted tapDecay^i. The
// fine pass (step density / n², decay^(16/n²)) then the coarse one (density / n, decay^(16/n)) give n² effective taps
// over `density` of the way to the sun. The sampler reads black past the image edges, so rays leaving the screen add
// nothing.
[[vk::binding(0, 0)]] Sampler2D source;

struct Shafts
{
    float2 sunUv;
    float  aspect;       // mask only
    float  radius;       // mask only
    float  luminanceCap; // mask only
    float  step;
    float  tapDecay;
    uint   taps;
};

[[vk::push_constant]] ConstantBuffer<Shafts> pc;

[shader("fragment")]
float4 main(float4 fragCoord : SV_Position) : SV_Target0
{
    uint w, h, levels;
    source.GetDimensions(0u, w, h, levels);
    float2 uv = fragCoord.xy / float2(float(w), float(h));
    float2 delta = (uv - pc.sunUv) * pc.step;

    float3 sum = float3(0.0);
    float weight = 1.0;
    for (uint i = 0u; i < pc.taps; i++)
    {
        sum += source.SampleLevel(uv - delta * float(i), 0.0).rgb * weight;
        weight *= pc.tapDecay;
    }

    return float4(sum / float(max(pc.taps, 1u)), 1.0);
}

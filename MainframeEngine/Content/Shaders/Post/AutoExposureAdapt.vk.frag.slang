// Auto exposure (ADR 0154): the 1 × 1 R32F adapted luminance. The measured average (2^mean log2 luminance) is clamped to
// [min, max] and approached from the previous frame's value by `blend` (1 − e^(−dt·speed); 1 snaps, the first frame).
// The tonemap and the glow's first level multiply by scale / this. Nothing is read back to the CPU.
[[vk::binding(0, 0)]] Sampler2D averageLog; // 1 × 1, R16F: mean log2 luminance
[[vk::binding(1, 0)]] Sampler2D previous;   // 1 × 1, R32F: last frame's adapted luminance

struct Adapt
{
    float minLuminance;
    float maxLuminance;
    float blend;
};

[[vk::push_constant]] ConstantBuffer<Adapt> pc;

[shader("fragment")]
float4 main(float4 fragCoord : SV_Position) : SV_Target0
{
    float measured = clamp(exp2(averageLog.Load(int3(0, 0, 0)).r), pc.minLuminance, pc.maxLuminance);
    float last = previous.Load(int3(0, 0, 0)).r;
    float adapted = (pc.blend >= 1.0 || !(last > 0.0)) ? measured : last + (measured - last) * pc.blend;
    return float4(adapted, 0.0, 0.0, 1.0);
}

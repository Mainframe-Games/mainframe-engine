// Auto exposure (ADR 0154), pass 1: the HDR scene → a 64 × 64 R16F image of log2 luminance. Each texel averages the
// log luminance of a 4 × 4 grid of scene texels spread over its footprint. AutoExposureReduce then averages down to 1 × 1.
[[vk::binding(0, 0)]] Sampler2D hdrScene;

static const float3 kLuminance = float3(0.2126, 0.7152, 0.0722);
static const float kTargetSize = 64.0;

[shader("fragment")]
float4 main(float4 fragCoord : SV_Position) : SV_Target0
{
    uint w, h, levels;
    hdrScene.GetDimensions(0u, w, h, levels);
    float2 scene = float2(float(w), float(h));
    float2 footprint = scene / kTargetSize;
    float2 origin = floor(fragCoord.xy) * footprint;

    float sum = 0.0;
    for (int y = 0; y < 4; y++)
    {
        for (int x = 0; x < 4; x++)
        {
            int2 p = int2(origin + (float2(float(x), float(y)) + 0.5) * footprint * 0.25);
            p = clamp(p, int2(0, 0), int2(int(w) - 1, int(h) - 1));
            float3 c = max(hdrScene.Load(int3(p, 0)).rgb, float3(0.0));
            sum += log2(max(dot(c, kLuminance), 1e-5));
        }
    }

    return float4(sum / 16.0, 0.0, 0.0, 1.0);
}

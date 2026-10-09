// Depth of field (ADR 0168), pass 3 at full resolution: blends the sharp HDR scene towards the half-resolution bokeh
// (bilinear) by each pixel's own circle of confusion, or by how far a blurred foreground spread over it, from half a
// pixel (sharp) to one and a half (fully the bokeh image). The result replaces the scene colour (CopyToSceneColor).
#include "dof.slang"

[[vk::binding(0, 0)]] Sampler2D hdrScene;          // point
[[vk::binding(1, 0)]] Sampler2D<float> sceneDepth; // point
[[vk::binding(2, 0)]] Sampler2D bokeh;             // DofGather's output, bilinear

[shader("fragment")]
float4 main(float4 fragCoord : SV_Position) : SV_Target0
{
    uint w, h, levels;
    hdrScene.GetDimensions(0u, w, h, levels);
    int2 pixel = int2(fragCoord.xy);
    float4 sharp = hdrScene.Load(int3(pixel, 0));
    float coc = abs(circleOfConfusion(sceneDepth.Load(int3(pixel, 0))));
    float4 blurred = bokeh.SampleLevel(fragCoord.xy / float2(float(w), float(h)), 0.0);
    float blend = smoothstep(0.5, 1.5, max(coc, blurred.a));
    return float4(lerp(sharp.rgb, blurred.rgb, blend), sharp.a);
}

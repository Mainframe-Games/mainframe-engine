// Default canvas item fragment stage: Godot's canvas.glsl without a fragment() function — COLOR = vertex colour (modulate)
// × texture, then × the canvas modulation (CanvasModulate) and the 2D point lights unless unshaded. Gamma-space values
// throughout, as Godot with hdr_2d off.
#include "canvas.slang"
#define CANVAS_LIGHT_SET 1
#include "canvas_lights.slang"

[[vk::binding(0, 0)]] Sampler2D colorTexture;

[shader("fragment")]
float4 main([[vk::location(0)]] float4 uvVertexInterp, [[vk::location(1)]] float4 colorInterp) : SV_Target0
{
    float4 color = colorInterp * colorTexture.Sample(uvVertexInterp.xy);
    float4 base = color;
    if ((canvas_flags() & CANVAS_FLAG_UNSHADED) == 0u)
    {
        color *= canvas_pc.canvasModulation;
        canvas_apply_lights(color, base, uvVertexInterp.zw);
    }
    if ((canvas_flags() & CANVAS_FLAG_PREMULTIPLY) != 0u)
        color.rgb *= color.a;
    return color;
}

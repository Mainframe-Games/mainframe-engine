#version 450
#extension GL_GOOGLE_include_directive : require

// Default canvas item fragment stage: Godot's canvas.glsl without a fragment() function — COLOR = vertex colour (modulate)
// × texture, then × the canvas modulation (CanvasModulate) and the 2D point lights unless unshaded. Gamma-space values
// throughout, as Godot with hdr_2d off.
#include "canvas.glsl"
#define CANVAS_LIGHT_SET 1
#include "canvas_lights.glsl"

layout(location = 0) in vec4 uvVertexInterp;
layout(location = 1) in vec4 colorInterp;

layout(set = 0, binding = 0) uniform sampler2D colorTexture;

layout(location = 0) out vec4 fragColor;

void main()
{
    vec4 color = colorInterp * texture(colorTexture, uvVertexInterp.xy);
    vec4 base = color;
    if ((canvas_flags() & CANVAS_FLAG_UNSHADED) == 0u)
    {
        color *= canvas_pc.canvasModulation;
        canvas_apply_lights(color, base, uvVertexInterp.zw);
    }
    if ((canvas_flags() & CANVAS_FLAG_PREMULTIPLY) != 0u)
        color.rgb *= color.a;
    fragColor = color;
}

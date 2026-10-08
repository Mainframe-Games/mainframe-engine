// Canvas items (docs/design/canvas.md, ADR 0111): the push constants shared by the canvas renderer's default shaders and
// every canvas_item shader, mirroring Godot 4.7's canvas.glsl data (world/canvas/screen transforms, modulation).
//
// Spaces: vertices arrive in item-local space when the material's shader has vertex() (MODEL_MATRIX = the item's global
// transform), else already transformed to target pixels (MODEL_MATRIX = CANVAS_MATRIX = identity). CANVAS_MATRIX maps
// canvas space to target pixels (camera / layer × stretch); SCREEN maps target pixels to Vulkan NDC (y down).
//
// The matrix functions list the GLSL columns as constructor arguments (row-major layout): native code transforms with
// mul(v, M), and translated canvas shaders (compiled with -allow-glsl) with GLSL's M * v; both give Godot's result.
#pragma once

struct CanvasPush
{
    float4 modelAxes;      // MODEL_MATRIX x axis (xy), y axis (zw)
    float4 modelOrigin;    // MODEL_MATRIX origin (xy), TEXTURE_PIXEL_SIZE (zw)
    float4 canvasAxes;     // CANVAS_MATRIX x axis (xy), y axis (zw)
    float4 canvasOrigin;   // CANVAS_MATRIX origin (xy), TIME (z), flags (w)
    float4 screen;         // pixels -> NDC: scale (xy), offset (zw)
    float4 canvasModulation;
    float4 lights;         // x: the frame lights affecting this item (bit mask, uint bits); see canvas_lights.slang
};

[[vk::push_constant]] ConstantBuffer<CanvasPush> canvas_pc;

#define CANVAS_FLAG_UNSHADED 1u
#define CANVAS_FLAG_LIGHT_ONLY 2u
#define CANVAS_FLAG_PREMULTIPLY 4u   // output premultiplied colour (a clip-children group's atop blend)

float4x4 canvas_model_matrix()
{
    return float4x4(float4(canvas_pc.modelAxes.xy, 0.0, 0.0), float4(canvas_pc.modelAxes.zw, 0.0, 0.0), float4(0.0, 0.0, 1.0, 0.0),
                    float4(canvas_pc.modelOrigin.xy, 0.0, 1.0));
}

float4x4 canvas_canvas_matrix()
{
    return float4x4(float4(canvas_pc.canvasAxes.xy, 0.0, 0.0), float4(canvas_pc.canvasAxes.zw, 0.0, 0.0), float4(0.0, 0.0, 1.0, 0.0),
                    float4(canvas_pc.canvasOrigin.xy, 0.0, 1.0));
}

float4x4 canvas_screen_matrix()
{
    return float4x4(float4(canvas_pc.screen.x, 0.0, 0.0, 0.0), float4(0.0, canvas_pc.screen.y, 0.0, 0.0), float4(0.0, 0.0, 1.0, 0.0),
                    float4(canvas_pc.screen.zw, 0.0, 1.0));
}

uint canvas_flags()
{
    return asuint(canvas_pc.canvasOrigin.w);
}

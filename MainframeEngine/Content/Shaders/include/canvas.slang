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

// GLSL's one-argument matrix constructors for translated canvas shaders (CanvasShaderCompiler rewrites matN(x) to these):
// a scalar builds a diagonal, a matrix copies its upper-left block with the identity elsewhere. Slang fills every
// element from a scalar and rejects resizing. Rows here are GLSL columns (row-major layout), so m[i] is column i.
float2x2 glsl_mat2(float s) { return float2x2(s, 0.0, 0.0, s); }
float2x2 glsl_mat2(float2x2 m) { return m; }
float2x2 glsl_mat2(float3x3 m) { return float2x2(m[0].xy, m[1].xy); }
float2x2 glsl_mat2(float4x4 m) { return float2x2(m[0].xy, m[1].xy); }
float3x3 glsl_mat3(float s) { return float3x3(s, 0.0, 0.0, 0.0, s, 0.0, 0.0, 0.0, s); }
float3x3 glsl_mat3(float2x2 m) { return float3x3(float3(m[0], 0.0), float3(m[1], 0.0), float3(0.0, 0.0, 1.0)); }
float3x3 glsl_mat3(float3x3 m) { return m; }
float3x3 glsl_mat3(float4x4 m) { return float3x3(m[0].xyz, m[1].xyz, m[2].xyz); }
float4x4 glsl_mat4(float s) { return float4x4(s, 0.0, 0.0, 0.0, 0.0, s, 0.0, 0.0, 0.0, 0.0, s, 0.0, 0.0, 0.0, 0.0, s); }
float4x4 glsl_mat4(float2x2 m)
{
    return float4x4(float4(m[0], 0.0, 0.0), float4(m[1], 0.0, 0.0), float4(0.0, 0.0, 1.0, 0.0), float4(0.0, 0.0, 0.0, 1.0));
}
float4x4 glsl_mat4(float3x3 m) { return float4x4(float4(m[0], 0.0), float4(m[1], 0.0), float4(m[2], 0.0), float4(0.0, 0.0, 0.0, 1.0)); }
float4x4 glsl_mat4(float4x4 m) { return m; }

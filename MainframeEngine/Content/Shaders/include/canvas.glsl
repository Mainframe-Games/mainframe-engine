// Canvas items (docs/design/canvas.md, ADR 0111): the push constants shared by the canvas renderer's default shaders and
// every canvas_item shader, mirroring Godot 4.7's canvas.glsl data (world/canvas/screen transforms, modulation).
//
// Spaces: vertices arrive in item-local space when the material's shader has vertex() (MODEL_MATRIX = the item's global
// transform), else already transformed to target pixels (MODEL_MATRIX = CANVAS_MATRIX = identity). CANVAS_MATRIX maps
// canvas space to target pixels (camera / layer × stretch); SCREEN maps target pixels to Vulkan NDC (y down).

layout(push_constant) uniform CanvasPush {
    vec4 modelAxes;      // MODEL_MATRIX x axis (xy), y axis (zw)
    vec4 modelOrigin;    // MODEL_MATRIX origin (xy), TEXTURE_PIXEL_SIZE (zw)
    vec4 canvasAxes;     // CANVAS_MATRIX x axis (xy), y axis (zw)
    vec4 canvasOrigin;   // CANVAS_MATRIX origin (xy), TIME (z), flags (w)
    vec4 screen;         // pixels -> NDC: scale (xy), offset (zw)
    vec4 canvasModulation;
} canvas_pc;

#define CANVAS_FLAG_UNSHADED 1u
#define CANVAS_FLAG_LIGHT_ONLY 2u

mat4 canvas_model_matrix()
{
    return mat4(vec4(canvas_pc.modelAxes.xy, 0.0, 0.0), vec4(canvas_pc.modelAxes.zw, 0.0, 0.0), vec4(0.0, 0.0, 1.0, 0.0),
                vec4(canvas_pc.modelOrigin.xy, 0.0, 1.0));
}

mat4 canvas_canvas_matrix()
{
    return mat4(vec4(canvas_pc.canvasAxes.xy, 0.0, 0.0), vec4(canvas_pc.canvasAxes.zw, 0.0, 0.0), vec4(0.0, 0.0, 1.0, 0.0),
                vec4(canvas_pc.canvasOrigin.xy, 0.0, 1.0));
}

mat4 canvas_screen_matrix()
{
    return mat4(vec4(canvas_pc.screen.x, 0.0, 0.0, 0.0), vec4(0.0, canvas_pc.screen.y, 0.0, 0.0), vec4(0.0, 0.0, 1.0, 0.0),
                vec4(canvas_pc.screen.zw, 0.0, 1.0));
}

uint canvas_flags()
{
    return floatBitsToUint(canvas_pc.canvasOrigin.w);
}

// Default canvas item vertex stage: Godot's canvas.glsl vertex path without a vertex() function.
#include "canvas.slang"

struct VsOut
{
    float4 position : SV_Position;
    [[vk::location(0)]] float4 uvVertexInterp; // uv (xy), vertex in target pixels (zw)
    [[vk::location(1)]] float4 colorInterp;
};

[shader("vertex")]
VsOut main([[vk::location(0)]] float2 inPosition, [[vk::location(1)]] float2 inUv, [[vk::location(2)]] float4 inColor)
{
    float2 pixel = mul(float4(inPosition, 0.0, 1.0), canvas_model_matrix()).xy;
    VsOut o;
    o.colorInterp = inColor;
    pixel = mul(float4(pixel, 0.0, 1.0), canvas_canvas_matrix()).xy;
    o.uvVertexInterp = float4(inUv, pixel);
    o.position = mul(float4(pixel, 0.0, 1.0), canvas_screen_matrix());
    return o;
}

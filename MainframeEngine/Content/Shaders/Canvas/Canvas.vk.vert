#version 450
#extension GL_GOOGLE_include_directive : require

// Default canvas item vertex stage: Godot's canvas.glsl vertex path without a vertex() function.
#include "canvas.glsl"

layout(location = 0) in vec2 inPosition;
layout(location = 1) in vec2 inUv;
layout(location = 2) in vec4 inColor;

layout(location = 0) out vec4 uvVertexInterp; // uv (xy), vertex in target pixels (zw)
layout(location = 1) out vec4 colorInterp;

void main()
{
    vec2 vertex = (canvas_model_matrix() * vec4(inPosition, 0.0, 1.0)).xy;
    colorInterp = inColor;
    vertex = (canvas_canvas_matrix() * vec4(vertex, 0.0, 1.0)).xy;
    uvVertexInterp = vec4(inUv, vertex);
    gl_Position = canvas_screen_matrix() * vec4(vertex, 0.0, 1.0);
}

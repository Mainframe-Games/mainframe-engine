// Per-frame shared set 0, binding 0: the camera (FrameContext / FrameData in C#, std140, 368 bytes).
// Binding 1 of the same set is the lights UBO (lights.glsl, default LIGHTS_SET 0 / LIGHTS_BINDING 1).
#ifndef MF_FRAME_GLSL
#define MF_FRAME_GLSL

#include "common.glsl"

layout(set = 0, binding = 0) uniform FrameData {
    mat4 view;
    mat4 projection;
    mat4 viewProjection;
    mat4 invProjection;
    mat4 invViewRotation; // inverse view without translation: camera-relative rays (sky)
    vec4 cameraPosition;  // xyz = world position
    vec4 viewport;        // width, height, 1/width, 1/height (pixels)
    vec4 clip;            // near, far, time (s), exposure
} frame;

// Linear view depth from a [0, 1] depth-buffer value (perspective projection).
float linearizeDepth(float depth)
{
    float near = frame.clip.x;
    float far  = frame.clip.y;
    return near * far / (far - depth * (far - near));
}

#endif

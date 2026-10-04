#version 450
#extension GL_GOOGLE_include_directive : require

// Box3d / Quad (ShapeBase): set 0 VP (vertex), set 1 lights, set 2 shadows. The shared per-frame sets
// (0 = frame + lights, 1 = shadows) are adopted by the node-side shape rewrite (M3 materials).
#define LIGHTS_SET 1
#define LIGHTS_BINDING 0
#define SHADOW_SET 2
#include "shadows.glsl"
#include "lights.glsl"

layout(location = 0) in vec3 inWorldPos;
layout(location = 1) in vec3 inNormal;

layout(location = 0) out vec4 outColor;

layout(push_constant) uniform PushConstants {
    mat4 model;
    vec4 color;
} pc;

void main()
{
    // The shape colour is sRGB-authored (System.Drawing.Color); light it in linear space.
    vec3 N = normalize(inNormal);
    outColor = vec4(shadeLights(srgbToLinear(pc.color.xyz), N, inWorldPos), 1.0);
}

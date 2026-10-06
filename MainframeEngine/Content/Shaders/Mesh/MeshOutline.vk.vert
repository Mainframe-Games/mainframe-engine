#version 450
#extension GL_GOOGLE_include_directive : require

// OutlineMaterial3D (ADR 0132): Godot's inverted-hull outline shader. Same inputs and outputs as Mesh.vk.vert; the
// vertex is pushed along its clip-space normal by the material's width in pixels, and the pipeline culls front faces,
// so the surface drawn before hides all but a rim of constant screen width. Shaded by Mesh.vk.frag (unshaded path).
#include "frame.glsl"
#include "material.glsl"

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec2 inUV;
// System.Numerics row-vector matrix, uploaded as-is: its rows are the GLSL columns.
layout(location = 3) in vec4 inModel0;
layout(location = 4) in vec4 inModel1;
layout(location = 5) in vec4 inModel2;
layout(location = 6) in vec4 inModel3;
layout(location = 7) in uint inObjectId;

layout(location = 0) out vec3 outWorldPos;
layout(location = 1) out vec3 outNormal;
layout(location = 2) out vec2 outUV;
layout(location = 3) flat out uint outObjectId;

void main()
{
    mat4 model = mat4(inModel0, inModel1, inModel2, inModel3);
    vec4 worldPos = model * vec4(inPosition, 1.0);
    vec4 clipPosition = frame.viewProjection * worldPos;

    // As Godot's shader: clip_normal = mat3(PROJECTION) * (mat3(MODELVIEW) * NORMAL), then
    // offset = normalize(clip_normal.xy) / VIEWPORT_SIZE * w * width * 2 (2/size clip units per pixel).
    vec3 clipNormal = mat3(frame.projection) * (mat3(frame.view) * (mat3(model) * inNormal));
    float width = uintBitsToFloat(material.flags.w);
    if (dot(clipNormal.xy, clipNormal.xy) > 0.0)
        clipPosition.xy += normalize(clipNormal.xy) * frame.viewport.zw * clipPosition.w * width * 2.0;

    outWorldPos = worldPos.xyz;
    outNormal = mat3(model) * inNormal; // unused: the outline is unshaded
    outUV = inUV;
    outObjectId = inObjectId;
    gl_Position = clipPosition;
}

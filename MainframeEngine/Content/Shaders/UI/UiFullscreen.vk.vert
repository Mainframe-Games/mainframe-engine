#version 450

// Fullscreen triangle for UI layer composition and filters. The viewport selects the target rectangle; texture
// coordinates span [0, 1] over the viewport and are mapped by uvOffset + uv · uvScale (sub-rectangle copies, the blur's
// downscale/upscale, the drop-shadow offset).
layout(push_constant) uniform FullscreenPush {
    vec2 uvOffset;
    vec2 uvScale;
} pc;

layout(location = 0) out vec2 outTexCoord;

void main()
{
    vec2 uv = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2); // (0,0) (2,0) (0,2)
    gl_Position = vec4(uv * 2.0 - 1.0, 0.0, 1.0);
    outTexCoord = pc.uvOffset + uv * pc.uvScale;
}

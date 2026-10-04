#version 450

// RmlUi geometry (game UI, docs/design/game-ui.md). Vertices are Rml::Vertex: position in context pixels, premultiplied
// sRGB-encoded RGBA8 colour, texture coordinate. The transform is projection (pixels → clip, y down, no flip) times the
// element's RCSS transform, computed on the CPU.
layout(location = 0) in vec2 inPosition;
layout(location = 1) in vec4 inColor;
layout(location = 2) in vec2 inTexCoord;

layout(push_constant) uniform UiPush {
    mat4 transform;
    vec2 translate;
    uint textureFlags; // read by the fragment shader
} pc;

layout(location = 0) out vec2 outTexCoord;
layout(location = 1) out vec4 outColor;

void main()
{
    outTexCoord = inTexCoord;
    outColor = inColor;
    gl_Position = pc.transform * vec4(inPosition + pc.translate, 0.0, 1.0);
}

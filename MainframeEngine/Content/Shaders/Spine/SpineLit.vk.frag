#version 450
#extension GL_GOOGLE_include_directive : require

// SpineRenderer: set 0 frame (camera + lights), set 1 shadows, set 2 atlas page texture.
#include "frame.glsl"
#include "shadows.glsl"
#include "lights.glsl"

layout(location = 0) in vec2 inUV;
layout(location = 1) in vec4 inColor;
layout(location = 2) in vec3 inWorldPos;
layout(location = 3) in vec3 inNormal;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform sampler2D uTexture;

void main()
{
    vec4 texColor = inColor * texture(uTexture, inUV);
    if (texColor.a < 0.01) discard;

    vec3 N = normalize(inNormal);
    outColor = vec4(shadeLights(texColor.rgb, N, inWorldPos), texColor.a);
}

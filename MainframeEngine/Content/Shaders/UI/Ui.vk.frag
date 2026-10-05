#version 450
#extension GL_GOOGLE_include_directive : require

// RmlUi geometry: vertex colour × texture, both premultiplied and sRGB-encoded (the UI is authored in sRGB and blended
// in sRGB space, like a browser). Untextured geometry binds a 1×1 white texture. Engine textures (engine://) may be
// stored linear (sRGB formats, HDR targets) or with straight alpha; textureFlags converts them on the fly.
#include "common.glsl"

layout(location = 0) in vec2 inTexCoord;
layout(location = 1) in vec4 inColor;

layout(set = 0, binding = 0) uniform sampler2D uiTexture;

layout(push_constant) uniform UiPush {
    layout(offset = 72) uint textureFlags;
} pc;

layout(location = 0) out vec4 outColor;

const uint kEncodeSrgb = 1u;  // the sampler returns linear colour: encode to sRGB
const uint kPremultiply = 2u; // the texture stores straight alpha
const uint kDepthToGray = 4u; // a depth map: .r as grey, alpha 1

void main()
{
    vec4 texel = texture(uiTexture, inTexCoord);
    if ((pc.textureFlags & kDepthToGray) != 0u)
        texel = vec4(texel.rrr, 1.0);
    if ((pc.textureFlags & kEncodeSrgb) != 0u)
        texel.rgb = linearToSrgb(clamp(texel.rgb, 0.0, 1.0));
    if ((pc.textureFlags & kPremultiply) != 0u)
        texel.rgb *= texel.a;
    outColor = inColor * texel;
}

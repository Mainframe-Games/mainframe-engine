#version 450
#extension GL_GOOGLE_include_directive : require

// SpineRenderer: set 0 frame (camera + lights), set 1 shadows, set 2 atlas page texture.
// Output is premultiplied (blend One, OneMinusSrcAlpha); premultiplied alpha is handled here, once.
#include "frame.glsl"
#include "shadows.glsl"
#include "lights.glsl"

// True for premultiplied-alpha atlases (atlas page "pma: true"), which are bound as UNORM images.
layout(constant_id = 0) const bool kPremultipliedTexture = false;

layout(location = 0) in vec2 inUV;
layout(location = 1) in vec4 inColor;   // straight, sRGB-authored slot/skeleton tint
layout(location = 2) in vec3 inWorldPos;
layout(location = 3) in vec3 inNormal;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform sampler2D uTexture;

void main()
{
    vec4 tex = texture(uTexture, inUV);
    if (kPremultipliedTexture)
    {
        // Multiplied in sRGB space by the exporter: un-premultiply, decode, re-premultiply in linear.
        tex.rgb = tex.a > 0.0 ? srgbToLinear(tex.rgb / tex.a) * tex.a : vec3(0.0);
    }
    else
    {
        tex.rgb *= tex.a; // sRGB image, already decoded by the sampler; straight alpha → premultiplied
    }

    float alpha = tex.a * inColor.a;
    if (alpha < 0.01) discard;

    vec3 base = tex.rgb * srgbToLinear(inColor.rgb) * inColor.a; // premultiplied linear albedo
    vec3 N = normalize(inNormal);
    outColor = vec4(shadeLights(base, N, inWorldPos), alpha);
}

#version 450
#extension GL_GOOGLE_include_directive : require

// StandardMaterial3D (Blinn-Phong): set 0 frame + lights, set 1 shadows, set 2 material. Linear HDR output.
#include "frame.glsl"
#include "shadows.glsl"
#include "lights.glsl"
#include "material.glsl"

// AlphaMode of the pipeline: opaque pipelines never discard (early depth test stays on).
layout(constant_id = 0) const int kAlphaMode = kAlphaOpaque;

layout(location = 0) in vec3 inWorldPos;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec2 inUV;
layout(location = 3) flat in uint inObjectId;

layout(location = 0) out vec4 outColor;

void main()
{
    vec2 uv = materialUv(inUV);
    vec4 albedo = materialAlbedo(uv);
    if (kAlphaMode == kAlphaCutout && albedo.a < material.params.z)
        discard;

    vec3 N = normalize(inNormal);
    if (material.flags.z != 0u && !gl_FrontFacing)
        N = -N; // double-sided: light the back face with its own normal

    vec3 color;
    if (material.flags.y != 0u)
    {
        color = albedo.rgb; // unshaded
    }
    else
    {
        N = materialNormal(N, inWorldPos, uv);
        color = shadeLightsBlinnPhong(albedo.rgb, N, inWorldPos, material.params.x, material.params.y);
    }

    color += materialEmission(uv);
    outColor = vec4(color, kAlphaMode == kAlphaBlend ? albedo.a : 1.0);
}

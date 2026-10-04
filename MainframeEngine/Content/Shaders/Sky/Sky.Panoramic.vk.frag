#version 450
#extension GL_GOOGLE_include_directive : require

#include "sky.glsl"

layout(location = 0) in  vec2 inUV; // NDC
layout(location = 0) out vec4 outColor;

layout(set = 1, binding = 0) uniform sampler2D panoramic;

void main()
{
    vec3 dir = skyRay(inUV);

    // Equirectangular UV mapping.
    // u: longitude mapped to [0,1], seam at atan2(0,-1) = ±PI
    // v: latitude mapped to [0,1], 0 = zenith (top of image), 1 = nadir
    float u = atan(dir.z, dir.x) / (2.0 * PI) + 0.5;
    float v = 0.5 - asin(clamp(dir.y, -1.0, 1.0)) / PI;

    outColor = texture(panoramic, vec2(u, v));
}

#version 450
#extension GL_GOOGLE_include_directive : require

#include "frame.glsl"

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec4 inColor;
layout(location = 2) in vec3 inOther; // the other end of this vertex's line

layout(location = 0) out vec4 fragColor;

// x/y extent of the clip volume used here, in multiples of w: a guard band around the viewport, so the
// rasterizer still clips the last bit to the exact edges.
const float kGuardBand = 1.25;

// Liang–Barsky against one plane (signed distances dp, dq; inside >= 0), narrowing [t0, t1] along p → q.
void clipPlane(float dp, float dq, inout float t0, inout float t1)
{
    if (dp < 0.0 && dq < 0.0) { t0 = 1.0; t1 = 0.0; return; }
    if (dp < 0.0) t0 = max(t0, dp / (dp - dq));
    else if (dq < 0.0) t1 = min(t1, dp / (dp - dq));
}

bool lexLess(vec3 u, vec3 v)
{
    return u.x != v.x ? u.x < v.x : (u.y != v.y ? u.y < v.y : u.z < v.z);
}

void main() {
    fragColor = inColor;

    // Clip this line to the view volume here rather than in the rasterizer: grid lines run beside and behind the
    // camera, and their near-plane intersections project ~1e5 pixels off-screen, which some rasterizers
    // (lavapipe) turn into stray fragments. Both ends of a line run the same arithmetic on the same ordered
    // segment p → q, so they agree on the clipped span (and on rejecting it).
    bool first = lexLess(inPosition, inOther);
    vec4 p = frame.viewProjection * vec4(first ? inPosition : inOther, 1.0);
    vec4 q = frame.viewProjection * vec4(first ? inOther : inPosition, 1.0);
    float t0 = 0.0, t1 = 1.0;
    clipPlane(kGuardBand * p.w + p.x, kGuardBand * q.w + q.x, t0, t1);
    clipPlane(kGuardBand * p.w - p.x, kGuardBand * q.w - q.x, t0, t1);
    clipPlane(kGuardBand * p.w + p.y, kGuardBand * q.w + q.y, t0, t1);
    clipPlane(kGuardBand * p.w - p.y, kGuardBand * q.w - q.y, t0, t1);
    clipPlane(p.z, q.z, t0, t1);             // near (Vulkan depth 0)
    clipPlane(p.w - p.z, q.w - q.z, t0, t1); // far

    // A rejected line puts both ends behind the near plane, so nothing is drawn.
    gl_Position = t0 <= t1 ? mix(p, q, first ? t0 : t1) : vec4(0.0, 0.0, -1.0, 1.0);
}
